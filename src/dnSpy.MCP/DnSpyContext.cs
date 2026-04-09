using System;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Output;
using Microsoft.VisualStudio.Composition;

namespace dnSpy.MCP {
    /// <summary>
    /// Static context that bridges MEF services to MCP tools
    /// </summary>
    public static class DnSpyContext {
        internal static IDsDocumentService? DocumentService { get; private set; }
        internal static IDecompilerService? DecompilerService { get; private set; }
        internal static TheExtension? Extension { get; set; }

        static IOutputService? _outputService;
        static IOutputTextPane? _outputPane;
        static readonly Guid OutputPaneGuid = new("D1E5F6A0-3B7C-4A8D-9E2F-1B3D5C7A9E0F");

        internal static IOutputTextPane? OutputPane => _outputPane;

        /// <summary>
        /// Initialize from extension OnEvent
        /// </summary>
        internal static void Initialize(IDsDocumentService docSvc, IDecompilerService decompSvc, IOutputService? outputSvc = null) {
            DocumentService = docSvc;
            DecompilerService = decompSvc;
            if (outputSvc != null)
                _outputService = outputSvc;
        }

        /// <summary>
        /// Lazily creates the MCP output pane on the UI thread
        /// </summary>
        internal static void EnsureOutputPane() {
            if (_outputPane != null || _outputService == null) return;
            try {
                // Must be called from UI thread - caller should use Dispatcher
                _outputPane = _outputService.Create(OutputPaneGuid, "MCP Server", (string?)null);
            }
            catch { }
        }
    }
}
