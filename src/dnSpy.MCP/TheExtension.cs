using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Threading.Tasks;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Scripting;
using dnSpy.MCP.Adapters;
using dnSpy.MCP.Core.Adapters;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Settings;
using dnSpy.MCP.Tools;

namespace dnSpy.MCP {
    [ExportExtension]
    sealed class TheExtension : IExtension, IMcpExtension {
        /// <summary>
        /// Static accessor for menu commands. Replaces the prior
        /// <c>DnSpyContext.Extension</c> bridge — menu items live in the same Extension
        /// project and only need to reach the running server host, so a static field on the
        /// concrete entry point is sufficient and avoids resurrecting the static context class.
        /// Set in <see cref="OnEvent"/> on <see cref="ExtensionEvent.AppLoaded"/>.
        /// </summary>
        internal static IMcpExtension? Instance { get; private set; }

        private McpServerHost? _serverHost;
        private IOutputTextPane? _outputPane;

        // All imports are optional (AllowDefault): a missing export on some dnSpy build must
        // degrade the corresponding feature, never reject the whole extension part. MEF
        // ignores NRT annotations — without AllowDefault a nullable property is still a
        // REQUIRED import and VS-MEF silently drops TheExtension from the catalog.
        // StartServer() reports missing required services explicitly.
        [Import(AllowDefault = true)]
        public IDsDocumentService? DocumentService { get; set; }

        [Import(AllowDefault = true)]
        public IDecompilerService? DecompilerService { get; set; }

        [Import(AllowDefault = true)]
        public IOutputService? OutputService { get; set; }

        [Import(AllowDefault = true)]
        public IServiceLocator? ServiceLocator { get; set; }

        [Import(AllowDefault = true)]
        public McpSettings? Settings { get; set; }

        public ExtensionInfo ExtensionInfo => new ExtensionInfo {
            ShortDescription = "MCP Server for AI-assisted analysis",
        };

        public IEnumerable<string> MergedResourceDictionaries {
            get { yield break; }
        }

        public void OnEvent(ExtensionEvent @event, object? obj) {
            switch (@event) {
                case ExtensionEvent.AppLoaded:
                    Instance = this;
                    EnsureOutputPane();
                    // Mirror every McpLogger line (server lifecycle, tool calls, errors)
                    // into the Output Pane — v1.6.2 behavior, restored host-agnostically:
                    // Core emits LineLogged, only the UI-owning host subscribes.
                    McpLogger.LineLogged += OnLogLine;
                    // Populate TreeViewTools' static refs so the Extension-only UI tools
                    // (get_selected_node, refresh_u_i) and the namespace rename helper can
                    // reach the WPF TreeView without going through DnSpyContext.
                    var treeView = ServiceLocator?.TryResolve<IDocumentTreeView>();
                    var tabService = ServiceLocator?.TryResolve<IDocumentTabService>();
                    TreeViewTools.Initialize(treeView, tabService);

                    LogServiceLocatorStatus(treeView, tabService);
                    McpLogger.Info("MCP extension loaded");
                    if (Settings?.AutoStart == true)
                        StartServer();
                    break;

                case ExtensionEvent.AppExit:
                    McpLogger.LineLogged -= OnLogLine;
                    _serverHost?.Dispose();
                    break;
            }
        }

        void EnsureOutputPane() {
            if (_outputPane != null || OutputService == null) return;
            var paneGuid = new Guid("D1E5F6A0-3B7C-4A8D-9E2F-1B3D5C7A9E0F");
            try {
                _outputPane = OutputService.Create(paneGuid, "MCP Server", string.Empty);
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"MCP: Failed to create output pane: {ex.Message}");
            }
        }

