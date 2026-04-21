using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
    public static class AssemblyTools {
        [Description("List all binaries currently loaded in dnSpy. Shows filename, assembly name, MVID, type count, and file path.")]
        public static string ListLoadedAssemblies() {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            var docs = documentService.GetDocuments().ToList();
            if (docs.Count == 0)
                return "No assemblies loaded.";

            var sb = new StringBuilder();
            sb.AppendLine($"Loaded assemblies ({docs.Count}):\n");

            for (int i = 0; i < docs.Count; i++) {
                var doc = docs[i];
                if (doc.ModuleDef is not ModuleDef mod) continue;

                var typeCount = mod.GetTypes().Count();
                sb.AppendLine($"  [{i}] {mod.Name}");
                sb.AppendLine($"      Assembly:  {mod.Assembly?.Name?.String ?? "N/A"}");
                sb.AppendLine($"      MVID:      {mod.Mvid}");
                sb.AppendLine($"      Types:     {typeCount}");
                sb.AppendLine($"      Path:      {mod.Location ?? "(in-memory)"}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        [Description("Get overview of the currently loaded assembly. Returns module name, version, entry point, type count, and assembly references.")]
        public static string AssemblyOverview(
            [Description("Optional assembly name to scope to (e.g. 'PVService')")] string? assemblyName = null) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: dnSpy document service not available.";

            if (string.IsNullOrEmpty(assemblyName)) {
                foreach (var doc in documentService.GetDocuments()) {
                    if (doc.ModuleDef is ModuleDef mod)
                        return FormatModuleOverview(mod);
                }
                return "No assembly loaded. Please open an assembly in dnSpy.";
            }

            foreach (var mod in DnSpyContext.Resolver.GetModules(assemblyName)) {
                return FormatModuleOverview(mod);
            }

            return $"Assembly '{assemblyName}' not found. Use list_loaded_assemblies to see available assemblies.";
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
