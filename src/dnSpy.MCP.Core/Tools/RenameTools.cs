using System;
using System.ComponentModel;
using System.Text;
using dnlib.DotNet;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class RenameTools {
        private readonly McpContext _ctx;
        public RenameTools(McpContext ctx) => _ctx = ctx;

        private TypeDef? FindType(string assembly, string @namespace, string className) {
            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (!string.Equals(loaded.AssemblyName ?? loaded.Name, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var type in loaded.Module.GetTypes()) {
                    if (string.Equals(type.Namespace, @namespace, StringComparison.Ordinal)
                        && string.Equals(type.Name.String, className, StringComparison.Ordinal))
                        return type;
                }
            }
            return null;
        }

        [Description("Renames a namespace across matching types in an assembly. Use dryRun=true (default) to preview changes without modifying metadata.")]
        public string RenameNamespace(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Current namespace to replace")] string oldNamespace,
            [Description("New namespace value")] string newNamespace,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(oldNamespace) || string.IsNullOrWhiteSpace(newNamespace))
                return "Error: assembly, oldNamespace, newNamespace are required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: DocumentService not available.";

            var plan = new StringBuilder();
            var changedCount = 0;
            ModuleDef? modifiedModule = null;

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (!string.Equals(loaded.AssemblyName ?? loaded.Name, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

                var mod = loaded.Module;
                foreach (var type in mod.GetTypes()) {
                    if (!string.Equals(type.Namespace, oldNamespace, StringComparison.Ordinal))
                        continue;

                    var oldFullName = type.FullName;
                    var nextNamespace = (UTF8String)newNamespace;
                    plan.AppendLine($"{oldFullName} => {newNamespace}.{type.Name}");

                    if (!dryRun) {
                        type.Namespace = nextNamespace;
                        changedCount++;
                        modifiedModule = mod;
                    }
                }
            }

            if (plan.Length == 0)
                return $"No types found in assembly '{assembly}' with namespace '{oldNamespace}'.";

            if (dryRun)
                return $"[DRY RUN] Namespace rename plan ({assembly}):\n{plan}";

            _ctx.TreeRefresh.NotifyNamespaceRenamed(assembly, oldNamespace, newNamespace);
            var saveResult = RefreshAfterRename(modifiedModule);
            return $"Renamed namespace for {changedCount} types in assembly '{assembly}'.{saveResult}";
        }

        [Description("Renames one class (type) in an assembly+namespace. Use dryRun=true (default) to preview first.")]
        public string RenameClass(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Namespace containing the class")] string @namespace,
            [Description("Current class name (without namespace)")] string oldClassName,
            [Description("New class name") ] string newClassName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(oldClassName) || string.IsNullOrWhiteSpace(newClassName))
                return "Error: assembly, namespace, oldClassName, newClassName are required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: DocumentService not available.";

            var target = FindType(assembly, @namespace, oldClassName);

            if (target == null)
                return $"Class '{@namespace}.{oldClassName}' not found in assembly '{assembly}'.";

            var oldFullName = target.FullName;
            var newFullName = $"{@namespace}.{newClassName}";
            if (dryRun)
                return $"[DRY RUN] Class rename plan: {oldFullName} => {newFullName}";

            target.Name = (UTF8String)newClassName;
            var saveResult = RefreshAfterRename(target.Module);
            return $"Renamed class: {oldFullName} => {newFullName}.{saveResult}";
        }

        [Description("Renames methods in a class by exact or partial match. Use dryRun=true (default) to preview first.")]
        public string RenameMethod(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Namespace containing the class")] string @namespace,
            [Description("Class name (without namespace)")] string className,
            [Description("Method name or substring to match")] string methodName,
            [Description("New method name") ] string newName,
            [Description("If true, match methodName by substring; otherwise exact match") ] bool partialMatch = false,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(newName))
                return "Error: assembly, namespace, className, methodName, newName are required.";

            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: DocumentService not available.";

            var targetType = FindType(assembly, @namespace, className);

            if (targetType == null)
                return $"Class '{@namespace}.{className}' not found in assembly '{assembly}'.";

            var plan = new StringBuilder();
            var changedCount = 0;

            foreach (var method in targetType.Methods) {
                var name = method.Name.String;
                var matched = partialMatch
                    ? name.Contains(methodName, StringComparison.Ordinal)
                    : string.Equals(name, methodName, StringComparison.Ordinal);

                if (!matched)
                    continue;

                plan.AppendLine($"{targetType.FullName}::{name} => {newName}");
                if (!dryRun) {
                    method.Name = (UTF8String)newName;
                    changedCount++;
                }
            }

            if (plan.Length == 0)
                return partialMatch
                    ? $"No methods containing '{methodName}' found in '{targetType.FullName}'."
                    : $"Method '{methodName}' not found in '{targetType.FullName}'.";

            if (dryRun)
                return $"[DRY RUN] Method rename plan:\n{plan}";

            var saveResult = RefreshAfterRename(targetType.Module);
            return $"Renamed {changedCount} methods in '{targetType.FullName}'.{saveResult}";
        }

        private string RefreshAfterRename(ModuleDef? module) {
            if (module == null)
                return "";

            _ctx.TreeRefresh.RefreshAll();

            return " (changes applied in-memory. Use dnSpy's File > Save Module to persist to disk.)";
        }
    }
}
