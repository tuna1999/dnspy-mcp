using System;
using System.ComponentModel;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.MCP.Tools {
    public static class NamespaceTools {
        [Description("List all types that have no explicit namespace (global namespace).")]
        public static string GetGlobalNamespaces() {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var sb = new StringBuilder();
            int count = 0;

            foreach (var doc in DnSpyContext.DocumentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
                    foreach (var type in mod.GetTypes()) {
                        if (string.IsNullOrEmpty(type.Namespace)) {
                            var fullName = type.FullName?.ToString();
                            if (!string.IsNullOrEmpty(fullName)) {
                                sb.AppendLine(fullName);
                                count++;
                            }
                        }
                    }
                }
            }

            return count == 0
                ? "No types in global namespace."
                : $"Types in global namespace ({count}):\n{sb}";
        }
    }
}