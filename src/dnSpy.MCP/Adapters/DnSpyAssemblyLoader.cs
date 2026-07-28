using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

/// <summary>
/// Wraps IDsDocumentService for Extension use. Document collection mutations
/// (Load, Close) marshal to the UI thread because they drive TreeView updates.
/// </summary>
internal sealed class DnSpyAssemblyLoader : IAssemblyLoader {
    private readonly IDsDocumentService _documentService;
    private readonly IUIThreadScheduler _ui;

    public DnSpyAssemblyLoader(IDsDocumentService documentService, IUIThreadScheduler ui) {
        _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public LoadResult Load(string path) {
        if (string.IsNullOrWhiteSpace(path))
            return new LoadResult(false, "Path is required", null);
        if (!File.Exists(path))
            return new LoadResult(false, $"File not found: {path}", null);

        IDsDocument? doc = null;
        _ui.Invoke(() => {
            doc = _documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(path));
        });

        if (doc?.ModuleDef is ModuleDef mod) {
            var loaded = new LoadedModule(
                Name: mod.Name,
                AssemblyName: mod.Assembly?.Name?.String,
                Module: mod,
                Path: path);
            return new LoadResult(true, null, loaded);
        }
        return new LoadResult(false, "Failed to load (TryGetOrCreate returned null or no ModuleDef)", null);
    }

    public int Close(string assemblyName) {
        var toRemove = _documentService.GetDocuments()
            .Where(d => {
                if (d.ModuleDef is not ModuleDef mod) return false;
                var name = mod.Assembly?.Name?.String ?? mod.Name;
                return string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (toRemove.Count > 0)
            _ui.Invoke(() => _documentService.Remove(toRemove));
        return toRemove.Count;
    }

    public IReadOnlyList<LoadedModule> GetDocuments() {
        return _documentService.GetDocuments()
            .Where(d => d.ModuleDef is not null)
            .Select(d => {
                var mod = (ModuleDef)d.ModuleDef!;
                return new LoadedModule(
                    Name: mod.Name,
                    AssemblyName: mod.Assembly?.Name?.String,
                    Module: mod,
                    Path: mod.Location ?? "");
            })
            .ToList();
    }
}
