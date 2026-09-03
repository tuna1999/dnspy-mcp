using System;
using System.Collections.Generic;
using dnlib.DotNet;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Loads and tracks .NET modules. Implementations may wrap dnSpy's IDsDocumentService
/// (Extension) or use ModuleDefMD.Load directly (Headless).
/// </summary>
public interface IAssemblyLoader {
    /// <summary>Load by absolute path. Idempotent by filename key.</summary>
    LoadResult Load(string path);

    /// <summary>Remove by simple assembly name (case-insensitive). Returns count removed.</summary>
    int Close(string assemblyName);

    /// <summary>All currently loaded modules.</summary>
    IReadOnlyList<LoadedModule> GetDocuments();
}

public sealed record LoadResult(bool Success, string? Error, LoadedModule? Module);

/// <summary>
/// Immutable wrapper around a loaded dnlib ModuleDef. The Module property is itself mutable;
/// in-place IL mutations (e.g. after update_method_body) are visible through it.
/// </summary>
public sealed record LoadedModule(string Name, string? AssemblyName, ModuleDef Module, string Path);
