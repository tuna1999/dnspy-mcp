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
        /// Resolves a method by full name (e.g., "Namespace.Class::Method")
        /// </summary>
        public MethodDef? ResolveMethod(string fullName) {
            foreach (var mod in GetAllModules()) {
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
        public MethodDef? ResolveMethodByToken(int token) {
            foreach (var mod in GetAllModules()) {
                var resolved = mod.ResolveToken(token);
                if (resolved is MethodDef method)
                    return method;
            }
            return null;
        }

        /// <summary>
        /// Resolves a type by full name
        /// </summary>
        public TypeDef? ResolveType(string fullName) {
            foreach (var mod in GetAllModules()) {
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
        public TypeDef? ResolveTypeByToken(int token) {
            foreach (var mod in GetAllModules()) {
                var resolved = mod.ResolveToken(token);
                if (resolved is TypeDef type)
                    return type;
            }
            return null;
        }

        /// <summary>
        /// Finds types matching a pattern
        /// </summary>
        public IEnumerable<TypeDef> SearchTypes(string pattern, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetAllModules()) {
                foreach (var type in mod.GetTypes()) {
                    if (MatchesPattern(type.FullName?.ToString(), pattern, comparison))
                        yield return type;
                }
            }
        }

        /// <summary>
        /// Finds methods matching a pattern
        /// </summary>
        public IEnumerable<MethodDef> SearchMethods(string pattern, string? typeFullName = null, bool caseSensitive = false) {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var mod in GetAllModules()) {
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

        private bool MatchesPattern(string input, string pattern, StringComparison comparison) {
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
