using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Normalizes incoming MCP tool-call argument keys so clients may use:
///   1. the schema-declared camelCase parameter names (<c>memberFullName</c>),
///   2. the snake_case form (<c>member_full_name</c>) — common MCP convention,
///   3. a single unrecognized string key (e.g. <c>query</c>) which is mapped to
///      the one required parameter when that parameter is missing.
///
/// MCP tool NAMES in this server are snake_case, and LLM agents frequently
/// assume parameters follow the same convention — or invent synonyms like
/// "query" for a parameter declared as "pattern". Without normalization those
/// calls fail validation with "X: is required" even though a value was supplied.
///
/// The mapping table is populated by <see cref="AutoToolRegistration"/> at
/// startup. Exact-match keys always win; heuristic (3) only fires when strict
/// validation would otherwise fail, so it can never break a correct call.
/// </summary>
public static class ArgumentNameNormalizer {
    private static readonly Dictionary<string, ToolParams> _tools = new();

    private sealed class ToolParams {
        /// <summary>snake_case alias → declared parameter name (only when they differ).</summary>
        public required Dictionary<string, string> Aliases { get; init; }
        /// <summary>All declared parameter names (case-sensitive).</summary>
        public required HashSet<string> Names { get; init; }
        /// <summary>Parameters without a default value (i.e. schema-required).</summary>
        public required List<string> Required { get; init; }
    }

    /// <summary>Registers the declared parameters for a tool.</summary>
    public static void Register(string toolName, IEnumerable<ParameterInfo> parameters) {
        var plist = parameters.ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in plist) {
            var snake = ToolRegistry.ToSnakeCase(p.Name!);
            if (!string.Equals(snake, p.Name, StringComparison.Ordinal))
                map[snake] = p.Name!;
        }
        _tools[toolName] = new ToolParams {
            Aliases = map,
            Names = plist.Select(p => p.Name!).ToHashSet(StringComparer.Ordinal),
            Required = plist.Where(p => !p.HasDefaultValue).Select(p => p.Name!).ToList(),
        };
    }

    /// <summary>
    /// Rewrites argument keys for the given tool to their declared parameter
    /// names. Returns the (possibly new) dictionary; null when nothing changed.
    /// </summary>
    public static IDictionary<string, JsonElement>? Normalize(
        string toolName, IDictionary<string, JsonElement>? arguments) {
        if (arguments is null || arguments.Count == 0 || !_tools.TryGetValue(toolName, out var tp))
            return null;

        Dictionary<string, JsonElement>? rewritten = null;

        // Pass 1: snake_case aliases → declared names. An exact-name key wins.
        foreach (var key in arguments.Keys) {
            if (!tp.Aliases.TryGetValue(key, out var canonical))
                continue;
            rewritten ??= new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
            rewritten.Remove(key);
            rewritten.TryAdd(canonical, arguments[key]);
        }

        var effective = (IDictionary<string, JsonElement>?)rewritten ?? arguments;

        // Pass 2 (fallback heuristic): a required parameter is still missing and
        // exactly one unrecognized string key was supplied → map it over. This
        // only runs when strict validation would fail regardless.
        var present = new HashSet<string>(effective.Keys, StringComparer.Ordinal);
        var missingRequired = tp.Required.Where(r => !present.Contains(r)).ToList();
        if (missingRequired.Count == 1) {
            var unknownKeys = effective.Keys
                .Where(k => !tp.Names.Contains(k) && !tp.Aliases.ContainsKey(k))
                .ToList();
            if (unknownKeys.Count == 1 &&
                effective[unknownKeys[0]].ValueKind == JsonValueKind.String) {
                var value = effective[unknownKeys[0]];
                rewritten ??= new Dictionary<string, JsonElement>(effective, StringComparer.Ordinal);
                rewritten.Remove(unknownKeys[0]);
                rewritten.TryAdd(missingRequired[0], value);
            }
        }

        return rewritten;
    }
}
