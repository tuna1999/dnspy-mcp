using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Helpers;

namespace dnSpy.MCP.Tools {
    public static class DecompilerTools {
        [Description("Decompile a specific method to C# code. Format: 'Namespace.Class::Method' or just 'Method'")]
        public static string DecompileMethod(string methodFullnameOrToken) {
            var decompilerService = DnSpyContext.DecompilerService;
            if (DnSpyContext.DocumentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var method = DnSpyContext.Resolver.ResolveMethodFlexible(methodFullnameOrToken);

            if (method == null)
                return $"Method not found: {methodFullnameOrToken}";

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
        public static string DecompileType(string typeFullname) {
            var decompilerService = DnSpyContext.DecompilerService;
            if (DnSpyContext.DocumentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var type = DnSpyContext.Resolver.ResolveType(typeFullname);

            if (type == null)
                return $"Type not found: {typeFullname}";

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
                    catch { }
                }
                if (count > 10) break;
            }

            return count == 0 ? "No types found." : sb.ToString();
        }
    }
}
