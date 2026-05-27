using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Helpers {
    /// <summary>
    /// Resolves methods and types by full name or token
    /// </summary>
    public sealed class MethodResolver {
        private readonly IDsDocumentService documentService;

        public MethodResolver(IDsDocumentService documentService) {
            this.documentService = documentService;
        }

        /// <summary>
        /// Gets the first module from loaded documents
        /// </summary>
        public ModuleDef? GetCurrentModule() {
            var docs = documentService.GetDocuments();
            foreach (var doc in docs) {
                if (doc.ModuleDef is ModuleDef mod)
                    return mod;
            }
            return null;
        }

        /// <summary>
        /// Gets all loaded modules
        /// </summary>
        public IEnumerable<ModuleDef> GetAllModules() {
            var docs = documentService.GetDocuments();
            foreach (var doc in docs) {
                if (doc.ModuleDef is ModuleDef mod)
                    yield return mod;
            }
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

            if (method == null) {
                var name = identifier.Contains('.')
                    ? identifier.Split('.').Last()
                    : identifier;
                foreach (var mod in GetModules(assemblyName)) {
                    foreach (var type in mod.GetTypes()) {
                        foreach (var m in type.Methods) {
                            if (UTF8String.Equals(m.Name, name))
                                return m;
                        }
                    }
                }
            }

            return method;
        }

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
