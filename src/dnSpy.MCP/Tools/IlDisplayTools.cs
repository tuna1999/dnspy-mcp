using System.ComponentModel;
using System.Text;
using dnSpy.MCP.Helpers;

namespace dnSpy.MCP.Tools {
    public static class IlDisplayTools {
        [Description("Returns formatted IL opcodes for a method with line numbers. Input accepts full name, token, or partial method name.")]
        public static string GetIlOpcodesFormatted(string methodFullNameOrToken) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var method = DnSpyContext.Resolver.ResolveMethodFlexible(methodFullNameOrToken);
            if (method == null)
                return $"Method not found: {methodFullNameOrToken}";

            if (method.Body == null)
                return $"Method has no body: {method.FullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"// IL for {method.DeclaringType?.FullName}::{method.Name}");
            sb.AppendLine("// #   Offset  OpCode            Operand");
            sb.AppendLine("// --------------------------------------------------------------");

            for (int i = 0; i < method.Body.Instructions.Count; i++) {
                var ins = method.Body.Instructions[i];
                var operand = ins.Operand?.ToString() ?? "";
                sb.AppendLine($"{i,3}  {ins.Offset:X4}    {ins.OpCode.Name,-16} {operand}");
            }

            return sb.ToString();
        }
    }
}
