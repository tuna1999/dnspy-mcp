using System;
using System.Linq;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Tools;

namespace dnSpy.MCP.Adapters;

/// <summary>
/// Extension tree refresh notifier: refreshes the TreeView and all open document tabs so
/// decompiled views reflect in-memory mutations. Delegates namespace-specific tree
/// restructuring to <see cref="TreeViewTools"/>. All UI access is marshaled via
/// <see cref="IUIThreadScheduler"/>.
/// </summary>
internal sealed class DnSpyTreeRefreshNotifier : ITreeRefreshNotifier {
    private readonly IDocumentTreeView? _treeView;
    private readonly IDocumentTabService? _tabService;
    private readonly IDsDocumentService? _documentService;
    private readonly IUIThreadScheduler _ui;

    public DnSpyTreeRefreshNotifier(
        IDocumentTreeView? treeView,
        IDocumentTabService? tabService,
        IDsDocumentService? documentService,
        IUIThreadScheduler ui) {
        _treeView = treeView;
        _tabService = tabService;
        _documentService = documentService;
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>
    /// Refreshes tree nodes and every open tab's decompiled view. Addresses the forward-ref
    /// parked finding from Task 2.4: <c>RefreshAfterRename</c> originally lost the
    /// <c>tabSvc.RefreshModifiedDocument(doc)</c> call, leaving stale decompiled views until
    /// the user navigated away/back.
    /// </summary>
    public void RefreshAll() {
        if (_treeView is not null) {
            _ui.Invoke(() => _treeView.TreeView?.RefreshAllNodes());
        }

        if (_tabService is null || _documentService is null) return;

        // IDocumentTabService.RefreshModifiedDocument(IDsDocument) internally finds all tabs
        // that use the given document and refreshes them. Iterating the document list is more
        // robust than iterating SortedTabs (whose IDocumentTab does not expose Document
        // directly) and covers tabs that may be in background tab groups.
        _ui.Invoke(() => {
            foreach (var doc in _documentService.GetDocuments()) {
                try {
                    _tabService.RefreshModifiedDocument(doc);
                }
                catch (Exception ex) {
                    // A single doc failure should not block refresh of the rest
                    // (e.g. document being unloaded concurrently).
                    System.Diagnostics.Debug.WriteLine(
                        $"MCP [TAB REFRESH ERROR] doc={doc?.Filename}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Delegates namespace-specific tree restructuring to <see cref="TreeViewTools"/>, which
    /// handles moving type nodes under the new namespace node (or renaming in place) and
    /// refreshing the affected module's open tab.
    /// </summary>
    public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) {
        if (_treeView is null) return;
        // TreeViewTools.UpdateNamespaceNode reads its static treeView/tabService fields
        // (populated by TheExtension.OnEvent) and already marshals to the UI thread and
        // refreshes the affected module's tab.
        TreeViewTools.UpdateNamespaceNode(assembly, oldNamespace, newNamespace);
    }
}
