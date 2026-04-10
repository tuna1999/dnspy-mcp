using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.MCP.Tools {
    public static class TreeViewTools {
        static T? RunOnUIThread<T>(Func<T> action) where T : class {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return null;

            if (dispatcher.CheckAccess())
                return action();

            return dispatcher.Invoke(action, DispatcherPriority.Normal);
        }

        static void RunOnUIThread(Action action) {
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
            var treeView = DnSpyContext.TreeView;
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
        /// Called on background MCP thread; must marshal to UI thread.
        /// </summary>
        internal static void RefreshTreeViewOnUIThread() {
            var treeView = DnSpyContext.TreeView;
            if (treeView == null)
                return;

            try {
                RunOnUIThread(() => {
                    treeView.TreeView?.RefreshAllNodes();
                });
            }
            catch {
                // best-effort
            }
        }

        [Description("Refreshes all open document tabs in dnSpy to reflect any assembly modifications made by MCP tools.")]
        public static string RefreshUI() {
            var treeView = DnSpyContext.TreeView;
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