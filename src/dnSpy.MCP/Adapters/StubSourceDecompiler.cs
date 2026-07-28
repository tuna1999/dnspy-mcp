using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class StubSourceDecompiler : ISourceDecompiler {
    public string DecompileMethod(MethodDef method) => "stub";
    public string DecompileType(TypeDef type) => "stub";
    public string DecompileField(FieldDef field) => "stub";
    public string DecompileProperty(PropertyDef property) => "stub";
    public string DecompileEvent(EventDef ev) => "stub";
    public string DecompileModule(ModuleDef module) => "stub";
}
