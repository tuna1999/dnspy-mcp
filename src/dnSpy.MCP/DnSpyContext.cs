using System;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Scripting;
using Microsoft.VisualStudio.Composition;

namespace dnSpy.MCP {
    /// <summary>
    /// Static context that bridges MEF services to MCP tools.
    /// Services that may not be available via direct MEF import (e.g. TabService, TreeView)
    /// are resolved lazily via IServiceLocator at first access.
    /// </summary>
    public static class DnSpyContext {
        internal static IDsDocumentService? DocumentService { get; private set; }
        internal static IDecompilerService? DecompilerService { get; private set; }
        internal static TheExtension? Extension { get; set; }

        static IOutputService? _outputService;
        static IOutputTextPane? _outputPane;
        static readonly Guid OutputPaneGuid = new("D1E5F6A0-3B7C-4A8D-9E2F-1B3D5C7A9E0F");
        static IServiceLocator? _serviceLocator;

        internal static IOutputTextPane? OutputPane => _outputPane;

        /// <summary>
        /// Lazily resolves IDocumentTabService via IServiceLocator (safe to call from any thread).
        /// </summary>
        public static IDocumentTabService? TabService {
            get {
                if (_tabService == null) {
                    lock (_tabServiceLock) {
                        if (_tabService == null && _serviceLocator != null)
                            _tabService = _serviceLocator.TryResolve<IDocumentTabService>();
                    }
                }
                return _tabService;
            }
        }

        /// <summary>
        /// Lazily resolves IDocumentTreeView via IServiceLocator (safe to call from any thread).
        /// </summary>
        public static IDocumentTreeView? TreeView {
            get {
                if (_treeView == null) {
                    lock (_treeViewLock) {
                        if (_treeView == null && _serviceLocator != null)
                            _treeView = _serviceLocator.TryResolve<IDocumentTreeView>();
                    }
                }
                return _treeView;
            }
        }

        static readonly object _tabServiceLock = new();
        static readonly object _treeViewLock = new();
        static IDocumentTreeView? _treeView;
        static IDocumentTabService? _tabService;

        /// <summary>
        /// Initialize from extension OnEvent
        /// </summary>
        internal static void Initialize(
            IDsDocumentService docSvc,
            IDecompilerService decompSvc,
            IOutputService? outputSvc,
            IServiceLocator? serviceLocator) {
            DocumentService = docSvc;
            DecompilerService = decompSvc;
            _outputService = outputSvc;
            _serviceLocator = serviceLocator;
            // Clear cached lazy refs so next access re-resolves
            _tabService = null;
            _treeView = null;
        }

        /// <summary>
        /// Lazily creates the MCP output pane on the UI thread
        /// </summary>
        internal static void EnsureOutputPane() {
            if (_outputPane != null || _outputService == null) return;
            try {
                _outputPane = _outputService.Create(OutputPaneGuid, "MCP Server", (string?)null);
            }
            catch { }
        }
    }
}