using System;
using System.Collections.Generic;
using System.Text.Json;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Normalizes incoming MCP tool-call argument keys so clients may use either the
/// schema-declared camelCase parameter names (<c>memberFullName</c>) or the
/// snake_case form common in the MCP ecosystem (<c>member_full_name</c>).
///
/// MCP tool NAMES in this server are snake_case (e.g. <c>get_xrefs_to</c>), and
/// LLM agents frequently assume parameters follow the same convention. Without
/// normalization those calls fail validation with "X: is required" even though
/// the agent supplied the value under the snake_case key.
///
/// Mapping table is populated by <see cref="AutoToolRegistration"/> at startup;
/// only aliases that differ from the declared parameter name are registered.
/// Exact-match keys always win — an explicit camelCase key is never rewritten.
/// </summary>
public static class ArgumentNameNormalizer {
    private static readonly Dictionary<string, Dictionary<string, string>> _aliases = new();

    /// <summary>Registers the declared parameter names for a tool.</summary>
    public static void Register(string toolName, IEnumerable<string> parameterNames) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var param in parameterNames) {
            var snake = ToolRegistry.ToSnakeCase(param);
            if (!string.Equals(snake, param, StringComparison.Ordinal))
                map[snake] = param;
        }
        if (map.Count > 0)
            _aliases[toolName] = map;
    }

    /// <summary>
    /// Rewrites snake_case aliases in <paramref name="arguments"/> to their declared
    /// camelCase parameter names for the given tool. Returns the (possibly new)
    /// dictionary; null when nothing changed.
    /// </summary>
    public static IDictionary<string, JsonElement>? Normalize(
        string toolName, IDictionary<string, JsonElement>? arguments) {
        if (arguments is null || arguments.Count == 0 || !_aliases.TryGetValue(toolName, out var map))
            return null;

        Dictionary<string, JsonElement>? rewritten = null;
        foreach (var key in arguments.Keys) {
            if (!map.TryGetValue(key, out var canonical))
                continue;
            rewritten ??= new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
            rewritten.Remove(key);
            // An explicit exact-name key always wins over the alias.
            rewritten.TryAdd(canonical, arguments[key]);
        }
        return rewritten;
    }
}
