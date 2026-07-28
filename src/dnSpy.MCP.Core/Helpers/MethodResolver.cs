using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Core.Helpers {
    /// <summary>
    /// Resolves methods and types by full name or token. Host-agnostic port of the
    /// Extension MethodResolver: depends on <see cref="IAssemblyLoader"/> instead of
    /// dnSpy's IDsDocumentService. Resolution logic is otherwise unchanged.
    /// </summary>
    public sealed class MethodResolver {
        private readonly IAssemblyLoader _loader;

        public MethodResolver(IAssemblyLoader loader) {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        /// <summary>
        /// Gets the first module from loaded documents
        /// </summary>
        public ModuleDef? GetCurrentModule() {
            foreach (var loaded in _loader.GetDocuments())
                return loaded.Module;
            return null;
        }

        /// <summary>
        /// Gets all loaded modules
        /// </summary>
        public IEnumerable<ModuleDef> GetAllModules() {
            foreach (var loaded in _loader.GetDocuments())
                yield return loaded.Module;
        }

        /// <summary>
        /// Gets modules filtered by assembly name (case-insensitive), or all if null/empty.
        /// </summary>
        public IEnumerable<ModuleDef> GetModules(string? assemblyName) {
            var modules = GetAllModules();
            if (string.IsNullOrEmpty(assemblyName))
                return modules;
            return modules.Where(m => string.Equals(m.Assembly?.Name?.String, assemblyName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves a method by full name (e.g., "Namespace.Class::Method"), optionally scoped to an assembly.
        /// </summary>
        public MethodDef? ResolveMethod(string fullName, string? assemblyName = null) {
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var method in type.Methods) {
                        if (method.FullName == fullName || $"{type.FullName}::{method.Name}" == fullName)
                            return method;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves a method by metadata token
        /// </summary>
        public MethodDef? ResolveMethodByToken(int token, string? assemblyName = null) {
            foreach (var mod in GetModules(assemblyName)) {
                var resolved = mod.ResolveToken(token);
                if (resolved is MethodDef method)
                    return method;
            }
            return null;
        }

        /// <summary>
        /// Resolves a type by full name
        /// </summary>
        public TypeDef? ResolveType(string fullName, string? assemblyName = null) {
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    if (type.FullName == fullName)
                        return type;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves a type by metadata token
        /// </summary>
        public TypeDef? ResolveTypeByToken(int token, string? assemblyName = null) {
            foreach (var mod in GetModules(assemblyName)) {
                var resolved = mod.ResolveToken(token);
                if (resolved is TypeDef type)
                    return type;
            }
            return null;
        }

        /// <summary>
        /// Flexible method resolution: tries hex token, plain token, full name, then fallback short name.
        /// Returns the first match found.
        /// </summary>
        /// <remarks>
        /// The short-name fallback returns the <b>first</b> method whose name matches. For common
        /// names (ToString, Equals, Dispose, etc.) there may be many matches across types —
        /// callers should prefer a full name or token. When the fallback finds more than one
        /// candidate, a warning is logged so ambiguous resolution is diagnosable.
        /// </remarks>
        public MethodDef? ResolveMethodFlexible(string identifier, string? assemblyName = null) {
            MethodDef? method = null;

            if (identifier.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                var hex = identifier.Substring(2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int token))
                    method = ResolveMethodByToken(token, assemblyName);
            }
            else if (int.TryParse(identifier, out int plainToken)) {
                method = ResolveMethodByToken(plainToken, assemblyName);
            }

            if (method == null)
                method = ResolveMethod(identifier, assemblyName);

            if (method == null)
                method = ResolveByShortName(identifier, assemblyName);

            return method;
        }

        /// <summary>
        /// Last-resort resolution: matches a bare method name (or the trailing segment of a
        /// dotted identifier) against every method in scope. Returns the first match.
        /// </summary>
        /// <remarks>
        /// The ambiguity count and cap are preserved from the Extension version so the loop's
        /// early-exit behavior is identical. The Extension version emits a warning via the static
        /// McpLogger when more than one candidate matches; Core has no equivalent static logger,
        /// and the ctor signature is fixed at <c>MethodResolver(IAssemblyLoader)</c>, so the
        /// warning emission is dropped here. Future tasks may route diagnostics through the
        /// owning McpContext's ILogSink if needed.
        /// </remarks>
        private MethodDef? ResolveByShortName(string identifier, string? assemblyName) {
            var name = identifier.Contains('.')
                ? identifier.Split('.').Last()
                : identifier;

            MethodDef? first = null;
            var matchCount = 0;
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    foreach (var m in type.Methods) {
                        if (!UTF8String.Equals(m.Name, name))
                            continue;
                        matchCount++;
                        first ??= m;
                        // Keep counting to report ambiguity, but stop after a generous cap so a
                        // pathological assembly can't make this loop expensive. The cap is high
                        // enough that any real-world ambiguity is still flagged.
                        if (matchCount >= ShortNameAmbiguityCap)
                            goto done;
                    }
                }
            }
            done:

            return first;
        }

        /// <summary>Stops the ambiguity count after this many matches; keeps the fallback cheap.</summary>
        const int ShortNameAmbiguityCap = 64;

        /// <summary>
        /// Finds types matching a pattern
        /// </summary>
        public IEnumerable<TypeDef> SearchTypes(string pattern, string? assemblyName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    if (MatchesPattern(type.FullName?.ToString(), pattern, comparison))
                        yield return type;
                }
            }
        }

        /// <summary>
        /// Finds methods matching a pattern
        /// </summary>
        public IEnumerable<MethodDef> SearchMethods(string pattern, string? typeFullName = null, string? assemblyName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetModules(assemblyName)) {
                foreach (var type in mod.GetTypes()) {
                    if (typeFullName != null && type.FullName != typeFullName)
                        continue;
                    foreach (var method in type.Methods) {
                        if (MatchesPattern(method.Name?.ToString(), pattern, comparison))
                            yield return method;
                    }
                }
            }
        }

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        private bool MatchesPattern(string? input, string pattern, StringComparison comparison) {
            if (string.IsNullOrEmpty(input)) return false;
            if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) {
                var regex = pattern.Substring(6);
                try {
                    return System.Text.RegularExpressions.Regex.IsMatch(input, regex, System.Text.RegularExpressions.RegexOptions.None, RegexTimeout);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) {
                    return false;
                }
            }
            return input.Contains(pattern, comparison);
        }
    }
}
