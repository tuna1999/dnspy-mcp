using System;
using System.ComponentModel;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Tools {
    public static class RenameTools {
        [Description("Renames a namespace across matching types in an assembly. Use dryRun=true (default) to preview changes without modifying metadata.")]
        public static string RenameNamespace(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Current namespace to replace")] string oldNamespace,
            [Description("New namespace value")] string newNamespace,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(oldNamespace) || string.IsNullOrWhiteSpace(newNamespace))
                return "Error: assembly, oldNamespace, newNamespace are required.";

            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: DocumentService not available.";

            var plan = new StringBuilder();
            var changedCount = 0;
            ModuleDef? modifiedModule = null;
            IDsDocument? modifiedDoc = null;

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is not ModuleDef mod)
                    continue;

                if (!string.Equals(mod.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

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
                        modifiedDoc = doc;
                    }
                }
            }

            if (plan.Length == 0)
                return $"No types found in assembly '{assembly}' with namespace '{oldNamespace}'.";

            if (dryRun)
                return $"[DRY RUN] Namespace rename plan ({assembly}):\n{plan}";

            var saveResult = SaveModule(modifiedModule, modifiedDoc);
            return $"Renamed namespace for {changedCount} types in assembly '{assembly}'.{saveResult}";
        }

        [Description("Renames one class (type) in an assembly+namespace. Use dryRun=true (default) to preview first.")]
        public static string RenameClass(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Namespace containing the class")] string @namespace,
            [Description("Current class name (without namespace)")] string oldClassName,
            [Description("New class name") ] string newClassName,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(oldClassName) || string.IsNullOrWhiteSpace(newClassName))
                return "Error: assembly, namespace, oldClassName, newClassName are required.";

            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: DocumentService not available.";

            TypeDef? target = null;
            ModuleDef? targetModule = null;
            IDsDocument? targetDoc = null;

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is not ModuleDef mod)
                    continue;

                if (!string.Equals(mod.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var type in mod.GetTypes()) {
                    if (!string.Equals(type.Namespace, @namespace, StringComparison.Ordinal))
                        continue;

                    if (!string.Equals(type.Name.String, oldClassName, StringComparison.Ordinal))
                        continue;

                    target = type;
                    targetModule = mod;
                    targetDoc = doc;
                    break;
                }

                if (target != null)
                    break;
            }

            if (target == null || targetModule == null)
                return $"Class '{@namespace}.{oldClassName}' not found in assembly '{assembly}'.";

            var oldFullName = target.FullName;
            var newFullName = $"{@namespace}.{newClassName}";
            if (dryRun)
                return $"[DRY RUN] Class rename plan: {oldFullName} => {newFullName}";

            target.Name = (UTF8String)newClassName;
            var saveResult = SaveModule(targetModule, targetDoc);
            return $"Renamed class: {oldFullName} => {newFullName}.{saveResult}";
        }

        [Description("Renames methods in a class by exact or partial match. Use dryRun=true (default) to preview first.")]
        public static string RenameMethod(
            [Description("Assembly simple name (eg. MyAssembly)")] string assembly,
            [Description("Namespace containing the class")] string @namespace,
            [Description("Class name (without namespace)")] string className,
            [Description("Method name or substring to match")] string methodName,
            [Description("New method name") ] string newName,
            [Description("If true, match methodName by substring; otherwise exact match") ] bool partialMatch = false,
            [Description("Preview only, do not modify metadata")] bool dryRun = true) {

            if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(methodName) || string.IsNullOrWhiteSpace(newName))
                return "Error: assembly, namespace, className, methodName, newName are required.";

            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: DocumentService not available.";

            TypeDef? targetType = null;
            ModuleDef? targetModule = null;
            IDsDocument? targetDoc = null;

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is not ModuleDef mod)
                    continue;

                if (!string.Equals(mod.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var type in mod.GetTypes()) {
                    if (!string.Equals(type.Namespace, @namespace, StringComparison.Ordinal))
                        continue;

                    if (!string.Equals(type.Name.String, className, StringComparison.Ordinal))
                        continue;

                    targetType = type;
                    targetModule = mod;
                    targetDoc = doc;
                    break;
                }

                if (targetType != null)
                    break;
            }

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

            var saveResult = SaveModule(targetModule, targetDoc);
            return $"Renamed {changedCount} methods in '{targetType.FullName}'.{saveResult}";
        }

        private static string SaveModule(ModuleDef? module, IDsDocument? doc) {
            if (module == null)
                return "";

            // Refresh tree view on UI thread after metadata rename.
            TreeViewTools.RefreshTreeViewOnUIThread();

            return " (changes applied in-memory. Use dnSpy's File > Save Module to persist to disk.)";
        }
    }
}
