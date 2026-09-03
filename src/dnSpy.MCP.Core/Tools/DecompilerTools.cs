using System;
using System.ComponentModel;
using System.Text;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class DecompilerTools {
        private readonly McpContext _ctx;
        public DecompilerTools(McpContext ctx) => _ctx = ctx;

        [Description("Decompile a specific method to C# code. Format: 'Namespace.Class::Method' or just 'Method'")]
        public string DecompileMethod(string methodFullNameOrToken) {
            if (string.IsNullOrWhiteSpace(methodFullNameOrToken))
                return "Error: methodFullNameOrToken is required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var method = _ctx.Resolver.ResolveMethodFlexible(methodFullNameOrToken);

            if (method == null)
                return $"Method not found: {methodFullNameOrToken}";

            try {
                return _ctx.SourceDecompiler.DecompileMethod(method);
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile an entire type (all members) to C# code.")]
        public string DecompileType(string typeFullName) {
            if (string.IsNullOrWhiteSpace(typeFullName))
                return "Error: typeFullName is required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var type = _ctx.Resolver.ResolveType(typeFullName);

            if (type == null)
                return $"Type not found: {typeFullName}";

            try {
                return _ctx.SourceDecompiler.DecompileType(type);
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile the entire assembly. May be slow for large assemblies.")]
        public string DecompileAssembly() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var sb = new StringBuilder();
            var count = 0;

            foreach (var mod in _ctx.Resolver.GetAllModules()) {
                foreach (var type in mod.GetTypes()) {
                    if (type.Name.String.StartsWith("<"))
                        continue;
                    try {
                        sb.AppendLine(_ctx.SourceDecompiler.DecompileType(type));
                        count++;
                        if (count > 10) {
                            sb.AppendLine($"\n... (stopped at {count} types for brevity)");
                            break;
                        }
                    }
                    catch (Exception ex) {
                        // Some types (compiler-generated async state machines,
                        // anonymous types, etc.) fail to decompile cleanly.
                        // Skip them so the overview still returns useful output.
                        _ctx.Log.Warn($"Skipped type '{type.FullName}' during assembly overview: {ex.Message}");
                    }
                }
                if (count > 10) break;
            }

            return count == 0 ? "No types found." : sb.ToString();
        }
    }
}
