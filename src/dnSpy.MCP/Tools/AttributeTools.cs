using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.MCP.Tools {
    public static class AttributeTools {
        [Description("List custom attributes on an assembly, type, method, or field. Optionally filter by attribute name.")]
        public static string GetAttributes(
            [Description("Target kind: 'assembly', 'type', 'method', or 'field'")] string targetType,
            [Description("Identifier: fullname or token. Use 'assembly' for the current module.")] string targetIdentifier,
            [Description("Optional filter: only show attributes whose name contains this substring")] string? attributeFilter = null) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var attrs = ResolveAttributes(targetType, targetIdentifier);
            if (attrs == null)
                return $"Target not found: {targetType} '{targetIdentifier}'";

            if (!string.IsNullOrEmpty(attributeFilter))
                attrs = attrs.Where(a => {
                    var name = a.AttributeType?.FullName ?? "";
                    return name.IndexOf(attributeFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();

            var attrList = attrs.ToList();
            if (attrList.Count == 0)
                return string.IsNullOrEmpty(attributeFilter)
                    ? $"No custom attributes on {targetType} '{targetIdentifier}'."
                    : $"No attributes matching '{attributeFilter}' on {targetType} '{targetIdentifier}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Custom attributes on {targetType} '{targetIdentifier}' ({attrList.Count}):\n");

            foreach (var ca in attrList) {
                var typeName = ca.AttributeType?.FullName ?? ca.AttributeType?.Name ?? "?";
                sb.AppendLine($"  [{typeName}]");

                if (ca.ConstructorArguments.Count > 0) {
                    sb.AppendLine("    Constructor args:");
                    foreach (var arg in ca.ConstructorArguments)
                        sb.AppendLine($"      {FormatCAValue(arg)}");
                }

                if (ca.NamedArguments.Count > 0) {
                    sb.AppendLine("    Named args:");
                    foreach (var na in ca.NamedArguments)
                        sb.AppendLine($"      {na.Name} = {FormatCAValue(na.Argument)}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        [Description("List custom attributes on a specific method. Shortcut for get_attributes with targetType='method'.")]
        public static string GetMethodAttributes(
            [Description("Method identifier: full name, token, or partial name")] string methodFullnameOrToken,
            [Description("Optional filter: only show attributes whose name contains this substring")] string? attributeFilter = null) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var method = DnSpyContext.Resolver.ResolveMethodFlexible(methodFullnameOrToken);
            if (method == null)
                return $"Method not found: {methodFullnameOrToken}";

            return GetAttributes("method", method.FullName, attributeFilter);
        }

        private static System.Collections.Generic.IEnumerable<CustomAttribute>? ResolveAttributes(string targetType, string identifier) {
            var resolver = DnSpyContext.Resolver;

            switch (targetType.ToLowerInvariant()) {
                case "assembly": {
                    var mod = string.IsNullOrEmpty(identifier) || identifier.Equals("assembly", StringComparison.OrdinalIgnoreCase)
                        ? resolver.GetCurrentModule()
                        : resolver.GetModules(identifier).FirstOrDefault();
                    return mod?.Assembly?.CustomAttributes ?? Enumerable.Empty<CustomAttribute>();
                }
                case "type": {
                    var type = resolver.ResolveType(identifier);
                    return type?.CustomAttributes;
                }
                case "method": {
                    var method = resolver.ResolveMethodFlexible(identifier);
                    return method?.CustomAttributes;
                }
                case "field": {
                    var parts = identifier.Split(new[] { "::" }, StringSplitOptions.None);
                    if (parts.Length != 2) return null;
                    var type = resolver.ResolveType(parts[0]);
                    return type?.Fields.FirstOrDefault(f => f.Name.String == parts[1])?.CustomAttributes;
                }
                default:
                    return null;
            }
        }

        private static string FormatCAValue(CAArgument arg) {
            if (arg.Value == null) return "null";

            if (arg.Value is TypeSig ts)
                return $"typeof({ts.FullName})";

            if (arg.Value is UTF8String s)
                return $"\"{s.String}\"";

            if (arg.Value is byte[] bytes)
                return $"{bytes.Length} bytes";

            if (arg.Value is int[] intArr)
                return string.Join(", ", intArr);

            return arg.Value.ToString() ?? "null";
        }
    }
}
