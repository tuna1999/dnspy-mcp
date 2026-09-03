using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Tools {
    /// <summary>
    /// Extension-only tool class providing the two UI-backed MCP tools
    /// (<c>get_selected_node</c>, <c>refresh_u_i</c>) plus internal helpers used by
    /// <see cref="dnSpy.MCP.Adapters.DnSpyTreeRefreshNotifier"/> for namespace rename
    /// restructuring.
    ///
    /// Unlike the Core tool classes (which receive <see cref="dnSpy.MCP.Core.Mcp.McpContext"/>
    /// via constructor injection), this class stays static because it is the only Extension-only
    /// tool set and it needs direct WPF dispatcher access. <see cref="Initialize"/> is called
    /// once by <c>TheExtension.OnEvent(AppLoaded)</c> to populate the tree view and tab service
    /// resolved from the MEF <c>IServiceLocator</c>.
    /// </summary>
    public static class TreeViewTools {
        static IDocumentTreeView? _treeView;
        static IDocumentTabService? _tabService;

        /// <summary>
        /// Populate the static tree view / tab service references. Called once during
        /// <c>TheExtension.OnEvent(AppLoaded)</c>. Safe to call with nulls (tools will report
        /// "not available" at call time).
        /// </summary>
        internal static void Initialize(IDocumentTreeView? treeView, IDocumentTabService? tabService) {
            _treeView = treeView;
            _tabService = tabService;
        }

        static T? RunOnUIThread<T>(Func<T> action) where T : class? {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return null;

            if (dispatcher.CheckAccess())
                return action();

            return dispatcher.Invoke(action, DispatcherPriority.Normal);
        }

        internal static void RunOnUIThread(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action, DispatcherPriority.Normal);
        }

        [Description("Gets the currently selected node in the dnSpy tree view. Returns node type, name, and path, or empty if nothing is selected.")]
        public static string GetSelectedNode() {
            var treeView = _treeView;
            if (treeView == null)
                return "Error: TreeView not available.";

            try {
                var result = RunOnUIThread(() => {
                    var selected = treeView.TreeView?.SelectedItem;
                    if (selected == null)
                        return (string?)null;

                    if (selected is DocumentTreeNodeData docNode) {
                        var name = docNode.NodePathName.Name.ToString();
                        if (docNode is AssemblyDocumentNode)
                            return $"Assembly: {name}";
                        if (docNode is NamespaceNode)
                            return $"Namespace: {name}";
                        if (docNode is TypeNode)
                            return $"Type: {name}";
                        if (docNode is MethodNode)
                            return $"Method: {name}";
                        if (docNode is FieldNode)
                            return $"Field: {name}";
                        if (docNode is PropertyNode)
                            return $"Property: {name}";
                        if (docNode is EventNode)
                            return $"Event: {name}";
                        if (docNode is AssemblyReferenceNode)
                            return $"AssemblyRef: {name}";
                        return $"{docNode.GetType().Name}: {name}";
                    }

                    return selected.ToString();
                });

                return result ?? "";
            }
            catch (Exception ex) {
                return $"Error accessing tree view: {ex.Message}";
            }
        }

        /// <summary>
        /// Dispatcher-aware tree refresh for use by rename/patch tools.
        /// </summary>
        internal static void RefreshTreeViewOnUIThread() {
            var treeView = _treeView;
            if (treeView == null) {
                McpLogger.Warn("RefreshTreeView: IDocumentTreeView not resolved");
                return;
            }

            try {
                RunOnUIThread(() => {
                    var tv = treeView.TreeView;
                    if (tv == null) return;

                    tv.RefreshAllNodes();
                });
            }
            catch (Exception ex) {
                McpLogger.Error(ex, "RefreshTreeView failed");
            }
        }

        internal static void UpdateNamespaceNode(string assembly, string oldNamespace, string newNamespace) {
            var treeView = _treeView;
            if (treeView == null) {
                McpLogger.Warn("UpdateNamespaceNode: IDocumentTreeView not resolved");
                return;
            }

            try {
                RunOnUIThread(() => {
                    var tv = treeView.TreeView;
                    if (tv == null) return;

                    foreach (var asmTreeNode in tv.Root.Children) {
                        if (asmTreeNode.Data is not AssemblyDocumentNode asmNode) continue;
                        if (!string.Equals(asmNode.Document.ModuleDef?.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                            continue;

                        asmTreeNode.EnsureChildrenLoaded();
                        var modNode = asmTreeNode.DataChildren.OfType<ModuleDocumentNode>().FirstOrDefault();
                        if (modNode == null) continue;

                        modNode.TreeNode.EnsureChildrenLoaded();
                        var oldNsNode = modNode.FindNode(oldNamespace);
                        if (oldNsNode == null) continue;

                        oldNsNode.TreeNode.EnsureChildrenLoaded();
                        var existingNewNs = modNode.FindNode(newNamespace);

                        if (existingNewNs != null) {
                            var typeTreeNodes = oldNsNode.TreeNode.Children.ToList();
                            oldNsNode.TreeNode.Children.Clear();
                            foreach (var typeTreeNode in typeTreeNodes)
                                existingNewNs.TreeNode.AddChild(typeTreeNode);
                            oldNsNode.TreeNode.Parent?.Children.Remove(oldNsNode.TreeNode);
                            existingNewNs.TreeNode.RefreshUI();
                        }
                        else {
                            oldNsNode.Name = newNamespace;
                            oldNsNode.TreeNode.RefreshUI();
                        }

                        _tabService?.RefreshModifiedDocument(modNode.Document);
                    }
                });
            }
            catch (Exception ex) {
                McpLogger.Error(ex, "UpdateNamespaceNode failed");
            }
        }

        [Description("Refreshes all open document tabs in dnSpy to reflect any assembly modifications made by MCP tools.")]
        public static string RefreshUI() {
            var treeView = _treeView;
            if (treeView == null)
                return "Error: TreeView not available.";

            try {
                RunOnUIThread(() => {
                    treeView.TreeView?.RefreshAllNodes();
                });
            }
            catch (Exception ex) {
                return $"Error refreshing UI: {ex.Message}";
            }

            return "UI refreshed.";
        }
    }
}
