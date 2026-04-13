using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
    public static class AssemblyTools {
        [Description("Get overview of the currently loaded assembly. Returns module name, version, entry point, type count, and assembly references.")]
        public static string AssemblyOverview(
            [Description("Optional assembly path to load")] string? assemblyPath = null) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            if (string.IsNullOrEmpty(assemblyPath)) {
                foreach (var doc in documentService.GetDocuments()) {
                    if (doc.ModuleDef is ModuleDef mod)
                        return FormatModuleOverview(mod);
                }
                return "No assembly loaded. Please open an assembly in dnSpy.";
            }

            return $"Loading from path not yet implemented. Currently loaded:\n{GetCurrentlyLoadedOverview(documentService)}";
        }

        [Description("List all namespaces in the currently loaded assembly.")]
        public static string AssemblyListNamespaces() {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var namespaces = new SortedSet<string>();
            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
                    foreach (var type in mod.GetTypes()) {
                        if (!string.IsNullOrEmpty(type.Namespace))
                            namespaces.Add(type.Namespace);
                    }
                }
            }

            return namespaces.Count == 0
                ? "No namespaces found."
                : $"Namespaces ({namespaces.Count}):\n" + string.Join("\n", namespaces);
        }

        [Description("List types in the currently loaded assembly, optionally filtered by a regex pattern.")]
        public static string AssemblyListTypes(string? pattern = null) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var types = new List<string>();
            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
                    foreach (var type in mod.GetTypes()) {
                        var fullName = type.FullName?.ToString();
                        if (string.IsNullOrEmpty(fullName)) continue;
                        if (pattern == null) {
                            types.Add(fullName);
                            continue;
                        }

                        try {
                            if (System.Text.RegularExpressions.Regex.IsMatch(fullName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))) {
                                types.Add(fullName);
                            }
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) {
                            // treat timeout as no-match
                        }
                    }
                }
            }

            if (types.Count == 0)
                return pattern != null
                    ? $"No types match '{pattern}'."
                    : "No types found.";

            return $"Types ({types.Count}):\n" + string.Join("\n", types.OrderBy(t => t));
        }

        [Description("Get assembly references (DLLs, NuGet packages) of the currently loaded assembly.")]
        public static string AssemblyGetReferences() {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var sb = new StringBuilder();
            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
                    sb.AppendLine($"Module: {mod.Name}");
                    sb.AppendLine($"Assembly: {mod.Assembly?.FullName ?? "N/A"}");
                    sb.AppendLine();
                    sb.AppendLine("References:");
                    foreach (var asmRef in mod.GetAssemblyRefs())
                        sb.AppendLine($"  - {asmRef.FullName}");
                }
            }
            return sb.Length == 0 ? "No assembly loaded." : sb.ToString();
        }

        private static string FormatModuleOverview(ModuleDef mod) {
            var sb = new StringBuilder();
            sb.AppendLine($"Module: {mod.Name}");
            sb.AppendLine($"Assembly: {mod.Assembly?.FullName ?? "N/A"}");
            sb.AppendLine($"Module Version ID: {mod.Mvid}");
            sb.AppendLine($"Runtime: {mod.RuntimeVersion}");

            if (mod.EntryPoint != null)
                sb.AppendLine($"Entry Point: {mod.EntryPoint.DeclaringType?.FullName}::{mod.EntryPoint.Name}");

            var types = mod.GetTypes().ToList();
            sb.AppendLine($"Type Count: {types.Count}");
            var nsCount = types.Select(t => t.Namespace).Where(ns => !string.IsNullOrEmpty(ns)).Distinct().Count();
            sb.AppendLine($"Namespace Count: {nsCount}");

            sb.AppendLine();
            sb.AppendLine("References:");
            foreach (var asmRef in mod.GetAssemblyRefs())
                sb.AppendLine($"  - {asmRef.FullName}");

            return sb.ToString();
        }

        private static string GetCurrentlyLoadedOverview(IDsDocumentService documentService) {
            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod)
                    return FormatModuleOverview(mod);
            }
            return "No assembly loaded.";
        }
    }
}
