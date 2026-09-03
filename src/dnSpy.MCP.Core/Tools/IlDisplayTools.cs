using System.ComponentModel;
using System.Text;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class IlDisplayTools {
        private readonly McpContext _ctx;
        public IlDisplayTools(McpContext ctx) => _ctx = ctx;

        [Description("Returns formatted IL opcodes for a method with line numbers. Input accepts full name, token, or partial method name.")]
        public string GetIlOpcodesFormatted(string methodFullNameOrToken) {
            if (string.IsNullOrWhiteSpace(methodFullNameOrToken))
                return "Error: methodFullNameOrToken is required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var method = _ctx.Resolver.ResolveMethodFlexible(methodFullNameOrToken);
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
