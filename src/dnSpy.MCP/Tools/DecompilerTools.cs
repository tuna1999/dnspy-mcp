using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Helpers;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.MCP.Tools {
    public static class DecompilerTools {
        [Description("Decompile a specific method to C# code. Format: 'Namespace.Class::Method' or just 'Method'")]
        public static string DecompileMethod(string methodFullnameOrToken) {
            var documentService = DnSpyContext.DocumentService;
            var decompilerService = DnSpyContext.DecompilerService;
            if (documentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var decompiler = decompilerService.Decompiler;
            MethodDef? method = null;

            // Try token
            if (methodFullnameOrToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                var hex = methodFullnameOrToken.Substring(2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int token))
                    method = resolver.ResolveMethodByToken(token);
            }
            else if (int.TryParse(methodFullnameOrToken, out int plainToken))
                method = resolver.ResolveMethodByToken(plainToken);

            // Try full name
            if (method == null)
                method = resolver.ResolveMethod(methodFullnameOrToken);

            // Try name search
            if (method == null) {
                var name = methodFullnameOrToken.Contains('.')
                    ? methodFullnameOrToken.Split('.').Last()
                    : methodFullnameOrToken;
                foreach (var mod in resolver.GetAllModules()) {
                    foreach (var type in mod.GetTypes()) {
                        foreach (var m in type.Methods) {
                            if (UTF8String.Equals(m.Name, name))
                                method = m;
                        }
                        if (method != null) break;
                    }
                    if (method != null) break;
                }
            }

            if (method == null)
                return $"Method not found: {methodFullnameOrToken}";

            try {
                var output = new TextDecompilerOutput();
                decompiler.Decompile(method, output, new DecompilationContext());
                return output.ToString();
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile an entire type (all members) to C# code.")]
        public static string DecompileType(string typeFullname) {
            var documentService = DnSpyContext.DocumentService;
            var decompilerService = DnSpyContext.DecompilerService;
            if (documentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var decompiler = decompilerService.Decompiler;
            var type = resolver.ResolveType(typeFullname);

            if (type == null)
                return $"Type not found: {typeFullname}";

            try {
                var output = new TextDecompilerOutput();
                decompiler.Decompile(type, output, new DecompilationContext());
                return output.ToString();
            }
            catch (Exception ex) {
                return $"Decompilation failed: {ex.Message}";
            }
        }

        [Description("Decompile the entire assembly. May be slow for large assemblies.")]
        public static string DecompileAssembly() {
            var documentService = DnSpyContext.DocumentService;
            var decompilerService = DnSpyContext.DecompilerService;
            if (documentService == null || decompilerService == null)
                return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var decompiler = decompilerService.Decompiler;
            var sb = new StringBuilder();
            var count = 0;

            foreach (var mod in resolver.GetAllModules()) {
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