        /// <summary>
        /// McpLogger.LineLogged handler: renders a recorded line to the Output Pane.
        /// May fire from any thread (server/tools run on background threads) — marshal
        /// to the WPF UI thread before touching the pane. Never throws into the logger.
        /// </summary>
        void OnLogLine(McpLogger.Level level, string line) {
            var pane = _outputPane;
            if (pane is null) return;
            var color = level switch {
                McpLogger.Level.Warn => dnSpy.Contracts.Text.BoxedTextColor.DebugLogStepFiltering,
                McpLogger.Level.Error => dnSpy.Contracts.Text.BoxedTextColor.DebugLogExceptionUnhandled,
                _ => dnSpy.Contracts.Text.BoxedTextColor.DebugLogExtensionMessage
            };
            void Write() {
                try { pane.WriteLine(color, line); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MCP [OUTPUT ERROR]: {ex.Message}"); }
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Write();
            else dispatcher.InvokeAsync(Write);
        }

        void LogServiceLocatorStatus(IDocumentTreeView? treeView, IDocumentTabService? tabService) {
            var sl = ServiceLocator;
            McpLogger.Info($"ServiceLocator: {(sl != null ? "available" : "null")}");
            McpLogger.Info($"TabService: {(tabService != null ? "resolved" : "null")}");
            McpLogger.Info($"TreeView: {(treeView != null ? "resolved" : "null")}");
        }

        public void StartServer() {
            if (_serverHost != null && _serverHost.IsRunning)
                return;

            // Replace-and-dispose a previously stopped host so its CancellationTokenSource
            // and listener state are released across stop/start cycles.
            _serverHost?.Dispose();
            _serverHost = null;

            var errors = new List<string>();
            if (DocumentService == null) errors.Add("DocumentService is null");
            if (DecompilerService == null) errors.Add("DecompilerService is null");
            if (Settings == null) errors.Add("Settings is null");

            if (errors.Count > 0) {
                McpLogger.Error($"Cannot start: {string.Join(", ", errors)}");
                return;
            }

            // Build McpContext + ToolRegistry with real adapters.
            // WpfUIThreadScheduler is shared between loader, log sink, and notifier so all
            // UI-thread marshaling goes through one dispatcher path.
            var uiScheduler = new WpfUIThreadScheduler();

            // Re-resolve tab service + tree view for the notifier (they may have been resolved
            // above for TreeViewTools already; TryResolve is cheap on a resolved service).
            var treeView = ServiceLocator?.TryResolve<IDocumentTreeView>();
            var tabService = ServiceLocator?.TryResolve<IDocumentTabService>();

            var ctx = new McpContext(
                new DnSpyAssemblyLoader(DocumentService!, uiScheduler),
                new DnSpyDecompilerSourceProvider(DecompilerService!.Decompiler),
                uiScheduler,
                new DnSpyLogSink(),
                new DnSpyTreeRefreshNotifier(treeView, tabService, DocumentService, uiScheduler));
            // Core assembly holds the 36 instance tools; the Extension assembly holds
            // Extension-only static tools (TreeViewTools: get_selected_node, refresh_u_i).
            var registry = new ToolRegistry(ctx, typeof(McpContext).Assembly, typeof(TheExtension).Assembly);
            var host = new McpServerHost(Settings!, registry);
            _serverHost = host;
            // Capture the local: the background task must start exactly this instance.
            // Rereading the mutable _serverHost field here would let a fast Start→Stop→Start
            // cycle point two tasks at the same (newer) host and never start the first.
            Task.Run(async () => {
                try {
                    await host.StartAsync();
                }
                catch (Exception ex) {
                    McpLogger.Error(ex, "Server startup");
                }
            });
        }

        public void StopServer() {
            _serverHost?.Stop();
        }

        /// <summary>
        /// Renders a line to the "MCP Server" Output Pane (visible in the dnSpy UI).
        /// Rendering only — this MUST NOT call <see cref="McpLogger"/>: re-logging rendered
        /// lines feeds GetRecent() output back into the log (duplicate entries, unbounded
        /// file growth, legitimate history eviction). Log events belong to callers;
        /// this method is the UI side of "display", not "record".
        /// </summary>
        public void WriteToOutputPane(string message) {
            EnsureOutputPane();
            _outputPane?.WriteLine(dnSpy.Contracts.Text.BoxedTextColor.DebugLogExtensionMessage, message);
        }

        /// <summary>Clears the Output Pane (paired with the "Clear Log" menu item,
        /// matching v1.6.2 behavior where ClearLog also cleared the pane).</summary>
        public void ClearOutputPane() {
            try {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess()) _outputPane?.Clear();
                else dispatcher.InvokeAsync(() => _outputPane?.Clear());
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"MCP [OUTPUT ERROR]: {ex.Message}");
            }
        }

        public bool IsServerRunning => _serverHost?.IsRunning ?? false;
        public int ServerPort => Settings?.Port ?? 0;
    }
}
