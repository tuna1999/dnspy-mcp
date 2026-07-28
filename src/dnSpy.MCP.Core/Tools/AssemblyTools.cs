using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class AssemblyTools {
        private readonly McpContext _ctx;
        public AssemblyTools(McpContext ctx) => _ctx = ctx;

        [Description("Load a .NET DLL/EXE into dnSpy by absolute path. Returns the assembly name and type count on success. Use list_loaded_assemblies to verify.")]
        public string LoadAssembly(
            [Description("Absolute path to the DLL or EXE file")] string path) {

            if (string.IsNullOrWhiteSpace(path))
                return "Error: path is required.";

            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            try {
                var result = _ctx.AssemblyLoader.Load(path);
                if (!result.Success)
                    return $"Error: {result.Error}";

                var loaded = result.Module!;
                if (loaded.Module is ModuleDef mod) {
                    var typeCount = mod.GetTypes().Count();
                    _ctx.Log.Info($"Loaded assembly: {mod.Assembly?.Name?.String ?? mod.Name} ({typeCount} types) from {path}");
                    return $"Loaded: {mod.Assembly?.Name?.String ?? mod.Name}\n  Path: {mod.Location}\n  Types: {typeCount}\n  MVID: {mod.Mvid}";
                }

                return $"Loaded (non-CLR or no module): {path}";
            }
            catch (Exception ex) {
                _ctx.Log.Error($"LoadAssembly failed: {path}: {ex.Message}");
                return $"Error loading '{path}': {ex.Message}";
            }
        }

        [Description("Unload (close) an assembly from dnSpy by its simple name (e.g. 'MyAssembly'). Case-insensitive. Use list_loaded_assemblies to see names. Returns how many documents were removed.")]
        public string CloseAssembly(
            [Description("Assembly simple name to remove (case-insensitive)")] string assemblyName) {

            if (string.IsNullOrWhiteSpace(assemblyName))
                return "Error: assemblyName is required.";

            // Snapshot matching names before removal so the close message can list them
            // even after the underlying documents are gone.
            var names = new List<string>();
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
                    var name = mod.Assembly?.Name?.String ?? mod.Name;
                    if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            if (names.Count == 0)
                return $"No loaded assembly named '{assemblyName}'. Use list_loaded_assemblies to see what's loaded.";

            try {
                var removed = _ctx.AssemblyLoader.Close(assemblyName);
                _ctx.Log.Info($"Closed assembly '{assemblyName}' ({removed} document(s))");
                return $"Closed {removed} document(s) matching '{assemblyName}':\n  {string.Join("\n  ", names)}";
            }
            catch (Exception ex) {
                _ctx.Log.Error($"CloseAssembly failed: {assemblyName}: {ex.Message}");
                return $"Error closing '{assemblyName}': {ex.Message}";
            }
        }

        [Description("List all binaries currently loaded in dnSpy. Shows filename, assembly name, MVID, type count, and file path.")]
        public string ListLoadedAssemblies() {
            var docs = _ctx.AssemblyLoader.GetDocuments();
            if (docs.Count == 0)
                return "No assemblies loaded.";

            var sb = new StringBuilder();
            sb.AppendLine($"Loaded assemblies ({docs.Count}):\n");

            for (int i = 0; i < docs.Count; i++) {
                var loaded = docs[i];
                if (loaded.Module is not ModuleDef mod) continue;

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
        public string AssemblyOverview(
            [Description("Optional assembly name to scope to (e.g. 'PVService')")] string? assemblyName = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            if (string.IsNullOrEmpty(assemblyName)) {
                foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                    if (loaded.Module is ModuleDef mod)
                        return FormatModuleOverview(mod);
                }
                return "No assembly loaded. Please open an assembly in dnSpy.";
            }

            foreach (var mod in _ctx.Resolver.GetModules(assemblyName)) {
                return FormatModuleOverview(mod);
            }

            return $"Assembly '{assemblyName}' not found. Use list_loaded_assemblies to see available assemblies.";
        }

        [Description("List all namespaces in the currently loaded assembly.")]
        public string AssemblyListNamespaces() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var namespaces = new SortedSet<string>();
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
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
        public string AssemblyListTypes(string? pattern = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var types = new List<string>();
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
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
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex) {
                            // Regex too expensive on this type name; treat as no-match
                            // but trace it so pathological patterns are diagnosable.
                            _ctx.Log.Warn($"Regex timeout matching '{fullName}' against pattern '{pattern}': {ex.Message}");
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
        public string AssemblyGetReferences() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var sb = new StringBuilder();
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
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
    }
}
