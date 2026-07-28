using System;
using System.Collections.Generic;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class StubAssemblyLoader : IAssemblyLoader {
    public LoadResult Load(string path) =>
        new(false, "StubAssemblyLoader not yet implemented", null);
    public int Close(string assemblyName) => 0;
    public IReadOnlyList<LoadedModule> GetDocuments() => Array.Empty<LoadedModule>();
}
