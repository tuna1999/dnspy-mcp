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
    // MCP SDK 1.4.0 dispatches messages on one stdio connection concurrently, so
    // Load/Close/GetDocuments can interleave (the repo already serializes mutations
    // for the same reason — see MutationLockFilter). A plain Dictionary is not safe
    // for concurrent read-during-write; one lock covers all three operations.
    private readonly object _lock = new();
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
        lock (_lock) {
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
    }

    public int Close(string assemblyName) {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return 0;

        List<LoadedModule> removed;
        lock (_lock) {
            var matches = _byPath
                .Where(kv => string.Equals(kv.Value.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kv.Value.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Key: kv.Key, Value: kv.Value))
                .ToList();
            foreach (var m in matches)
                _byPath.Remove(m.Key);
            removed = matches.Select(m => m.Value).ToList();
        }

        // Dispose OUTSIDE the lock: module disposal does file/metadata teardown we
        // don't want to hold the registry lock over.
        //
        // Disposing actually releases the module: ModuleDefMD memory-maps the file on
        // Windows, and only Dispose() unmaps it. Without this, close_assembly left the
        // DLL file-locked for the process lifetime (breaking close → rebuild cycles)
        // and leaked metadata in long-lived stdio servers.
        //
        // Known hazard (same one dnSpy's own document service accepts): if another
        // loaded module already resolved references into this one, those references go
        // stale. Cross-module analysis after closing a dependency is undefined either way.
        foreach (var loaded in removed) {
            try {
                ((AssemblyResolver)_moduleContext.AssemblyResolver).Remove(loaded.Module);
            }
            catch {
                // Best-effort cache eviction; a miss must not block the unload.
            }
            try {
                loaded.Module.Dispose();
            }
            catch {
                // Dispose failure must not report the close as failed — the registry
                // entry is already gone.
            }
        }

        return removed.Count;
    }

    public IReadOnlyList<LoadedModule> GetDocuments() {
        lock (_lock)
            return _byPath.Values.ToList();
    }
}
