using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;
using dnSpy.MCP.Helpers;

namespace dnSpy.MCP.Tools {
    public static class SearchTools {
        [Description("Search for types by name pattern. Use 'regex:' prefix for regex.")]
        public static string SearchTypes(string pattern, string? namespaceFilter = null) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var resolver = new MethodResolver(documentService);
            var types = resolver.SearchTypes(pattern).ToList();

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
        public static string SearchMethods(string pattern, string? typeFullName = null) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var resolver = new MethodResolver(documentService);
            var methods = resolver.SearchMethods(pattern, typeFullName).ToList();

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
        public static string SearchStrings(string? pattern = null, int minLength = 4) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var strings = new List<string>();
            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
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
            }

            var unique = strings.Distinct().OrderBy(s => s).ToList();
            if (unique.Count == 0)
                return pattern != null
                    ? $"No strings matching '{pattern}'."
                    : $"No strings found (min length: {minLength}).";

            var sb = new StringBuilder();
            sb.AppendLine($"Strings ({unique.Count} unique):\n");
            foreach (var s in unique.Take(500))
                sb.AppendLine($"  \"{s}\"");
            if (unique.Count > 500) sb.AppendLine($"\n  ... and {unique.Count - 500} more");
            return sb.ToString();
        }

        [Description("Grep across types, methods, and strings.")]
        public static string Grep(string pattern, string scope = "all") {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var resolver = new MethodResolver(documentService);
            var sb = new StringBuilder();
            var total = 0;

            if (scope == "all" || scope == "types") {
                var types = resolver.SearchTypes(pattern).ToList();
                if (types.Count > 0) {
                    sb.AppendLine($"Types ({types.Count}):");
                    foreach (var t in types.Take(50)) sb.AppendLine($"  {t.FullName}");
                    if (types.Count > 50) sb.AppendLine($"  ... {types.Count - 50} more");
                    total += types.Count;
                }
            }

            if (scope == "all" || scope == "methods") {
                var methods = resolver.SearchMethods(pattern).ToList();
                if (methods.Count > 0) {
                    sb.AppendLine($"Methods ({methods.Count}):");
                    foreach (var m in methods.Take(50)) sb.AppendLine($"  {m.DeclaringType?.FullName}::{m.Name}");
                    if (methods.Count > 50) sb.AppendLine($"  ... {methods.Count - 50} more");
                    total += methods.Count;
                }
            }

            if (scope == "all" || scope == "strings") {
                var strings = new List<string>();
                foreach (var doc in documentService.GetDocuments()) {
                    if (doc.ModuleDef is ModuleDef mod) {
                        foreach (var type in mod.GetTypes()) {
                            foreach (var method in type.Methods) {
                                if (method.Body == null) continue;
                                foreach (var instr in method.Body.Instructions) {
                                    if (instr.OpCode == OpCodes.Ldstr && instr.Operand is string str &&
                                        str.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                        strings.Add(str);
                                }
                            }
                        }
                    }
                }
                var unique = strings.Distinct().ToList();
                if (unique.Count > 0) {
                    sb.AppendLine($"Strings ({unique.Count}):");
                    foreach (var s in unique.Take(50)) sb.AppendLine($"  \"{s}\"");
                    if (unique.Count > 50) sb.AppendLine($"  ... {unique.Count - 50} more");
                    total += unique.Count;
                }
            }

            return total == 0
                ? $"No results for '{pattern}' in scope '{scope}'."
                : $"Results for '{pattern}': {total} total\n\n{sb}";
        }
    }
}
