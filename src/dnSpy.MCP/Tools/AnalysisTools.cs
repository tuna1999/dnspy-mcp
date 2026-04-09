using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;
using dnSpy.MCP.Helpers;
using System.ComponentModel;

namespace dnSpy.MCP.Tools {
    public static class AnalysisTools {
        [Description("Get raw IL instructions of a method body. Useful for low-level analysis.")]
        public static string GetMethodIl(string methodFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var method = resolver.ResolveMethod(methodFullname);

            if (method == null) return $"Method not found: {methodFullname}";
            if (method.Body == null) return $"No body: {method.FullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"IL for {method.FullName}");
            sb.AppendLine($"MaxStack: {method.Body.MaxStack}, Locals: {method.Body.Variables.Count}, ExceptionHandlers: {method.Body.ExceptionHandlers.Count}");
            sb.AppendLine();

            foreach (var instr in method.Body.Instructions)
                sb.AppendLine($"  IL_{instr.Offset:X4}: {instr.OpCode.Name} {FormatOperand(instr.Operand)}");

            if (method.Body.ExceptionHandlers.Count > 0) {
                sb.AppendLine();
                sb.AppendLine("Exception Handlers:");
                foreach (var eh in method.Body.ExceptionHandlers)
                    sb.AppendLine($"  Try: IL_{eh.TryStart?.Offset:X4}, Handler: IL_{eh.HandlerStart?.Offset:X4}, Type: {eh.HandlerType}");
            }

            return sb.ToString();
        }

        [Description("Get detailed method signature: parameters, return type, attributes, and flags.")]
        public static string GetMethodSignatures(string methodFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var method = resolver.ResolveMethod(methodFullname);

            if (method == null) return $"Method not found: {methodFullname}";

            var sb = new StringBuilder();
            sb.AppendLine($"Method: {method.FullName}");
            sb.AppendLine($"Token: 0x{method.MDToken.Raw:X8}");
            sb.AppendLine($"Return: {method.ReturnType?.FullName ?? "void"}");
            sb.AppendLine();
            sb.AppendLine("Parameters:");
            foreach (var param in method.Parameters)
                sb.AppendLine($"  [{param.Index}] {param.Type?.FullName} {param.Name}");
            sb.AppendLine();
            sb.AppendLine("Flags:");
            sb.AppendLine($"  Public={method.IsPublic}, Static={method.IsStatic}, Virtual={method.IsVirtual}, Abstract={method.IsAbstract}");

            if (method.HasGenericParameters) {
                sb.AppendLine("Generic Parameters:");
                foreach (var gp in method.GenericParameters)
                    sb.AppendLine($"  {gp.Name}");
            }

            if (method.ImplMap != null)
                sb.AppendLine($"P/Invoke: {method.ImplMap.Module?.Name}!{method.ImplMap.Name}");

            return sb.ToString();
        }

        [Description("Get type hierarchy: base types, implemented interfaces, and inheritance chain.")]
        public static string GetTypeHierarchy(string typeFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var type = resolver.ResolveType(typeFullname);

            if (type == null) return $"Type not found: {typeFullname}";

            var sb = new StringBuilder();
            sb.AppendLine($"Type: {type.FullName}");
            sb.AppendLine($"Token: 0x{type.MDToken.Raw:X8}");
            sb.AppendLine();

            sb.AppendLine("Inheritance:");
            var current = type;
            int depth = 0;
            while (current != null && depth < 20) {
                sb.AppendLine($"  {new string(' ', depth * 2)}{current.FullName}");
                current = current.BaseType?.ResolveTypeDef();
                depth++;
            }

            sb.AppendLine();
            sb.AppendLine("Interfaces:");
            foreach (var iface in type.Interfaces)
                sb.AppendLine($"  {iface.Interface.FullName}");

            sb.AppendLine();
            sb.AppendLine($"Members: Fields={type.Fields.Count}, Methods={type.Methods.Count}, Properties={type.Properties.Count}");
            sb.AppendLine($"Flags: Public={type.IsPublic}, Abstract={type.IsAbstract}, Sealed={type.IsSealed}, Interface={type.IsInterface}");

            return sb.ToString();
        }

        [Description("Get raw IL bytes of a method body for pattern matching.")]
        public static string GetMethodBody(string methodFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            var resolver = new MethodResolver(documentService);
            var method = resolver.ResolveMethod(methodFullname);

            if (method == null) return $"Method not found: {methodFullname}";
            if (method.Body == null) return $"No body: {method.FullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"Body of {method.FullName}");
            sb.AppendLine($"MaxStack: {method.Body.MaxStack}, InitLocals: {method.Body.InitLocals}");
            sb.AppendLine();

            foreach (var instr in method.Body.Instructions)
                sb.AppendLine($"  IL_{instr.Offset:X4}: {instr.OpCode.Name} {FormatOperand(instr.Operand)}");

            return sb.ToString();
        }

        private static string FormatOperand(object? operand) {
            if (operand == null) return "";
            if (operand is IMethod mr) return $"{mr.DeclaringType?.FullName}::{mr.Name}";
            if (operand is IField fr) return $"{fr.DeclaringType?.FullName}::{fr.Name}";
            if (operand is ITypeDefOrRef tdr) return tdr.FullName;
            if (operand is string s) return $"\"{s}\"";
            if (operand is Parameter p) return p.Name ?? $"param{p.Index}";
            if (operand is Local l) return l.Type?.FullName ?? $"local{l.Index}";
            if (operand is Instruction[] targets) return string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}"));
            if (operand is Instruction target) return $"IL_{target.Offset:X4}";
            return operand.ToString() ?? "";
        }
    }
}
