using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.MCP.Tools {
    public static class ConstantTools {
        [Description("Get all named values of an enum type with underlying type info.")]
        public static string GetEnumValues(
            [Description("Full enum type name")] string enumTypeFullName) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var type = DnSpyContext.Resolver.ResolveType(enumTypeFullName);
            if (type == null)
                return $"Type not found: {enumTypeFullName}";

            if (!type.IsEnum)
                return $"'{type.FullName}' is not an enum. (Flags: IsEnum={type.IsEnum})";

            var underlyingType = type.GetEnumUnderlyingType().FullName ?? "int";

            var sb = new StringBuilder();
            sb.AppendLine($"Enum: {type.FullName}");
            sb.AppendLine($"Underlying type: {underlyingType}");
            sb.AppendLine($"Token: 0x{type.MDToken.Raw:X8}");
            sb.AppendLine();
            sb.AppendLine("Values:");
            sb.AppendLine($"  {"Name",-35} {"Dec",12} {"Hex",12}");
            sb.AppendLine($"  {new string('-', 35)} {new string('-', 12)} {new string('-', 12)}");

            foreach (var field in type.Fields) {
                if (!field.IsLiteral || !field.IsStatic) continue;

                var name = field.Name.String;
                var value = field.Constant?.Value;

                if (value != null) {
                    var decStr = Convert.ToInt64(value).ToString();
                    var hexStr = $"0x{Convert.ToUInt64(value):X}";
                    sb.AppendLine($"  {name,-35} {decStr,12} {hexStr,12}");
                }
                else {
                    sb.AppendLine($"  {name,-35} {"?",12} {"?",12}");
                }
            }

            return sb.ToString();
        }

        [Description("Search for constant/literal fields across loaded assemblies. Finds const fields and enum values.")]
        public static string SearchConstants(
            [Description("Search pattern: name or value substring")] string pattern,
            [Description("Optional: restrict search to types in this namespace")] string? namespaceFilter = null,
            [Description("Optional: restrict search to this assembly name")] string? assembly = null) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            if (string.IsNullOrEmpty(pattern))
                return "Error: pattern is required.";

            var results = new List<(TypeDef Type, FieldDef Field, object? Value)>();

            foreach (var mod in DnSpyContext.Resolver.GetModules(assembly)) {
                foreach (var type in mod.GetTypes()) {
                    if (!string.IsNullOrEmpty(namespaceFilter) &&
                        !UTF8String.IsNullOrEmpty(type.Namespace) &&
                        type.Namespace.String.IndexOf(namespaceFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreach (var field in type.Fields) {
                        if (!field.IsLiteral) continue;

                        var name = field.Name.String;
                        var value = field.Constant?.Value;

                        var nameMatch = name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                        var valueMatch = value != null && value.ToString()?.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

                        if (nameMatch || valueMatch)
                            results.Add((type, field, value));
                    }
                }
            }

            if (results.Count == 0)
                return $"No constants matching '{pattern}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Constants matching '{pattern}' ({results.Count}):\n");

            foreach (var (type, field, value) in results.Take(200)) {
                var enumTag = type.IsEnum ? " [enum]" : "";
                sb.AppendLine($"  {type.FullName}{enumTag}::{field.Name} = {FormatValue(value)} ({field.FieldType?.FullName ?? "?"})");
            }

            if (results.Count > 200)
                sb.AppendLine($"\n  ... and {results.Count - 200} more");

            return sb.ToString();
        }

        private static string FormatValue(object? value) {
            if (value == null) return "null";
            if (value is byte[] bytes)
                return $"{bytes.Length} bytes";
            if (value is string s)
                return $"\"{s}\"";
            return value.ToString() ?? "null";
        }
    }
}
