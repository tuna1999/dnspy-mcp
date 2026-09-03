using System;
using System.Linq;
using System.Reflection;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Reflection-based loader for dnSpy's C# ILSpy decompiler. Mirrors the pattern in
/// dnSpy.Console/Program.cs:226-247 — load dnSpy.Decompiler.ILSpy.Core, find
/// IDecompilerProvider implementations, instantiate via default ctor, and return the
/// C# (ILSpy) language.
///
/// The resulting IDecompiler is wrapped by DnSpyDecompilerSourceProvider in Core so
/// Headless produces decompiled output identical to dnSpy.exe.
/// </summary>
public static class DnSpyDecompilerLoader {
    private const string ILSpyCoreAssemblyName = "dnSpy.Decompiler.ILSpy.Core";

    /// <summary>
    /// Loads the C# ILSpy decompiler. Throws InvalidOperationException if no
    /// IDecompilerProvider can be found or no C# language is available.
    /// </summary>
    public static IDecompiler LoadCSharp() {
        var asm = TryLoad(ILSpyCoreAssemblyName)
            ?? throw new InvalidOperationException(
                $"Could not load {ILSpyCoreAssemblyName}.dll — ensure dnSpy decompiler " +
                "binaries are deployed alongside the host.");

        var languages = GetAllLanguages(asm).ToList();
        if (languages.Count == 0)
            throw new InvalidOperationException(
                "No IDecompilerProvider implementations found in " + ILSpyCoreAssemblyName);

        var csharp = languages.FirstOrDefault(d =>
            d.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY
            || d.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
            ?? languages.FirstOrDefault(d =>
            d.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP
            || d.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP);

        return csharp ?? throw new InvalidOperationException(
            "No C# decompiler language available in " + ILSpyCoreAssemblyName);
    }

    private static System.Collections.Generic.IEnumerable<IDecompiler> GetAllLanguages(Assembly asm) {
        foreach (var type in asm.GetTypes()) {
            if (type.IsAbstract || type.IsInterface)
                continue;
            if (!typeof(IDecompilerProvider).IsAssignableFrom(type))
                continue;

            IDecompilerProvider provider;
            try {
                // IDecompilerProvider contract requires a default constructor.
                provider = (IDecompilerProvider)Activator.CreateInstance(type)!;
            }
            catch {
                continue;
            }

            foreach (var lang in provider.Create())
                yield return lang;
        }
    }

    private static Assembly? TryLoad(string asmName) {
        try {
            return Assembly.Load(asmName);
        }
        catch {
            return null;
        }
    }
}
