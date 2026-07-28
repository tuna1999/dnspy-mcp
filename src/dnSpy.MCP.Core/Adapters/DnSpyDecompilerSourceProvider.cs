using System;
using System.IO;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Core.Adapters;

/// <summary>
/// Bridges ISourceDecompiler to dnSpy's IDecompiler. Used by BOTH Extension and Headless.
/// Composition root supplies the IDecompiler instance (Extension via MEF, Headless via
/// reflection load of IDecompilerProvider — see DnSpyDecompilerLoader).
/// Output format is identical to dnSpy.exe because we delegate to the same IDecompiler.
/// </summary>
public sealed class DnSpyDecompilerSourceProvider : ISourceDecompiler {
    private readonly IDecompiler _decompiler;
    private static readonly Indenter DefaultIndenter = new(4, 4, true);

    public DnSpyDecompilerSourceProvider(IDecompiler decompiler) {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
    }

    public string DecompileMethod(MethodDef method) =>
        DecompileCore((d, o, c) => d.Decompile(method, o, c));

    public string DecompileType(TypeDef type) =>
        DecompileCore((d, o, c) => d.Decompile(type, o, c));

    public string DecompileField(FieldDef field) =>
        DecompileCore((d, o, c) => d.Decompile(field, o, c));

    public string DecompileProperty(PropertyDef property) =>
        DecompileCore((d, o, c) => d.Decompile(property, o, c));

    public string DecompileEvent(EventDef ev) =>
        DecompileCore((d, o, c) => d.Decompile(ev, o, c));

    public string DecompileModule(ModuleDef module) =>
        DecompileCore((d, o, c) => d.Decompile(module, o, c));

    private string DecompileCore(
        Action<IDecompiler, IDecompilerOutput, DecompilationContext> decompose) {
        var writer = new StringWriter();
        using var output = new TextWriterDecompilerOutput(writer, DefaultIndenter);
        decompose(_decompiler, output, new DecompilationContext());
        return writer.ToString();
    }
}
