using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace dnSpy.MCP.Mcp {
    public sealed class ToolRegistry {
        private readonly Dictionary<string, ToolEntry> _tools = new();

        public ToolRegistry() {
            DiscoverTools();
        }

        private void DiscoverTools() {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var type in assembly.GetTypes()) {
                if (type.Namespace == null || !type.Namespace.StartsWith("dnSpy.MCP.Tools"))
                    continue;
                if (!type.IsClass || !type.IsAbstract) // static classes are abstract+sealed
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)) {
                    var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
                    if (descAttr == null) continue;

                    var toolName = ToSnakeCase(method.Name);
                    var parameters = method.GetParameters()
                        .Select(p => new ToolParam {
                            Name = p.Name ?? "arg",
                            Type = MapType(p.ParameterType),
                            Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                            Required = !p.HasDefaultValue
                        })
                        .ToList();

                    _tools[toolName] = new ToolEntry {
                        Name = toolName,
                        Description = descAttr.Description,
                        Method = method,
                        Parameters = parameters
                    };
                }
            }
        }

        public ToolEntry? GetTool(string name) =>
            _tools.TryGetValue(name, out var tool) ? tool : null;

        public List<object> ListTools() {
            return _tools.Values.OrderBy(t => t.Name).Select(t => (object)new {
                name = t.Name,
                description = t.Description,
                inputSchema = new {
                    type = "object",
                    properties = t.Parameters.ToDictionary(
                        p => p.Name,
                        p => (object)new { type = p.Type, description = p.Description }
                    ),
                    required = t.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
                }
            }).ToList();
        }

        public sealed class ToolEntry {
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public MethodInfo Method { get; init; } = null!;
            public List<ToolParam> Parameters { get; init; } = new();

            public string Invoke(JsonObject? arguments) {
                var methodParams = Method.GetParameters();
                var callArgs = new object?[methodParams.Length];

                for (int i = 0; i < methodParams.Length; i++) {
                    var p = methodParams[i];
                    var paramName = p.Name ?? "arg";
                    var node = ResolveArgument(arguments, paramName);

                    if (node != null) {
                        callArgs[i] = ConvertJsonValue(node, p.ParameterType, paramName);
                    }
                    else if (p.HasDefaultValue) {
                        callArgs[i] = p.DefaultValue;
                    }
                    else {
                        McpLogger.Warn($"[ARGS] Missing required param '{paramName}', received: {arguments?.ToJsonString() ?? "null"}");
                        throw new ArgumentException($"Missing required parameter: '{paramName}'");
                    }
                }

                var result = Method.Invoke(null, callArgs);
                return result?.ToString() ?? "";
            }

            /// <summary>
            /// Resolve argument by trying exact name, snake_case, and type-based semantic aliases.
            /// Claude Code may send different argument names than the C# parameter names.
            /// </summary>
            private static JsonNode? ResolveArgument(JsonObject? arguments, string paramName) {
                if (arguments == null) return null;

                // 1. Exact match
                if (arguments.TryGetPropertyValue(paramName, out var node))
                    return node;

                // 2. Snake_case match
                var snakeName = ToSnakeCase(paramName);
                if (arguments.TryGetPropertyValue(snakeName, out node))
                    return node;

                // 3. Semantic aliases — Claude Code often uses these instead of code parameter names
                var aliases = GetAliases(paramName);
                foreach (var alias in aliases) {
                    if (arguments.TryGetPropertyValue(alias, out node))
                        return node;
                }

                return null;
            }

            private static string[] GetAliases(string paramName) => paramName switch {
                // Type identification
                "typeFullname" or "typeFullName" => new[] { "typeName", "type_name", "type", "fullTypeName", "type_fullname", "type_full_name" },
                // Method identification
                "methodFullname" or "methodFullnameOrToken" => new[] { "methodName", "method_name", "method", "methodIdentifier", "method_identifier" },
                // Assembly scoping
                "assemblyName" => new[] { "assembly", "assembly_name", "module", "moduleName" },
                // Attribute target
                "targetType" => new[] { "target", "scope", "type" },
                // Search patterns
                "pattern" or "namePattern" => new[] { "regex", "filter", "name", "query", "search" },
                // Names
                "newName" => new[] { "new_name", "name", "renamedName" },
                // Resource
                "resourceName" => new[] { "resource", "resource_name", "name" },
                // Namespace
                "namespaceName" => new[] { "namespace", "namespace_name", "ns" },
                // Method body
                "csharpStatements" => new[] { "code", "statements", "patch", "csharp" },
                _ => Array.Empty<string>()
            };

            private static object? ConvertJsonValue(JsonNode? node, Type targetType, string paramName) {
                if (node == null) return null;

                // Handle nullable wrapper types
                var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                return node switch {
                    JsonValue jv when targetType == typeof(string) && jv.TryGetValue(out string? s) => s,
                    JsonValue jv when targetType == typeof(string) && jv.TryGetValue(out int n) => n.ToString(),
                    JsonValue jv when targetType == typeof(string) && jv.TryGetValue(out long l) => l.ToString(),
                    JsonValue jv when targetType == typeof(string) && jv.TryGetValue(out double d) => d.ToString(),
                    JsonValue jv when targetType == typeof(int) && jv.TryGetValue(out int n) => n,
                    JsonValue jv when targetType == typeof(int) && jv.TryGetValue(out long l) => (int)l,
                    JsonValue jv when targetType == typeof(int) && jv.TryGetValue(out double d) => (int)d,
                    JsonValue jv when targetType == typeof(long) && jv.TryGetValue(out long l) => l,
                    JsonValue jv when targetType == typeof(long) && jv.TryGetValue(out int n) => (long)n,
                    JsonValue jv when targetType == typeof(bool) && jv.TryGetValue(out bool b) => b,
                    JsonValue jv when targetType == typeof(double) && jv.TryGetValue(out double d) => d,
                    JsonValue jv when targetType == typeof(double) && jv.TryGetValue(out int n) => (double)n,
                    JsonValue jv when targetType == typeof(float) && jv.TryGetValue(out double d) => (float)d,
                    JsonValue jv => jv.ToString(),
                    _ => node.ToString()
                };
            }
        }

        public sealed class ToolParam {
            public string Name { get; init; } = "";
            public string Type { get; init; } = "string";
            public string Description { get; init; } = "";
            public bool Required { get; init; }
        }

        private static string ToSnakeCase(string name) {
            var sb = new System.Text.StringBuilder(name.Length + 10);
            for (int i = 0; i < name.Length; i++) {
                var c = name[i];
                if (i > 0 && char.IsUpper(c))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string MapType(Type t) {
            if (t == typeof(string)) return "string";
            if (t == typeof(int) || t == typeof(long)) return "integer";
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(float) || t == typeof(double)) return "number";
            return "string";
        }
    }
}
