using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Helpers;
using dnSpy.MCP.Mcp;

namespace dnSpy.MCP.Tools {
    public static class DecompilerTools {
        [Description("Decompile a specific method to C# code. Format: 'Namespace.Class::Method' or just 'Method'")]
        public static string DecompileMethod(string methodFullNameOrToken) {
            var decompilerService = DnSpyContext.DecompilerService;
            if (DnSpyContext.DocumentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var method = DnSpyContext.Resolver.ResolveMethodFlexible(methodFullNameOrToken);

            if (method == null)
                return $"Method not found: {methodFullNameOrToken}";

            try {
                var output = new TextDecompilerOutput();
                decompilerService.Decompiler.Decompile(method, output, new DecompilationContext());
                return output.ToString();
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile an entire type (all members) to C# code.")]
        public static string DecompileType(string typeFullName) {
            var decompilerService = DnSpyContext.DecompilerService;
            if (DnSpyContext.DocumentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var type = DnSpyContext.Resolver.ResolveType(typeFullName);

            if (type == null)
                return $"Type not found: {typeFullName}";

            try {
                var output = new TextDecompilerOutput();
                decompilerService.Decompiler.Decompile(type, output, new DecompilationContext());
                return output.ToString();
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile the entire assembly. May be slow for large assemblies.")]
        public static string DecompileAssembly() {
            var decompilerService = DnSpyContext.DecompilerService;
            if (DnSpyContext.DocumentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var decompiler = decompilerService.Decompiler;
            var sb = new StringBuilder();
            var count = 0;

            foreach (var mod in DnSpyContext.Resolver.GetAllModules()) {
                foreach (var type in mod.GetTypes()) {
                    if (type.Name.String.StartsWith("<"))
                        continue;
                    try {
                        var output = new TextDecompilerOutput();
                        decompiler.Decompile(type, output, new DecompilationContext());
                        sb.AppendLine(output.ToString());
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
                        McpLogger.Warn($"Skipped type '{type.FullName}' during assembly overview: {ex.Message}");
                    }
                }
                if (count > 10) break;
            }

            return count == 0 ? "No types found." : sb.ToString();
        }
    }
}
