using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Scripting;
using dnSpy.MCP.Mcp;

namespace dnSpy.MCP {
    [ExportExtension]
    sealed class TheExtension : IExtension {
        private McpServerHost? _serverHost;
        private readonly McpServerOptions _options = new();

        [Import]
        public IDsDocumentService? DocumentService { get; set; }

        [Import]
        public IDecompilerService? DecompilerService { get; set; }

        [Import]
        public IOutputService? OutputService { get; set; }

        [Import]
        public IServiceLocator? ServiceLocator { get; set; }

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

            if (errors.Count > 0) {
                McpLogger.Error($"Cannot start: {string.Join(", ", errors)}");
                return;
            }

            _serverHost = new McpServerHost(_options, DocumentService, DecompilerService);
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
        public int ServerPort => _options.Port;
    }
}
