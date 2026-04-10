using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.MCP.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace dnSpy.MCP.Tools {
    /// <summary>Thrown when patch compilation fails with a user-facing error.</summary>
    public sealed class PatchCompileException : Exception {
        public PatchCompileException(string message) : base(message) { }
    }

    public static class IlTools {
        [Description("Returns formatted IL opcodes for a method with line numbers. Input accepts full name, token, or partial method name.")]
        public static string GetIlOpcodesFormatted(string methodFullnameOrToken) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: DocumentService not available.";

            var resolver = new MethodResolver(documentService);
            var method = ResolveMethod(resolver, methodFullnameOrToken);
            if (method == null)
                return $"Method not found: {methodFullnameOrToken}";

            if (method.Body == null)
                return $"Method has no body: {method.FullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"// IL for {method.DeclaringType?.FullName}::{method.Name}");
            sb.AppendLine("// #   Offset  OpCode            Operand");
            sb.AppendLine("// --------------------------------------------------------------");

            for (int i = 0; i < method.Body.Instructions.Count; i++) {
                var ins = method.Body.Instructions[i];
                var operand = ins.Operand?.ToString() ?? "";
                sb.AppendLine($"{i,3}  {ins.Offset:X4}    {ins.OpCode.Name,-16} {operand}");
            }

            return sb.ToString();
        }

        [Description("Patch method body using C# statements. By default dryRun=true. Example methodBody: Console.WriteLine(\"patched\"); return 1;")]
        public static string UpdateMethodBody(
            [Description("Method identifier: full name, token, or partial name")] string methodFullnameOrToken,
            [Description("C# statements for method body only")] string methodBody,
            [Description("If true, only validates and previews without modifying IL")] bool dryRun = true) {

            var documentService = DnSpyContext.DocumentService;
            if (documentService == null)
                return "Error: DocumentService not available.";

            if (string.IsNullOrWhiteSpace(methodBody))
                return "Error: methodBody is required.";

            var resolver = new MethodResolver(documentService);
            var method = ResolveMethod(resolver, methodFullnameOrToken);
            if (method == null)
                return $"Method not found: {methodFullnameOrToken}";

            if (method.Body == null)
                return $"Method has no body: {method.FullName}";

            var module = method.Module;
            if (module == null)
                return "Error: Method module not available.";

            CilBody? clonedBody;
            try {
                clonedBody = BuildClonedBodyFromPatch(method, methodBody);
            }
            catch (PatchCompileException ex) {
                return ex.Message;
            }
            catch (Exception ex) {
                return $"Error building patch: {ex.GetType().Name} — {ex.Message}";
            }

            if (clonedBody == null)
                return "Error: Compiled patch method has no body.";

            if (dryRun) {
                return $"[DRY RUN] Patch compile succeeded for {method.FullName}. New instruction count: {clonedBody.Instructions.Count}";
            }

            // Atomic swap: build the new body fully before assigning it to the method.
            // This prevents leaving the original method body in a corrupted state on failure.
            method.Body = clonedBody;
            DnSpyContext.TreeView?.TreeView?.RefreshAllNodes();
            return $"Patched method body: {method.FullName}";
        }

        private static MethodDef? ResolveMethod(MethodResolver resolver, string methodFullnameOrToken) {
            MethodDef? method = null;

            if (methodFullnameOrToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                var hex = methodFullnameOrToken.Substring(2);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int token))
                    method = resolver.ResolveMethodByToken(token);
            } else if (int.TryParse(methodFullnameOrToken, out int plainToken)) {
                method = resolver.ResolveMethodByToken(plainToken);
            }

            if (method == null)
                method = resolver.ResolveMethod(methodFullnameOrToken);

            if (method == null) {
                var name = methodFullnameOrToken.Contains('.')
                    ? methodFullnameOrToken.Split('.').Last()
                    : methodFullnameOrToken;

                foreach (var mod in resolver.GetAllModules()) {
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

        // ---------------------------------------------------------------------------
        // Roslyn compilation
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<string, string> s_csharpTypeMap = new(StringComparer.Ordinal) {
            { "System.Int32", "int" }, { "System.Int64", "long" },
            { "System.Int16", "short" }, { "System.Byte", "byte" },
            { "System.SByte", "sbyte" }, { "System.Boolean", "bool" },
            { "System.Char", "char" }, { "System.String", "string" },
            { "System.Object", "object" }, { "System.Void", "void" },
            { "System.Single", "float" }, { "System.Double", "double" },
            { "System.Decimal", "decimal" },
            { "System.UInt32", "uint" }, { "System.UInt64", "ulong" },
            { "System.UInt16", "ushort" },
        };

        private static string NormalizeMetadataTypeName(string name) {
            // dnlib generic arity syntax: List`1 -> List
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name.Substring(0, tickIndex);
            return name.Replace('/', '.');
        }

        private static string ToCSharpTypeName(TypeSig? type) {
            if (type == null)
                return "object";

            // Handle byref (ref/out parameters): "int&" -> "ref int"
            if (type.IsByRef) {
                var elementType = type.Next;
                if (elementType != null && elementType.IsPointer)
                    return ToCSharpTypeName(elementType.Next) + "*";
                return "ref " + ToCSharpTypeName(elementType);
            }

            // Handle pointer types
            if (type.IsPointer) {
                var element = type.Next;
                return element != null ? ToCSharpTypeName(element) + "*" : "void*";
            }

            // Handle arrays
            if (type.IsSZArray)
                return ToCSharpTypeName(type.Next) + "[]";

            if (type is ArraySig arraySig) {
                var rank = Math.Max(1, (int)arraySig.Rank);
                var commas = new string(',', rank - 1);
                return ToCSharpTypeName(arraySig.Next) + $"[{commas}]";
            }

            // Handle generic instantiations: List`1<int> -> List<int>
            if (type.IsGenericInstanceType) {
                var genericType = (GenericInstSig)type;
                // ToCSharpTypeName already normalizes; strip backtick suffix if GenericType is a type ref
                var baseName = NormalizeMetadataTypeName(genericType.GenericType.FullName ?? "object");
                var args = string.Join(", ", genericType.GenericArguments.Select(ToCSharpTypeName));
                return $"{baseName}<{args}>";
            }

            // Handle standalone generic type/method parameters: `0, !!0, etc.
            if (type.IsGenericParameter)
                return "object";

            var fullName = type.FullName;
            if (string.IsNullOrEmpty(fullName))
                return "object";
            if (s_csharpTypeMap.TryGetValue(fullName, out var mapped))
                return mapped;
            return NormalizeMetadataTypeName(fullName);
        }

        private static string ToCSharpTypeName(string? fullName) {
            if (string.IsNullOrEmpty(fullName))
                return "object";
            if (s_csharpTypeMap.TryGetValue(fullName, out var mapped))
                return mapped;
            return fullName.Replace('/', '.');
        }

        private static string BuildPatchSource(MethodDef method, string methodBody) {
            var parameters = new List<string>();

            // Instance method: always include @this. For value types it must be "ref T @this"
            // because structs are passed byref in IL (ldarga / starga).
            if (!method.IsStatic && method.DeclaringType != null) {
                var thisType = ToCSharpTypeName(method.DeclaringType.ToTypeSig());
                if (method.DeclaringType.IsValueType)
                    parameters.Add($"ref {thisType} @this");
                else
                    parameters.Add($"{thisType} @this");
            }

            // Regular parameters
            foreach (var p in method.Parameters.Where(p => !p.IsHiddenThisParameter)) {
                var paramName = p.Name?.ToString() ?? $"arg{p.Index}";
                var paramType = ToCSharpTypeName(p.Type);
                parameters.Add($"{paramType} {MakeSafeIdentifier(paramName)}");
            }

            // Return type
            var returnType = ToCSharpTypeName(method.ReturnType);

            var signatureParams = string.Join(", ", parameters);

            return $@"using System;
public static class __Patch {{
    public static {returnType} PatchedMethod({signatureParams}) {{
        {methodBody}
    }}
}}";
        }

        private static string MakeSafeIdentifier(string name) {
            if (string.IsNullOrWhiteSpace(name))
                return "arg";

            var sb = new StringBuilder(name.Length + 1);
            if (!(char.IsLetter(name[0]) || name[0] == '_'))
                sb.Append('_');

            foreach (var ch in name) {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            }

            var normalized = sb.ToString();
            if (SyntaxFacts.GetKeywordKind(normalized) != SyntaxKind.None ||
                SyntaxFacts.GetContextualKeywordKind(normalized) != SyntaxKind.None) {
                return "@" + normalized;
            }
            return normalized;
        }

        /// <summary>Loads Roslyn metadata references for runtime patch compilation.</summary>
        private static List<MetadataReference> BuildRoslynReferences() {
            var refs = new List<MetadataReference>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? path) {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;
                if (!addedPaths.Add(path))
                    return;
                refs.Add(MetadataReference.CreateFromFile(path));
            }

            // Prefer full TPA set to avoid missing core references (eg. System.Runtime)
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpaList) {
                foreach (var path in tpaList.Split(Path.PathSeparator)) {
                    TryAdd(path);
                }
            }

            // Fallback/extra anchors for environments where TPA is incomplete
            var assemblies = new[] {
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(List<>).Assembly,
                typeof(Enumerable).Assembly,
                typeof(File).Assembly,
                typeof(DescriptionAttribute).Assembly,
            };

            foreach (var asm in assemblies) {
                try {
                    TryAdd(asm.Location);
                }
                catch {
                    // Skip assemblies without a file location (dynamic assemblies)
                }
            }

            return refs;
        }

        private static MethodDef? CompilePatch(MethodDef targetMethod, string methodBody) {
            var source = BuildPatchSource(targetMethod, methodBody);

            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            // Start with common BCL references, then add the target module's assembly
            var references = BuildRoslynReferences();
            var targetModule = targetMethod.Module;
            if (!string.IsNullOrWhiteSpace(targetModule?.Location) && File.Exists(targetModule.Location)) {
                references.Add(MetadataReference.CreateFromFile(targetModule.Location));
            }

            var compilation = CSharpCompilation.Create(
                "__PatchAsm",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var ms = new MemoryStream();
            try {
                var result = compilation.Emit(ms);
                if (!result.Success) {
                    var errors = string.Join("\n", result.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString()));
                    throw new PatchCompileException($"Compilation errors:\n{errors}");
                }

                // Extract the compiled IL bytes and load a patch module from them
                var patchModule = ModuleDefMD.Load(ms.ToArray());
                var patchType = patchModule.Types.FirstOrDefault(t => t.Name == "__Patch");
                if (patchType == null)
                    throw new PatchCompileException("Cannot find compiled __Patch type.");

                var patchMethod = patchType.Methods.FirstOrDefault(m => m.Name != ".ctor" && m.Name != ".cctor");
                if (patchMethod == null)
                    throw new PatchCompileException("No user method found in compiled patch module.");

                return patchMethod;
            }
            finally {
                ms.Dispose();
            }
        }

        // ---------------------------------------------------------------------------
        // CilBody construction
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Builds a new CilBody from the compiled patch method.
        /// Uses dnlib's Importer to safely cross-reference type/member tokens
        /// from the patch module into the target module.
        /// </summary>
        private static CilBody BuildClonedBodyFromPatch(MethodDef targetMethod, string methodBody) {
            var patchMethod = CompilePatch(targetMethod, methodBody);
            if (patchMethod?.Body == null)
                throw new PatchCompileException("Compiled patch method has no body.");

            var patchBody = patchMethod.Body;
            var targetModule = targetMethod.Module;

            // Importer maps patch-module references into the target module's metadata tables.
            // TryToUseDefs: prefer existing TypeDef/MethodDef/FieldDef entries in the target
            // module over creating new TypeRef/MemberRef rows (avoids duplicate metadata tokens).
            var importer = new Importer(targetModule, ImporterOptions.TryToUseDefs);

            var instructionMap = new Dictionary<Instruction, Instruction>();

            // Build a new CilBody rather than mutating the existing one.
            // This keeps the original body intact until the swap is complete.
            var newBody = new CilBody(patchBody.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>());

            // Pass 1: clone all instructions and build the map.
            // Branch operands are deferred to pass 2 — they cannot be resolved yet
            // because forward targets haven't been added to the map.
            foreach (var src in patchBody.Instructions) {
                var dst = CreateInstructionClone(src, importer);

                newBody.Instructions.Add(dst);
                instructionMap[src] = dst;
            }

            // Pass 2: remap all branch targets using the instruction map.
            // Both single-target (br, brfalse.s, etc.) and multi-target (switch) are handled.
            for (int i = 0; i < patchBody.Instructions.Count; i++) {
                var src = patchBody.Instructions[i];
                var dst = newBody.Instructions[i];

                if (src.Operand is Instruction srcTarget && instructionMap.TryGetValue(srcTarget, out var remappedTarget)) {
                    dst.Operand = remappedTarget;
                }
                else if (src.Operand is Instruction[] srcTargets) {
                    var remapped = new Instruction[srcTargets.Length];
                    for (int j = 0; j < srcTargets.Length; j++) {
                        remapped[j] = instructionMap.TryGetValue(srcTargets[j], out var m) ? m : srcTargets[j];
                    }
                    dst.Operand = remapped;
                }
            }

            // Import and clone local variable types (must import the TypeSig into the target module)
            foreach (var v in patchBody.Variables) {
                var importedType = importer.Import(v.Type);
                newBody.Variables.Add(new Local(importedType));
            }

            // Import and clone exception handlers
            foreach (var eh in patchBody.ExceptionHandlers) {
                var clone = new ExceptionHandler(eh.HandlerType) {
                    CatchType = eh.CatchType != null ? importer.Import(eh.CatchType) : null,
                    TryStart     = eh.TryStart     != null && instructionMap.TryGetValue(eh.TryStart,     out var ts) ? ts : null,
                    TryEnd       = eh.TryEnd       != null && instructionMap.TryGetValue(eh.TryEnd,       out var te) ? te : null,
                    HandlerStart = eh.HandlerStart != null && instructionMap.TryGetValue(eh.HandlerStart, out var hs) ? hs : null,
                    HandlerEnd   = eh.HandlerEnd   != null && instructionMap.TryGetValue(eh.HandlerEnd,   out var he) ? he : null,
                    FilterStart  = eh.FilterStart  != null && instructionMap.TryGetValue(eh.FilterStart,  out var fs) ? fs : null,
                };
                newBody.ExceptionHandlers.Add(clone);
            }

            // Keep branch layout as cloned to preserve EH boundaries.
            // Branch simplification/optimization can rewrite instruction targets and
            // invalidate exception handler anchors if done after EH mapping.
            newBody.UpdateInstructionOffsets();

            return newBody;
        }

        private static Instruction CreateInstructionClone(Instruction src, Importer importer) {
            var operand = ImportOperand(importer, src.Operand);

            return operand switch {
                null => Instruction.Create(src.OpCode),

                // Branch/switch: must be created with Instruction/Instruction[] operand,
                // then remapped to cloned targets in pass 2.
                Instruction target => Instruction.Create(src.OpCode, target),
                Instruction[] targets => Instruction.Create(src.OpCode, targets),

                sbyte v => Instruction.Create(src.OpCode, v),
                byte v => Instruction.Create(src.OpCode, v),
                int v => Instruction.Create(src.OpCode, v),
                long v => Instruction.Create(src.OpCode, v),
                float v => Instruction.Create(src.OpCode, v),
                double v => Instruction.Create(src.OpCode, v),
                string v => Instruction.Create(src.OpCode, v),
                UTF8String v => Instruction.Create(src.OpCode, v),
                Local v => Instruction.Create(src.OpCode, v),
                Parameter v => Instruction.Create(src.OpCode, v),
                ITypeDefOrRef v => Instruction.Create(src.OpCode, v),
                IMethod v => Instruction.Create(src.OpCode, v),
                IField v => Instruction.Create(src.OpCode, v),

                _ => throw new PatchCompileException($"Unsupported IL operand type: {operand.GetType().FullName} ({src.OpCode})"),
            };
        }

        /// <summary>
        /// Imports an operand value into the target module using dnlib's Importer.
        /// Safe operands (primitives, strings, instructions, null) are returned unchanged.
        /// Member references, type references, and field references are resolved in the
        /// target module's metadata tables so they remain valid after the patch is applied.
        /// </summary>
        private static object? ImportOperand(Importer importer, object? operand) {
            return operand switch {
                null                            => null,
                Instruction                     => operand,  // handled in pass 2
                Instruction[]                   => operand,  // handled in pass 2
                ITypeDefOrRef tdor              => importer.Import(tdor),
                IMethodDefOrRef mdor            => importer.Import(mdor),
                IField ifd                      => importer.Import(ifd),
                _                               => operand,  // primitives, byte[], string, etc.
            };
        }
    }
}
