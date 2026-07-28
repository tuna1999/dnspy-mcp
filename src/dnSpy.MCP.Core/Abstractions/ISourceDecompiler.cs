using dnlib.DotNet;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Produces C# source text from dnlib entities. Implementations bridge to dnSpy's
/// IDecompiler (shared DnSpyDecompilerSourceProvider) so output is identical to dnSpy.exe.
/// </summary>
public interface ISourceDecompiler {
    string DecompileMethod(MethodDef method);
    string DecompileType(TypeDef type);
    string DecompileField(FieldDef field);
    string DecompileProperty(PropertyDef property);
    string DecompileEvent(EventDef ev);
    string DecompileModule(ModuleDef module);
}
