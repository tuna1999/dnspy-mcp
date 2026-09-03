using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using dnSpy.MCP.Core.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Scans Core tool classes via reflection and registers each [Description] method
/// as an MCP tool. Auto-discovers future Core tools without manual wrapper maintenance.
///
/// "Core" prefix reflects scope: only Core's instance tool classes are registered.
/// Extension-only static tools (e.g. TreeViewTools for get_selected_node, refresh_u_i)
/// are intentionally skipped — they have no McpContext ctor and need WPF, neither of
/// which exist in the headless stdio transport. The Extension transport registers
/// those via the in-tree ToolRegistry instead.
/// </summary>
public static class AutoToolRegistration {
    public static void RegisterCoreTools(IMcpServerBuilder builder, McpContext ctx) {
        var tools = new List<McpServerTool>();
        var coreAsm = typeof(McpContext).Assembly;
        foreach (var type in coreAsm.GetTypes()) {
            if (!IsToolClass(type)) continue;

            object? instance = null;
            var ctor = type.GetConstructor(new[] { typeof(McpContext) });
            if (ctor is null) continue;  // skip Extension-only static tools (TreeViewTools)
            instance = ctor.Invoke(new object[] { ctx });

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
                var desc = method.GetCustomAttribute<DescriptionAttribute>();
                if (desc is null) continue;

                var toolName = ToolRegistry.ToSnakeCase(method.Name);
                ArgumentNameNormalizer.Register(toolName,
                    method.GetParameters().Select(p => p.Name!));
                var mcpTool = McpServerTool.Create(method, instance,
                    new McpServerToolCreateOptions {
                        Name = toolName,
                        Description = desc.Description,
                    });
                tools.Add(mcpTool);
            }
        }

        // SDK 1.4.0 has no single-tool WithTools overload; batch-register the collection.
        builder.WithTools(tools);
    }

    private static bool IsToolClass(Type type) {
        if (type.Namespace is null ||
            !(type.Namespace.StartsWith("dnSpy.MCP.Core.Tools") || type.Namespace.StartsWith("dnSpy.MCP.Tools")))
            return false;
        if (!type.IsClass || type.IsAbstract) return false;
        return type.GetConstructor(new[] { typeof(McpContext) }) != null;
    }
}
