using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
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
using Microsoft.VisualStudio.Composition;

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

        [Import]
        public IDsDocumentService? DocumentService { get; set; }

        [Import]
        public IDecompilerService? DecompilerService { get; set; }

        [Import]
        public IOutputService? OutputService { get; set; }

        [Import]
        public IServiceLocator? ServiceLocator { get; set; }

        [Import]
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

        void LogServiceLocatorStatus(IDocumentTreeView? treeView, IDocumentTabService? tabService) {
            var sl = ServiceLocator;
            McpLogger.Info($"ServiceLocator: {(sl != null ? "available" : "null")}");
            McpLogger.Info($"TabService: {(tabService != null ? "resolved" : "null")}");
            McpLogger.Info($"TreeView: {(treeView != null ? "resolved" : "null")}");
        }

        public void StartServer() {
            if (_serverHost != null && _serverHost.IsRunning)
                return;

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
                new DnSpyLogSink(uiScheduler, _outputPane),
                new DnSpyTreeRefreshNotifier(treeView, tabService, DocumentService, uiScheduler));
            // Core assembly holds the 36 instance tools; the Extension assembly holds
            // Extension-only static tools (TreeViewTools: get_selected_node, refresh_u_i).
            var registry = new ToolRegistry(ctx, typeof(McpContext).Assembly, typeof(TheExtension).Assembly);
            _serverHost = new McpServerHost(Settings!, registry);
            Task.Run(async () => {
                try {
                    await _serverHost.StartAsync();
                }
                catch (Exception ex) {
                    McpLogger.Error(ex, "Server startup");
                }
            });
        }

        public void StopServer() {
            _serverHost?.Stop();
        }

        public bool IsServerRunning => _serverHost?.IsRunning ?? false;
        public int ServerPort => Settings?.Port ?? 0;
    }
}
