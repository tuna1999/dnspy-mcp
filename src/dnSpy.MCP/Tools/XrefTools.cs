using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
    public static class XrefTools {
        [Description("Find all methods that reference the given method or field. Format: 'Namespace.Class::Member' (scoped to that type) or plain 'Member' (matches any type).")]
        public static string GetXrefsTo(string memberFullName, string? assembly = null) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            // Parse 'Namespace.Class::Member' into a (declaringType, memberName) scope.
            // When a declaring type is given, references are scoped to that type only —
            // previously the type prefix was parsed then discarded, so 'A::Foo' matched
            // every 'Foo' across all types and assemblies.
            var parts = memberFullName.Split(new[] { "::" }, StringSplitOptions.None);
            string? declaringTypeFilter = null;
            string targetName;
            if (parts.Length > 1) {
                declaringTypeFilter = parts[0].Trim();
                targetName = parts[parts.Length - 1].Trim();
            }
            else {
                targetName = memberFullName.Trim();
            }
            var refs = new List<(TypeDef type, MethodDef caller, Instruction instr)>();

            foreach (var mod in DnSpyContext.Resolver.GetModules(assembly)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (method.Body == null) continue;
                        foreach (var instr in method.Body.Instructions) {
                            if (instr.Operand is IMethod mr && mr.Name == targetName
                                && MatchesDeclaringType(mr.DeclaringType, declaringTypeFilter)) {
                                refs.Add((type, method, instr));
                            }
                            else if (instr.Operand is IField fr && fr.Name == targetName
                                && MatchesDeclaringType(fr.DeclaringType, declaringTypeFilter)) {
                                refs.Add((type, method, instr));
                            }
                        }
                    }
                }
            }

            if (refs.Count == 0)
                return $"No references to: {memberFullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"References to '{memberFullName}' ({refs.Count}):");
            foreach (var (type, caller, instr) in refs) {
                sb.AppendLine($"  {type.FullName}::{caller.Name}");
                sb.AppendLine($"    IL: 0x{instr.Offset:X4} | {instr.OpCode.Name} {instr.Operand}");
            }
            return sb.ToString();
        }

        [Description("Get all methods/fields called by a method.")]
        public static string GetCallees(string methodFullName) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var method = DnSpyContext.Resolver.ResolveMethodFlexible(methodFullName);

            if (method == null)
                return $"Method not found: {methodFullName}";
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

        /// <summary>
        /// Returns true when <paramref name="filter"/> is null (simple-name-only lookup),
        /// or when the reference's declaring type full name matches the filter exactly.
        /// </summary>
        static bool MatchesDeclaringType(IType? declaringType, string? filter) {
            if (filter == null) return true;
            if (declaringType == null) return false;
            // FullName is the dotted 'Namespace.Class' form; fall back to Name for nested/edge cases.
            var full = declaringType.FullName?.ToString();
            var name = declaringType.Name?.ToString();
            return string.Equals(full, filter, StringComparison.Ordinal)
                || string.Equals(name, filter, StringComparison.Ordinal);
        }
    }
}
