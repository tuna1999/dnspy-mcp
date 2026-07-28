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
using Microsoft.VisualStudio.Composition;

namespace dnSpy.MCP {
    [ExportExtension]
    sealed class TheExtension : IExtension, IMcpExtension {
        private McpServerHost? _serverHost;

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
                    DnSpyContext.Extension = this;
                    if (DocumentService != null && DecompilerService != null) {
                        DnSpyContext.Initialize(DocumentService, DecompilerService, OutputService, ServiceLocator);
                        DnSpyContext.EnsureOutputPane();
                        LogServiceLocatorStatus();
                    }
                    McpLogger.Info("MCP extension loaded");
                    if (Settings?.AutoStart == true)
                        StartServer();
                    break;

                case ExtensionEvent.AppExit:
                    _serverHost?.Dispose();
                    break;
            }
        }

        void LogServiceLocatorStatus() {
            var sl = ServiceLocator;
            McpLogger.Info($"ServiceLocator: {(sl != null ? "available" : "null")}");
            if (sl != null) {
                var tabSvc = DnSpyContext.TabService;
                var treeView = DnSpyContext.TreeView;
                McpLogger.Info($"TabService: {(tabSvc != null ? "resolved" : "null")}");
                McpLogger.Info($"TreeView: {(treeView != null ? "resolved" : "null")}");
            }
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

            // Build McpContext + ToolRegistry with real adapters backed by DnSpyContext.
            // WpfUIThreadScheduler is shared between loader, log sink, and notifier so all
            // UI-thread marshaling goes through one dispatcher path.
            var uiScheduler = new WpfUIThreadScheduler();

            // Resolve tab service + tree view lazily (same pattern as DnSpyContext, but here
            // we pass them by value into the notifier so tests can substitute stubs).
            var treeView = ServiceLocator?.TryResolve<IDocumentTreeView>();
            var tabService = ServiceLocator?.TryResolve<IDocumentTabService>();

            var ctx = new McpContext(
                new DnSpyAssemblyLoader(DocumentService!, uiScheduler),
                new DnSpyDecompilerSourceProvider(DecompilerService!.Decompiler),
                uiScheduler,
                new DnSpyLogSink(uiScheduler, DnSpyContext.OutputPane),
                new DnSpyTreeRefreshNotifier(treeView, tabService, DocumentService, uiScheduler));
            var registry = new ToolRegistry(ctx, typeof(McpContext).Assembly);
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
