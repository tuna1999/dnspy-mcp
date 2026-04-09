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
    public static class XrefTools {
        [Description("Find all methods that reference the given method or field.")]
        public static string GetXrefsTo(string memberFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var resolver = new MethodResolver(documentService);
            var parts = memberFullname.Split(new[] { "::" }, StringSplitOptions.None);
            var targetName = parts.Length > 1 ? parts[parts.Length - 1] : memberFullname;
            var refs = new List<(TypeDef type, MethodDef caller, Instruction instr)>();

            foreach (var mod in resolver.GetAllModules()) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (method.Body == null) continue;
                        foreach (var instr in method.Body.Instructions) {
                            if (instr.Operand is IMethod mr && mr.Name == targetName) {
                                refs.Add((type, method, instr));
                            }
                            else if (instr.Operand is IField fr && fr.Name == targetName) {
                                refs.Add((type, method, instr));
                            }
                        }
                    }
                }
            }

            if (refs.Count == 0)
                return $"No references to: {memberFullname}";

            var sb = new StringBuilder();
            sb.AppendLine($"References to '{memberFullname}' ({refs.Count}):");
            foreach (var (type, caller, instr) in refs) {
                sb.AppendLine($"  {type.FullName}::{caller.Name}");
                sb.AppendLine($"    IL: 0x{instr.Offset:X4} | {instr.OpCode.Name} {instr.Operand}");
            }
            return sb.ToString();
        }

        [Description("Get all methods/fields called by a method.")]
        public static string GetCallees(string methodFullname) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var resolver = new MethodResolver(documentService);
            var method = resolver.ResolveMethod(methodFullname);

            if (method == null)
                return $"Method not found: {methodFullname}";
            if (method.Body == null)
                return $"Method has no body: {method.FullName}";

            var callees = new HashSet<string>();
            var fieldRefs = new HashSet<string>();

            foreach (var instr in method.Body.Instructions) {
                if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt || instr.OpCode == OpCodes.Newobj) {
                    if (instr.Operand is IMethod mr) {
                        callees.Add($"{mr.DeclaringType?.FullName}.{mr.Name}");
                    }
                }
                else if (instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Stfld ||
                         instr.OpCode == OpCodes.Ldsfld || instr.OpCode == OpCodes.Stsfld) {
                    if (instr.Operand is IField fr) {
                        fieldRefs.Add($"{fr.DeclaringType?.FullName}::{fr.Name}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Callees of '{method.FullName}':");
            foreach (var c in callees) sb.AppendLine($"  [call] {c}");
            foreach (var f in fieldRefs) sb.AppendLine($"  [field] {f}");
            sb.AppendLine($"\nTotal: {callees.Count} calls, {fieldRefs.Count} field refs");
            return sb.ToString();
        }

        [Description("Shorthand for get_xrefs_to.")]
        public static string GetCallers(string methodFullname) => GetXrefsTo(methodFullname);
    }
}
