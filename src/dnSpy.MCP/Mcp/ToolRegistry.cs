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
                    if (arguments != null && arguments.TryGetPropertyValue(p.Name ?? "", out var node)) {
                        callArgs[i] = node switch {
                            JsonValue jv when jv.TryGetValue(out string? s) => s,
                            JsonValue jv when jv.TryGetValue(out int n) => n,
                            JsonValue jv when jv.TryGetValue(out bool b) => b,
                            _ => node?.ToString()
                        };
                    }
                    else if (p.HasDefaultValue) {
                        callArgs[i] = p.DefaultValue;
                    }
                }

                var result = Method.Invoke(null, callArgs);
                return result?.ToString() ?? "";
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
            if (t == typeof(int)) return "integer";
            if (t == typeof(bool)) return "boolean";
            return "string";
        }
    }
}
