using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless assembly loader using dnlib directly. Mirrors dnSpy.Console/Program.cs
/// setup (ModuleDef.CreateModuleContext + AssemblyResolver config).
/// </summary>
public sealed class DnlibAssemblyLoader : IAssemblyLoader {
    private readonly Dictionary<string, LoadedModule> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModuleContext _moduleContext;

    public DnlibAssemblyLoader() {
        _moduleContext = ModuleDef.CreateModuleContext();
        var resolver = (AssemblyResolver)_moduleContext.AssemblyResolver;
        resolver.EnableFrameworkRedirect = false;
        resolver.FindExactMatch = true;
        resolver.EnableTypeDefCache = true;
    }

    public LoadResult Load(string path) {
        if (string.IsNullOrWhiteSpace(path))
            return new LoadResult(false, "Path is required", null);
        if (!File.Exists(path))
            return new LoadResult(false, $"File not found: {path}", null);

        // Use the full (normalized) path as the key — basename-only would collide when
        // two DLLs in different folders happen to share a name (e.g. comparing utils.dll
        // from folder A vs folder B). IAssemblyLoader consumers expect each load to be
        // distinct; silent dedup is a foot-gun for analyst workflows.
        var key = Path.GetFullPath(path);
        if (_byPath.TryGetValue(key, out var existing))
            return new LoadResult(true, null, existing);

        try {
            var mod = ModuleDefMD.Load(path, _moduleContext);
            mod.EnableTypeDefFindCache = true;
            ((AssemblyResolver)_moduleContext.AssemblyResolver).AddToCache(mod);

            var loaded = new LoadedModule(
                Name: mod.Name,
                AssemblyName: mod.Assembly?.Name?.String,
                Module: mod,
                Path: path);
            _byPath[key] = loaded;
            return new LoadResult(true, null, loaded);
        }
        catch (Exception ex) {
            return new LoadResult(false, ex.Message, null);
        }
    }

    public int Close(string assemblyName) {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return 0;

        var matches = _byPath
            .Where(kv => string.Equals(kv.Value.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Value.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in matches)
            _byPath.Remove(key);

        return matches.Count;
    }

    public IReadOnlyList<LoadedModule> GetDocuments() => _byPath.Values.ToList();
}
