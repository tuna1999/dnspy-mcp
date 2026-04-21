using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
    public static class SearchTools {
        [Description("Search for types by name pattern. Use 'regex:' prefix for regex.")]
        public static string SearchTypes(string pattern, string? namespaceFilter = null, string? assembly = null) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var types = DnSpyContext.Resolver.SearchTypes(pattern, assembly).ToList();

            if (!string.IsNullOrEmpty(namespaceFilter))
                types = types.Where(t => !UTF8String.IsNullOrEmpty(t.Namespace) && UTF8String.ToSystemStringOrEmpty(t.Namespace).IndexOf(namespaceFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (types.Count == 0)
                return $"No types matching '{pattern}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Types matching '{pattern}' ({types.Count}):\n");
            foreach (var type in types.OrderBy(t => t.FullName?.ToString()).Take(100))
                sb.AppendLine($"  {type.FullName}");
            if (types.Count > 100) sb.AppendLine($"\n  ... and {types.Count - 100} more");
            return sb.ToString();
        }

        [Description("Search for methods by name pattern. Use 'regex:' prefix for regex.")]
        public static string SearchMethods(string pattern, string? typeFullName = null, string? assembly = null) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var methods = DnSpyContext.Resolver.SearchMethods(pattern, typeFullName, assembly).ToList();

            if (methods.Count == 0)
                return $"No methods matching '{pattern}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Methods matching '{pattern}' ({methods.Count}):\n");
            foreach (var method in methods.OrderBy(m => m.FullName).Take(100))
                sb.AppendLine($"  {method.DeclaringType?.FullName}::{method.Name}");
            if (methods.Count > 100) sb.AppendLine($"\n  ... and {methods.Count - 100} more");
            return sb.ToString();
        }

        [Description("Search for string literals in the assembly.")]
        public static string SearchStrings(string? pattern = null, int minLength = 4, string? assembly = null) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var strings = CollectStringLiterals(pattern, minLength, assembly);

            if (strings.Count == 0)
                return pattern != null
                    ? $"No strings matching '{pattern}'."
                    : $"No strings found (min length: {minLength}).";

            var sb = new StringBuilder();
            sb.AppendLine($"Strings ({strings.Count} unique):\n");
            foreach (var s in strings.Take(500))
                sb.AppendLine($"  \"{s}\"");
            if (strings.Count > 500) sb.AppendLine($"\n  ... and {strings.Count - 500} more");
            return sb.ToString();
        }

        [Description("Grep across types, methods, and strings.")]
        public static string Grep(string pattern, string scope = "all", string? assembly = null) {
            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var sb = new StringBuilder();
            var total = 0;

            if (scope == "all" || scope == "types") {
                var types = DnSpyContext.Resolver.SearchTypes(pattern, assembly).ToList();
                if (types.Count > 0) {
                    sb.AppendLine($"Types ({types.Count}):");
                    foreach (var t in types.Take(50)) sb.AppendLine($"  {t.FullName}");
                    if (types.Count > 50) sb.AppendLine($"  ... {types.Count - 50} more");
                    total += types.Count;
                }
            }

            if (scope == "all" || scope == "methods") {
                var methods = DnSpyContext.Resolver.SearchMethods(pattern, null, assembly).ToList();
                if (methods.Count > 0) {
                    sb.AppendLine($"Methods ({methods.Count}):");
                    foreach (var m in methods.Take(50)) sb.AppendLine($"  {m.DeclaringType?.FullName}::{m.Name}");
                    if (methods.Count > 50) sb.AppendLine($"  ... {methods.Count - 50} more");
                    total += methods.Count;
                }
            }

            if (scope == "all" || scope == "strings") {
                var strings = CollectStringLiterals(pattern, 0, assembly);
                if (strings.Count > 0) {
                    sb.AppendLine($"Strings ({strings.Count}):");
                    foreach (var s in strings.Take(50)) sb.AppendLine($"  \"{s}\"");
                    if (strings.Count > 50) sb.AppendLine($"  ... {strings.Count - 50} more");
                    total += strings.Count;
                }
            }

            return total == 0
                ? $"No results for '{pattern}' in scope '{scope}'."
                : $"Results for '{pattern}': {total} total\n\n{sb}";
        }

        private static List<string> CollectStringLiterals(string? pattern, int minLength, string? assembly = null) {
            var strings = new List<string>();
            foreach (var mod in DnSpyContext.Resolver.GetModules(assembly)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (method.Body == null) continue;
                        foreach (var instr in method.Body.Instructions) {
                            if (instr.OpCode == OpCodes.Ldstr && instr.Operand is string str && str.Length >= minLength) {
                                if (pattern == null || str.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                    strings.Add(str);
                            }
                        }
                    }
                }
            }
            return strings.Distinct().OrderBy(s => s).ToList();
        }
    }
}
