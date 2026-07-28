using System.ComponentModel;
using System.Linq;
using System.Text;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class NamespaceTools {
        private readonly McpContext _ctx;
        public NamespaceTools(McpContext ctx) => _ctx = ctx;

        [Description("List all types that have no explicit namespace (global namespace).")]
        public string GetGlobalNamespaces() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var sb = new StringBuilder();
            int count = 0;

            foreach (var mod in _ctx.AssemblyLoader.GetDocuments().Select(l => l.Module)) {
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

            return count == 0
                ? "No types in global namespace."
                : $"Types in global namespace ({count}):\n{sb}";
        }
    }
}
