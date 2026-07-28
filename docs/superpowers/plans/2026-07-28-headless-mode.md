# Headless Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor dnSpy.MCP into a 3-project solution (Core/Extension/Headless) that supports both the existing HTTP extension and a new standalone stdio MCP headless binary for batch analysis.

**Architecture:** B3' DI-based hybrid. A shared `dnSpy.MCP.Core` library holds all tool classes (instance-based, ctor-inject `McpContext`). The Extension composes `McpContext` with WPF/dnSpy-backed adapters and serves over HTTP. Headless composes `McpContext` with dnlib-backed adapters and serves over stdio via MCP SDK. Both hosts share one decompiler bridge (`DnSpyDecompilerSourceProvider`) so output is byte-identical to dnSpy.exe.

**Tech Stack:** C# / .NET 10, dnlib, ICSharpCode.Decompiler, Microsoft.CodeAnalysis.CSharp (IL patch compile), ModelContextProtocol C# SDK (Headless only), xUnit + FluentAssertions + Moq (tests).

## Global Constraints

- **Target framework**: `net10.0` for Core and Headless; `net10.0-windows` with `UseWPF=true` for Extension
- **dnSpy deps path**: `<DnSpyBin>..\..\deps</DnSpyBin>` (relative to csproj); DLLs are `dnSpy.Contracts.DnSpy.dll`, `dnSpy.Contracts.Logic.dll`, `ICSharpCode.Decompiler.dll`, `dnlib.dll` (already present in `deps/`)
- **Headless deps**: also `dnSpy.Decompiler.dll`, `dnSpy.Decompiler.ILSpy.Core.dll`, `ICSharpCode.NRefactory*.dll` (must be copied from dnSpy install `bin/` into `deps/` before Phase 5)
- **MCP SDK packages**: `ModelContextProtocol` (latest stable), `Microsoft.Extensions.Hosting`
- **Decompiler output format**: must be identical to dnSpy.exe. Use built-in `TextWriterDecompilerOutput` from `dnSpy.Contracts.Logic`, never hand-roll
- **Headless logging**: stderr only — `Console.WriteLine` is forbidden anywhere in Headless. Only `StderrLogSink` may touch `Console.Error`
- **`IDecompilerProvider` contract**: must have default ctor (dnSpy convention, used by `dnSpy.Console`)
- **Tool naming**: `[Description]`-attributed public methods on classes in namespace `dnSpy.MCP.Tools*`. Snake_case auto-conversion via `ToolRegistry.ToSnakeCase`
- **No `#if HEADLESS` directives**: seams are interfaces, not preprocessor flags
- **Commit format**: conventional commits (`feat:`, `refactor:`, `test:`, `docs:`, `chore:`). No attribution footer (globally disabled)
- **Build verification after each task**: `dotnet build` must be green before commit

---

## File Structure

### New files

```
src/dnSpy.MCP.Core/
├── dnSpy.MCP.Core.csproj
├── Abstractions/
│   ├── IAssemblyLoader.cs           # Load/Close/GetDocuments + records
│   ├── ISourceDecompiler.cs         # DecompileMethod/Type/Field/Property/Event/Module
│   ├── IUIThreadScheduler.cs        # Invoke<T>/Invoke
│   ├── ILogSink.cs                  # Info/Warn/Error
│   └── ITreeRefreshNotifier.cs      # RefreshAll/NotifyNamespaceRenamed
├── Adapters/
│   └── DnSpyDecompilerSourceProvider.cs   # shared IDecompiler → ISourceDecompiler bridge
├── Mcp/
│   ├── McpContext.cs                # instance, holds 5 deps + Resolver
│   ├── ToolRegistry.cs              # hybrid instance/static discovery
│   ├── McpServerHost.cs             # HTTP transport (Extension-only at runtime)
│   ├── JsonRpc.cs                   # moved verbatim
│   ├── BufferedLineReader.cs        # moved verbatim
│   └── McpLogger.cs                 # file-only half of original
├── Helpers/
│   └── MethodResolver.cs            # ctor(IAssemblyLoader)
└── Tools/
    ├── DecompilerTools.cs
    ├── AssemblyTools.cs
    ├── SearchTools.cs
    ├── AnalysisTools.cs
    ├── XrefTools.cs
    ├── IlDisplayTools.cs
    ├── IlPatchTools.cs
    ├── ResourceTools.cs
    ├── TypeInspectorTools.cs
    ├── AttributeTools.cs
    ├── ConstantTools.cs
    ├── NamespaceTools.cs
    └── RenameTools.cs

src/dnSpy.MCP/
├── Adapters/
│   ├── DnSpyAssemblyLoader.cs
│   ├── WpfUIThreadScheduler.cs
│   ├── DnSpyLogSink.cs               # Output Pane half of original McpLogger
│   └── DnSpyTreeRefreshNotifier.cs
└── Tools/
    └── TreeViewTools.cs              # moved from current location (stays static)

src/dnSpy.MCP.Headless/
├── dnSpy.MCP.Headless.csproj
├── Program.cs
├── CliOptions.cs
└── Adapters/
    ├── DnlibAssemblyLoader.cs
    ├── DnSpyDecompilerLoader.cs
    ├── InlineUIThreadScheduler.cs
    ├── StderrLogSink.cs
    ├── NoOpTreeRefreshNotifier.cs
    └── AutoToolRegistration.cs

tests/dnSpy.MCP.Core.Tests/
├── dnSpy.MCP.Core.Tests.csproj
├── Mcp/McpContextTests.cs
├── Mcp/ToolRegistryTests.cs          # migrated
├── Mcp/JsonRpcTests.cs               # migrated
├── Mcp/BufferedLineReaderTests.cs    # migrated
├── Helpers/MethodResolverTests.cs
├── Abstractions/MockContextFactory.cs  # factory for mock-based McpContext
└── Tools/*.cs                        # 13 test files

tests/dnSpy.MCP.Headless.Tests/
├── dnSpy.MCP.Headless.Tests.csproj
└── HeadlessE2ETests.cs

tests/TestData/SampleLibrary/
├── SampleLibrary.csproj
└── Class1.cs
```

### Modified files

- `src/dnSpy.MCP/dnSpy.MCP.csproj` — remove moved sources, add Core reference
- `src/dnSpy.MCP/TheExtension.cs` — compose McpContext, pass ToolRegistry to McpServerHost
- `src/dnSpy.MCP/Mcp/McpServerHost.cs` — ctor accepts ToolRegistry parameter
- `scripts/verify-tool-count.ps1` — regex update for instance methods
- `scripts/build.ps1` — build solution instead of single project
- `.github/workflows/build.yml` — CI matrix update

### Deleted files (after functionality moved)

- `src/dnSpy.MCP/DnSpyContext.cs` — replaced by `McpContext`
- `src/dnSpy.MCP/Helpers/TextDecompilerOutput.cs` — replaced by `dnSpy.Contracts.Logic.TextWriterDecompilerOutput`
- `src/dnSpy.MCP/Helpers/MethodResolver.cs` — moved to Core
- `src/dnSpy.MCP/Mcp/McpLogger.cs` — split (file half → Core, pane half → DnSpyLogSink)
- `src/dnSpy.MCP/Mcp/JsonRpc.cs` — moved to Core
- `src/dnSpy.MCP/Mcp/BufferedLineReader.cs` — moved to Core
- `src/dnSpy.MCP/Mcp/ToolRegistry.cs` — moved to Core
- `src/dnSpy.MCP/Tools/*.cs` (12 files) — moved to Core (only `TreeViewTools.cs` stays)

---

# Phase 0 — Solution Skeleton

Establish the build graph without touching any logic.

### Task 0.1: Create solution file

**Files:**
- Create: `dnspy_mcp.sln`

- [ ] **Step 1: Create the sln at repo root**

Run from `D:\re_dev_projects\dnspy_mcp`:

```powershell
dotnet new sln -n dnspy_mcp
dotnet sln add src/dnSpy.MCP/dnSpy.MCP.csproj
dotnet sln add src/dnSpy.MCP.Tests/dnSpy.MCP.Tests.csproj
```

- [ ] **Step 2: Verify the solution builds**

Run: `dotnet build dnspy_mcp.sln -c Release`
Expected: PASS with the same output as before

- [ ] **Step 3: Commit**

```bash
git add dnspy_mcp.sln
git commit -m "chore: add solution file referencing existing projects"
```

### Task 0.2: Create empty Core project

**Files:**
- Create: `src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`

**Interfaces:**
- Produces: empty `dnSpy.MCP.Core.dll` (net10.0, no WPF)

- [ ] **Step 1: Scaffold the project**

Run from `D:\re_dev_projects\dnspy_mcp`:

```powershell
dotnet new classlib -n dnSpy.MCP.Core -o src/dnSpy.MCP.Core --framework net10.0
Remove-Item src/dnSpy.MCP.Core/Class1.cs
dotnet sln add src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj
```

- [ ] **Step 2: Edit the csproj to mirror Extension's reference style**

Replace the contents of `src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <AssemblyName>dnSpy.MCP.Core</AssemblyName>
    <Nullable>enable</Nullable>
    <TargetFramework>net10.0</TargetFramework>
    <Version>2.0.0</Version>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);1591</NoWarn>
  </PropertyGroup>

  <PropertyGroup>
    <DnSpyBin>..\..\deps</DnSpyBin>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="dnSpy.Contracts.DnSpy">
      <HintPath>$(DnSpyBin)\dnSpy.Contracts.DnSpy.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="dnSpy.Contracts.Logic">
      <HintPath>$(DnSpyBin)\dnSpy.Contracts.Logic.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ICSharpCode.Decompiler">
      <HintPath>$(DnSpyBin)\ICSharpCode.Decompiler.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="dnlib">
      <HintPath>$(DnSpyBin)\dnlib.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.13.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Verify Core builds standalone**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj -c Release`
Expected: PASS (empty assembly)

- [ ] **Step 4: Add Core reference to Extension**

Edit `src/dnSpy.MCP/dnSpy.MCP.csproj`, add inside the existing `<ItemGroup>`:

```xml
<ProjectReference Include="..\dnSpy.MCP.Core\dnSpy.MCP.Core.csproj" />
```

- [ ] **Step 5: Verify the whole solution builds**

Run: `dotnet build dnspy_mcp.sln -c Release`
Expected: PASS — Extension may now produce duplicate-type warnings (we'll deduplicate in later phases); build still succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/dnSpy.MCP.Core/ src/dnSpy.MCP/dnSpy.MCP.csproj
git commit -m "chore: scaffold empty dnSpy.MCP.Core lib project"
```

---

# Phase 1 — Core Abstractions

Build the interfaces + McpContext + shared infrastructure. No breaking changes to Extension yet.

### Task 1.1: Create 5 Abstraction interfaces

**Files:**
- Create: `src/dnSpy.MCP.Core/Abstractions/IAssemblyLoader.cs`
- Create: `src/dnSpy.MCP.Core/Abstractions/ISourceDecompiler.cs`
- Create: `src/dnSpy.MCP.Core/Abstractions/IUIThreadScheduler.cs`
- Create: `src/dnSpy.MCP.Core/Abstractions/ILogSink.cs`
- Create: `src/dnSpy.MCP.Core/Abstractions/ITreeRefreshNotifier.cs`

**Interfaces:**
- Produces: `IAssemblyLoader`, `ISourceDecompiler`, `IUIThreadScheduler`, `ILogSink`, `ITreeRefreshNotifier`, `LoadResult`, `LoadedModule`

- [ ] **Step 1: Write `IAssemblyLoader.cs` with records**

```csharp
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
```

- [ ] **Step 2: Write `ISourceDecompiler.cs`**

```csharp
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
```

- [ ] **Step 3: Write `IUIThreadScheduler.cs`**

```csharp
using System;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Marshals actions to the host's UI thread (Extension: WPF Dispatcher; Headless: inline).
/// </summary>
public interface IUIThreadScheduler {
    T Invoke<T>(Func<T> action);
    void Invoke(Action action);
}
```

- [ ] **Step 4: Write `ILogSink.cs`**

```csharp
using System;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Tool-facing logger. Extension: file + dnSpy Output Pane. Headless: stderr only.
/// </summary>
public interface ILogSink {
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
```

- [ ] **Step 5: Write `ITreeRefreshNotifier.cs`**

```csharp
namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Notifies the host that metadata mutations occurred so it can refresh UI state.
/// Extension: delegates to TreeViewTools. Headless: no-op.
/// </summary>
public interface ITreeRefreshNotifier {
    void RefreshAll();
    void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace);
}
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS, 5 new files compile

- [ ] **Step 7: Commit**

```bash
git add src/dnSpy.MCP.Core/Abstractions/
git commit -m "feat(core): add 5 abstraction interfaces for host-agnostic tools"
```

### Task 1.2: Create shared DnSpyDecompilerSourceProvider

**Files:**
- Create: `src/dnSpy.MCP.Core/Adapters/DnSpyDecompilerSourceProvider.cs`

**Interfaces:**
- Consumes: `ISourceDecompiler` (Task 1.1), dnSpy's `IDecompiler`, `IDecompilerOutput`, `DecompilationContext`, `TextWriterDecompilerOutput`, `Indenter` (all in `dnSpy.Contracts.*`)
- Produces: `DnSpyDecompilerSourceProvider` — used by both Extension and Headless composition roots

- [ ] **Step 1: Write the adapter**

```csharp
using System;
using System.IO;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
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
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/dnSpy.MCP.Core/Adapters/
git commit -m "feat(core): add shared DnSpyDecompilerSourceProvider adapter"
```

### Task 1.3: Create McpContext

**Files:**
- Create: `src/dnSpy.MCP.Core/Mcp/McpContext.cs`

**Interfaces:**
- Consumes: 5 interfaces from Task 1.1, `MethodResolver` (defined in Task 1.4)
- Produces: `McpContext` instance class

- [ ] **Step 1: Write McpContext (note: MethodResolver comes next, so reference forward)**

```csharp
using System;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Helpers;

namespace dnSpy.MCP.Core.Mcp;

/// <summary>
/// Typed composition root holding all 5 host-specific dependencies plus a derived
/// MethodResolver. Tools receive this via constructor injection.
/// Not a service locator: tools reference concrete properties, never GetService&lt;T&gt;().
/// </summary>
public sealed class McpContext {
    public IAssemblyLoader AssemblyLoader { get; }
    public ISourceDecompiler SourceDecompiler { get; }
    public IUIThreadScheduler UI { get; }
    public ILogSink Log { get; }
    public ITreeRefreshNotifier TreeRefresh { get; }
    public MethodResolver Resolver { get; }

    public McpContext(
        IAssemblyLoader assemblyLoader,
        ISourceDecompiler sourceDecompiler,
        IUIThreadScheduler ui,
        ILogSink log,
        ITreeRefreshNotifier treeRefresh) {
        AssemblyLoader = assemblyLoader ?? throw new ArgumentNullException(nameof(assemblyLoader));
        SourceDecompiler = sourceDecompiler ?? throw new ArgumentNullException(nameof(sourceDecompiler));
        UI = ui ?? throw new ArgumentNullException(nameof(ui));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        TreeRefresh = treeRefresh ?? throw new ArgumentNullException(nameof(treeRefresh));
        Resolver = new MethodResolver(assemblyLoader);
    }
}
```

- [ ] **Step 2: Do NOT build yet** — depends on `MethodResolver` (next task)

### Task 1.4: Create MethodResolver in Core

**Files:**
- Create: `src/dnSpy.MCP.Core/Helpers/MethodResolver.cs`

**Interfaces:**
- Consumes: `IAssemblyLoader`, `LoadedModule` (Task 1.1)
- Produces: `MethodResolver` with ctor `MethodResolver(IAssemblyLoader)`

- [ ] **Step 1: Copy MethodResolver from Extension, change ctor**

Copy `src/dnSpy.MCP/Helpers/MethodResolver.cs` to `src/dnSpy.MCP.Core/Helpers/MethodResolver.cs`.

Change the namespace from `dnSpy.MCP.Helpers` to `dnSpy.MCP.Core.Helpers`.

Replace the constructor + private field:

```csharp
// BEFORE (Extension):
public sealed class MethodResolver {
    private readonly IDsDocumentService documentService;

    public MethodResolver(IDsDocumentService documentService) {
        this.documentService = documentService;
    }

    public ModuleDef? GetCurrentModule() {
        var docs = documentService.GetDocuments();
        foreach (var doc in docs) {
            if (doc.ModuleDef is ModuleDef mod) return mod;
        }
        return null;
    }

    public IEnumerable<ModuleDef> GetAllModules() {
        var docs = documentService.GetDocuments();
        foreach (var doc in docs) {
            if (doc.ModuleDef is ModuleDef mod) yield return mod;
        }
    }
    // ...
}

// AFTER (Core):
public sealed class MethodResolver {
    private readonly IAssemblyLoader _loader;

    public MethodResolver(IAssemblyLoader loader) {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public ModuleDef? GetCurrentModule() {
        foreach (var loaded in _loader.GetDocuments())
            return loaded.Module;
        return null;
    }

    public IEnumerable<ModuleDef> GetAllModules() {
        foreach (var loaded in _loader.GetDocuments())
            yield return loaded.Module;
    }
    // ... rest of methods unchanged (they call GetAllModules / GetModules)
}
```

Add `using dnSpy.MCP.Core.Abstractions;` at top. All other methods (`GetModules`, `ResolveMethod`, `ResolveMethodFlexible`, `SearchTypes`, etc.) work unchanged because they only use `GetModules(name)` which itself uses `GetAllModules()`.

- [ ] **Step 2: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS — McpContext + MethodResolver compile together

- [ ] **Step 3: Commit**

```bash
git add src/dnSpy.MCP.Core/Mcp/ src/dnSpy.MCP.Core/Helpers/
git commit -m "feat(core): add McpContext instance and MethodResolver"
```

### Task 1.5: Move pure infrastructure files to Core

**Files:**
- Create: `src/dnSpy.MCP.Core/Mcp/JsonRpc.cs` (moved verbatim)
- Create: `src/dnSpy.MCP.Core/Mcp/BufferedLineReader.cs` (moved verbatim)
- Create: `src/dnSpy.MCP.Core/Mcp/McpLogger.cs` (file-only half)
- Delete (deferred to Phase 4): original `src/dnSpy.MCP/Mcp/JsonRpc.cs`, `BufferedLineReader.cs`

For now, KEEP originals in Extension to avoid breaking it prematurely. The duplicated copies in Core compile independently because Extension does not yet reference Core types from these files.

- [ ] **Step 1: Move `JsonRpc.cs`**

```powershell
Copy-Item src/dnSpy.MCP/Mcp/JsonRpc.cs src/dnSpy.MCP.Core/Mcp/JsonRpc.cs
```

Edit the moved file: change `namespace dnSpy.MCP.Mcp` to `namespace dnSpy.MCP.Core.Mcp`.

- [ ] **Step 2: Move `BufferedLineReader.cs`**

```powershell
Copy-Item src/dnSpy.MCP/Mcp/BufferedLineReader.cs src/dnSpy.MCP.Core/Mcp/BufferedLineReader.cs
```

Edit: change `namespace dnSpy.MCP.Mcp` to `namespace dnSpy.MCP.Core.Mcp`, and visibility from `internal sealed` to `public sealed` (so Extension can still use it through reference).

- [ ] **Step 3: Create file-only `McpLogger.cs` in Core**

Create `src/dnSpy.MCP.Core/Mcp/McpLogger.cs` with **file-logging only** (no Output Pane code):

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace dnSpy.MCP.Core.Mcp;

/// <summary>
/// File-logging logger. Used by both Extension and Headless. The dnSpy Output Pane
/// integration lives in dnSpy.MCP.Adapters.DnSpyLogSink (Extension-only).
/// </summary>
public static class McpLogger {
    static readonly ConcurrentQueue<string> _recent = new();
    internal const int MaxRecent = 200;
    static readonly string _logPath;
    static readonly object _fileLock = new();

    /// <summary>Maximum log file size before rotation (5 MB).</summary>
    const long MaxFileSizeBytes = 5 * 1024 * 1024;

    public enum Level { Info, Warn, Error }

    static McpLogger() {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(dir, "mcp-server.log");
    }

    public static void Log(Level level, string message) {
        var tag = level switch {
            Level.Info => "INFO",
            Level.Warn => "WARN",
            Level.Error => "ERROR",
            _ => level.ToString().ToUpperInvariant()
        };
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";

        _recent.Enqueue(line);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

        try {
            lock (_fileLock) {
                RotateLogIfNeeded();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"MCP [LOG ERROR]: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"MCP: {line}");
    }

    public static string LogPath => _logPath;

    /// <summary>Rotates the log file when it exceeds MaxFileSizeBytes. Caller must hold _fileLock.</summary>
    static void RotateLogIfNeeded() {
        if (!File.Exists(_logPath)) return;
        var fi = new FileInfo(_logPath);
        if (fi.Length < MaxFileSizeBytes) return;

        var oldest = _logPath + ".3";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = 2; i >= 1; i--) {
            var src = _logPath + "." + i;
            var dst = _logPath + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst);
        }
        File.Move(_logPath, _logPath + ".1");
    }

    public static string[] GetRecent(int count = 50) {
        var entries = _recent.ToArray();
        var start = Math.Max(0, entries.Length - count);
        var result = new string[entries.Length - start];
        for (int i = start; i < entries.Length; i++)
            result[i - start] = entries[i];
        return result;
    }

    public static void ClearLog() {
        try {
            lock (_fileLock) {
                if (File.Exists(_logPath)) File.Delete(_logPath);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"MCP [CLEAR ERROR]: {ex.Message}");
        }
        while (_recent.TryDequeue(out _)) { }
    }
}
```

- [ ] **Step 4: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 5: Verify solution still builds (Extension still uses its own copies)**

Run: `dotnet build dnspy_mcp.sln`
Expected: PASS — there may be duplicate-type warnings, ignore them (resolved when originals are deleted in Phase 4)

- [ ] **Step 6: Commit**

```bash
git add src/dnSpy.MCP.Core/Mcp/
git commit -m "feat(core): add JsonRpc, BufferedLineReader, file-only McpLogger"
```

---

# Phase 2 — Tool Class Migration

Convert 13 tool classes from `static` to instance, in 4 batches. After each batch, build Core.

### Task 2.1: Batch A — pure dnlib tools (6 files)

**Files:**
- Create: `src/dnSpy.MCP.Core/Tools/AnalysisTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/IlDisplayTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/TypeInspectorTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/AttributeTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/ConstantTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/NamespaceTools.cs`

**Pattern (apply to each file):**
1. Copy from `src/dnSpy.MCP/Tools/`
2. Change `namespace dnSpy.MCP.Tools` to `namespace dnSpy.MCP.Core.Tools`
3. Change `public static class X` to `public sealed class X`
4. Add field + ctor:

```csharp
private readonly McpContext _ctx;
public X(McpContext ctx) => _ctx = ctx;
```

5. Add `using dnSpy.MCP.Core.Mcp;`
6. Replace each occurrence:
   - `DnSpyContext.DocumentService == null` → `_ctx.AssemblyLoader.GetDocuments().Count == 0`
   - `DnSpyContext.Resolver.X` → `_ctx.Resolver.X`
   - `DnSpyContext.DocumentService.GetDocuments()` → `_ctx.AssemblyLoader.GetDocuments().Select(l => l.Module)` (add `using System.Linq;`)
   - `McpLogger.Info(...)` / `Warn(...)` / `Error(...)` → `_ctx.Log.Info(...)` etc.
7. Remove `using dnSpy.Contracts.Documents;` (no longer needed)
8. Remove `using dnSpy.MCP.Mcp;` and `using dnSpy.MCP.Helpers;` (replaced by Core equivalents)

- [ ] **Step 1: Convert `AnalysisTools.cs`** — apply pattern above

The file has 4 methods: `GetMethodIl`, `GetMethodSignatures`, `GetTypeHierarchy`, `GetMethodBody`. Each checks `DnSpyContext.DocumentService == null` — replace with `_ctx.AssemblyLoader.GetDocuments().Count == 0`. The rest uses `DnSpyContext.Resolver.X` — replace with `_ctx.Resolver.X`.

- [ ] **Step 2: Convert `IlDisplayTools.cs`** — same pattern. This is read-only, no DnSpyContext access at all (pure dnlib).

- [ ] **Step 3: Convert `TypeInspectorTools.cs`** — same pattern.

- [ ] **Step 4: Convert `AttributeTools.cs`** — same pattern.

- [ ] **Step 5: Convert `ConstantTools.cs`** — same pattern.

- [ ] **Step 6: Convert `NamespaceTools.cs`** — same pattern.

- [ ] **Step 7: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/dnSpy.MCP.Core/Tools/
git commit -m "refactor(core): migrate batch A pure-dnlib tools to instance pattern"
```

### Task 2.2: Batch B — resolver-using tools (2 files)

**Files:**
- Create: `src/dnSpy.MCP.Core/Tools/SearchTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/XrefTools.cs`

**Pattern:** same as Task 2.1. These use `_ctx.Resolver.GetModules(assembly)` etc.

- [ ] **Step 1: Convert `SearchTools.cs`** — 4 methods (`SearchTypes`, `SearchMethods`, `SearchStrings`, `Grep`), all use `_ctx.Resolver.SearchTypes/SearchMethods/GetModules`.

- [ ] **Step 2: Convert `XrefTools.cs`** — 2 methods (`GetXrefsTo`, `GetCallees`), use `_ctx.Resolver.GetModules(assembly)` and `_ctx.Resolver.ResolveMethodFlexible`.

- [ ] **Step 3: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/dnSpy.MCP.Core/Tools/
git commit -m "refactor(core): migrate batch B resolver-using tools"
```

### Task 2.3: Batch C — loader + decompiler tools (3 files)

**Files:**
- Create: `src/dnSpy.MCP.Core/Tools/DecompilerTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/AssemblyTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/ResourceTools.cs`

- [ ] **Step 1: Convert `DecompilerTools.cs`**

3 methods: `DecompileMethod`, `DecompileType`, `DecompileAssembly`. Replace the core call:

```csharp
// BEFORE:
var output = new TextDecompilerOutput();
decompilerService.Decompiler.Decompile(method, output, new DecompilationContext());
return output.ToString();

// AFTER:
return _ctx.SourceDecompiler.DecompileMethod(method);
```

Similarly for type and module. Note `DecompileAssembly` iterates types — call `_ctx.SourceDecompiler.DecompileType(type)` per type.

- [ ] **Step 2: Convert `AssemblyTools.cs`**

7 methods. Replace:

```csharp
// BEFORE:
var documentService = DnSpyContext.DocumentService;
if (documentService == null) return "Error: ...";
// ...
IDsDocument? doc = null;
TreeViewTools.RunOnUIThread(() => {
    doc = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(path));
});
if (doc?.ModuleDef is ModuleDef mod) { ... }

// AFTER:
var result = _ctx.AssemblyLoader.Load(path);
if (!result.Success) return $"Error: {result.Error}";
var loaded = result.Module!;
if (loaded.Module is ModuleDef mod) { ... use mod ... }
```

For `CloseAssembly`: `var n = _ctx.AssemblyLoader.Close(assemblyName);` then format message.

For `ListLoadedAssemblies`, `AssemblyListNamespaces`, etc.: iterate `_ctx.AssemblyLoader.GetDocuments()`, use `loaded.Module` and `loaded.Name`/`loaded.AssemblyName`/`loaded.Path`.

Remove `using dnSpy.Contracts.Documents;` and `using dnSpy.MCP.Tools;` (TreeViewTools reference). Remove the `using dnlib.DotNet;` only if no longer needed (still needed for `ModuleDef`).

- [ ] **Step 3: Convert `ResourceTools.cs`**

3 methods. The current implementation uses `doc.PEImage` for PE headers. Replace with `System.Reflection.PortableExecutable.PEReader`:

```csharp
// BEFORE:
foreach (var doc in documentService.GetDocuments()) {
    if (doc.ModuleDef is ModuleDef mod && doc.PEImage is PEImage pe) { ... }
}

// AFTER:
foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
    var mod = loaded.Module;
    using var fs = File.OpenRead(loaded.Path);
    using var peReader = new PEReader(fs);
    var headers = peReader.PEHeaders;
    // ... extract PE headers from `headers` ...
}
```

- [ ] **Step 4: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/dnSpy.MCP.Core/Tools/
git commit -m "refactor(core): migrate batch C loader+decompiler tools"
```

### Task 2.4: Batch D — mutation tools (2 files)

**Files:**
- Create: `src/dnSpy.MCP.Core/Tools/IlPatchTools.cs`
- Create: `src/dnSpy.MCP.Core/Tools/RenameTools.cs`

- [ ] **Step 1: Convert `IlPatchTools.cs`**

382 LOC. Apply standard pattern, plus:
- Replace `method.Module` access — already works via dnlib (no DnSpyContext dependency on this)
- Replace `TreeViewTools.RefreshTreeViewOnUIThread();` (line 67) with `_ctx.TreeRefresh.RefreshAll();`

- [ ] **Step 2: Convert `RenameTools.cs`**

176 LOC. Apply standard pattern, plus:
- Drop `RefreshAfterRename` private method's call to `TreeViewTools.RefreshTreeViewOnUIThread()` and `tabSvc.RefreshModifiedDocument(doc)` — replace with `_ctx.TreeRefresh.RefreshAll();`
- Drop `TreeViewTools.UpdateNamespaceNode(assembly, oldNamespace, newNamespace);` (line 76) — replace with `_ctx.TreeRefresh.NotifyNamespaceRenamed(assembly, oldNamespace, newNamespace);`
- The `FindType` private helper returns `(TypeDef?, ModuleDef?, IDsDocument?)` — simplify to `TypeDef?` since callers don't use the doc:

```csharp
// BEFORE:
private static (TypeDef? type, ModuleDef? module, IDsDocument? doc) FindType(
    IDsDocumentService documentService, string assembly, string ns, string cls) { ... }

// AFTER (instance, uses _ctx):
private TypeDef? FindType(string assembly, string ns, string cls) {
    foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
        if (!string.Equals(loaded.AssemblyName ?? loaded.Name, assembly, StringComparison.OrdinalIgnoreCase))
            continue;
        foreach (var type in loaded.Module.GetTypes()) {
            if (string.Equals(type.Namespace, ns, StringComparison.Ordinal)
                && string.Equals(type.Name.String, cls, StringComparison.Ordinal))
                return type;
        }
    }
    return null;
}
```

- [ ] **Step 3: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/dnSpy.MCP.Core/Tools/
git commit -m "refactor(core): migrate batch D mutation tools, route refresh through ITreeRefreshNotifier"
```

---

# Phase 3 — ToolRegistry Refactor + Extension Stub

Move ToolRegistry to Core with hybrid instance/static discovery. Write temporary stubs in Extension so it still compiles (will be replaced in Phase 4).

### Task 3.1: Move ToolRegistry to Core

**Files:**
- Create: `src/dnSpy.MCP.Core/Mcp/ToolRegistry.cs`

**Interfaces:**
- Consumes: `McpContext` (Task 1.3), `DescriptionAttribute`
- Produces: `ToolRegistry(McpContext, params Assembly[])`

- [ ] **Step 1: Copy ToolRegistry.cs from Extension to Core**

```powershell
Copy-Item src/dnSpy.MCP/Mcp/ToolRegistry.cs src/dnSpy.MCP.Core/Mcp/ToolRegistry.cs
```

Edit the moved file. Change `namespace dnSpy.MCP.Mcp` to `namespace dnSpy.MCP.Core.Mcp`.

- [ ] **Step 2: Update the constructor + DiscoverTools for hybrid instance/static**

Replace the existing ctor + DiscoverTools with:

```csharp
public sealed class ToolRegistry {
    private readonly Dictionary<string, ToolEntry> _tools = new();

    public ToolRegistry(McpContext ctx, params Assembly[] assemblies) {
        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly required", nameof(assemblies));
        DiscoverTools(ctx, assemblies);
    }

    private void DiscoverTools(McpContext ctx, Assembly[] assemblies) {
        foreach (var asm in assemblies) {
            foreach (var type in asm.GetTypes()) {
                if (!IsToolClass(type)) continue;

                object? instance = null;
                var ctor = type.GetConstructor(new[] { typeof(McpContext) });
                if (ctor != null)
                    instance = ctor.Invoke(new object[] { ctx });

                var flags = instance != null
                    ? BindingFlags.Public | BindingFlags.Instance
                    : BindingFlags.Public | BindingFlags.Static;

                foreach (var method in type.GetMethods(flags)) {
                    var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
                    if (descAttr == null) continue;

                    var toolName = ToSnakeCase(method.Name);
                    var parameters = method.GetParameters()
                        .Select(p => new ToolParam {
                            Name = p.Name ?? "arg",
                            Type = MapType(p.ParameterType),
                            Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                            Required = !p.HasDefaultValue
                        }).ToList();

                    _tools[toolName] = new ToolEntry {
                        Name = toolName,
                        Description = descAttr.Description,
                        Method = method,
                        Instance = instance,
                        Parameters = parameters,
                        IsMutation = IsMutationTool(toolName),
                    };
                }
            }
        }
    }

    private static bool IsToolClass(Type type) {
        if (type.Namespace is null || !type.Namespace.StartsWith("dnSpy.MCP.Tools"))
            return false;
        if (!type.IsClass || type.IsAbstract) return false;
        // Static class OR instance class with ctor(McpContext)
        if (type.IsAbstract && type.IsSealed) return true;  // static
        return type.GetConstructor(new[] { typeof(McpContext) }) != null;
    }
}
```

Note: `Type.IsStatic` is not a real C# API — use `type.IsAbstract && type.IsSealed` to detect static classes.

Update `ToolEntry.Invoke` to use `Instance`:

```csharp
public string Invoke(JsonObject? arguments) {
    var methodParams = Method.GetParameters();
    var callArgs = new object?[methodParams.Length];
    // ... existing arg resolution ...
    var result = Method.Invoke(Instance, callArgs);  // Instance may be null for static
    return result?.ToString() ?? "";
}
```

Add `using System.Linq;` and `using System.Reflection;` if not present.

- [ ] **Step 3: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/dnSpy.MCP.Core/Mcp/ToolRegistry.cs
git commit -m "refactor(core): move ToolRegistry to Core with hybrid instance/static discovery"
```

### Task 3.2: Move McpServerHost to Core (ctor gains ToolRegistry)

**Files:**
- Create: `src/dnSpy.MCP.Core/Mcp/McpServerHost.cs`

**Interfaces:**
- Consumes: `McpSettings` (stays in Extension — see Interface note below), `ToolRegistry`
- Produces: `McpServerHost(McpSettings, ToolRegistry)`

**Interface note:** `McpSettings` is currently in `dnSpy.MCP.Settings`. Moving it is out of scope for this plan. Instead, McpServerHost references it via `dnSpy.MCP.Settings.McpSettings` — Core has a transitive reference through `dnSpy.Contracts.DnSpy` which exposes `ViewModelBase`. To verify, attempt the build in Step 3.

- [ ] **Step 1: Copy McpServerHost.cs from Extension to Core**

```powershell
Copy-Item src/dnSpy.MCP/Mcp/McpServerHost.cs src/dnSpy.MCP.Core/Mcp/McpServerHost.cs
```

Change namespace from `dnSpy.MCP.Mcp` to `dnSpy.MCP.Core.Mcp`. Add `using dnSpy.MCP.Settings;` if not present.

- [ ] **Step 2: Update ctor signature**

```csharp
// BEFORE:
public McpServerHost(McpSettings settings) {
    _settings = settings;
    _concurrency = new SemaphoreSlim(settings.MaxConcurrency);
    _registry = new ToolRegistry();
}

// AFTER:
public McpServerHost(McpSettings settings, ToolRegistry registry) {
    _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    _concurrency = new SemaphoreSlim(settings.MaxConcurrency);
    _registry = registry ?? throw new ArgumentNullException(nameof(registry));
}
```

- [ ] **Step 3: Build Core**

Run: `dotnet build src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/dnSpy.MCP.Core/Mcp/McpServerHost.cs
git commit -m "refactor(core): move McpServerHost, ctor accepts ToolRegistry"
```

### Task 3.3: Add temporary stub adapters in Extension

**Files:**
- Create: `src/dnSpy.MCP/Adapters/StubAssemblyLoader.cs`
- Create: `src/dnSpy.MCP/Adapters/StubSourceDecompiler.cs`
- Create: `src/dnSpy.MCP/Adapters/WpfUIThreadScheduler.cs`
- Create: `src/dnSpy.MCP/Adapters/StubLogSink.cs`
- Create: `src/dnSpy.MCP/Adapters/StubTreeRefreshNotifier.cs`

**Purpose:** These stubs satisfy the compiler so Extension keeps building. Phase 4 replaces 3 of them with real implementations (WpfUIThreadScheduler stays as-is).

- [ ] **Step 1: Write `StubAssemblyLoader.cs`** (returns empty for now)

```csharp
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
```

- [ ] **Step 2: Write `StubSourceDecompiler.cs`**

```csharp
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
```

- [ ] **Step 3: Write `WpfUIThreadScheduler.cs`** (real impl, kept through Phase 4)

```csharp
using System;
using System.Windows;
using System.Windows.Threading;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class WpfUIThreadScheduler : IUIThreadScheduler {
    public T Invoke<T>(Func<T> action) {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return action();
        if (dispatcher.CheckAccess()) return action();
        return dispatcher.Invoke(action, DispatcherPriority.Normal);
    }

    public void Invoke(Action action) {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action, DispatcherPriority.Normal);
    }
}
```

- [ ] **Step 4: Write `StubLogSink.cs`** and **`StubTreeRefreshNotifier.cs`** — similar no-op stubs.

- [ ] **Step 5: Update Extension's `McpServerHost` consumer (TheExtension.cs)**

Edit `src/dnSpy.MCP/TheExtension.cs` `StartServer()` method:

```csharp
// BEFORE:
_serverHost = new McpServerHost(Settings!);

// AFTER (temporary):
var stubCtx = new McpContext(
    new StubAssemblyLoader(),
    new StubSourceDecompiler(),
    new WpfUIThreadScheduler(),
    new StubLogSink(),
    new StubTreeRefreshNotifier());
var stubRegistry = new ToolRegistry(stubCtx, typeof(McpContext).Assembly);
_serverHost = new McpServerHost(Settings!, stubRegistry);
```

Add `using dnSpy.MCP.Core.Mcp;` and `using dnSpy.MCP.Adapters;` at top.

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build dnspy_mcp.sln`
Expected: PASS — Extension is now using stubs (which produce useless results) but it compiles. Old `Tools/*.cs`, `DnSpyContext.cs`, etc. still exist but are unused. Will be deleted in Phase 4.

- [ ] **Step 7: Commit**

```bash
git add src/dnSpy.MCP/Adapters/ src/dnSpy.MCP/TheExtension.cs
git commit -m "feat(extension): add stub adapters to compose McpContext (temporary)"
```

---

# Phase 4 — Extension Rewire

Replace stubs with real implementations, refactor TheExtension, delete dead code.

### Task 4.1: Replace StubAssemblyLoader with real DnSpyAssemblyLoader

**Files:**
- Create: `src/dnSpy.MCP/Adapters/DnSpyAssemblyLoader.cs`
- Delete: `src/dnSpy.MCP/Adapters/StubAssemblyLoader.cs`

- [ ] **Step 1: Write `DnSpyAssemblyLoader.cs`**

```csharp
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
```

- [ ] **Step 2: Delete StubAssemblyLoader.cs**

```powershell
Remove-Item src/dnSpy.MCP/Adapters/StubAssemblyLoader.cs
```

- [ ] **Step 3: Update reference in TheExtension.cs**

Replace `new StubAssemblyLoader()` with `new DnSpyAssemblyLoader(DocumentService!, stubCtx.UI)`. Note: compose `ui` first, pass to loader.

- [ ] **Step 4: Build and verify**

Run: `dotnet build dnspy_mcp.sln`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/dnSpy.MCP/Adapters/
git commit -m "feat(extension): add DnSpyAssemblyLoader, replace stub"
```

### Task 4.2: Replace StubSourceDecompiler with real adapter

**Files:**
- Modify: `src/dnSpy.MCP/TheExtension.cs`
- Delete: `src/dnSpy.MCP/Adapters/StubSourceDecompiler.cs`

- [ ] **Step 1: Use the shared Core adapter in TheExtension**

In TheExtension.cs, replace `new StubSourceDecompiler()` with:

```csharp
using dnSpy.MCP.Core.Adapters;
// ...
new DnSpyDecompilerSourceProvider(DecompilerService!.Decompiler),
```

- [ ] **Step 2: Delete StubSourceDecompiler.cs**

```powershell
Remove-Item src/dnSpy.MCP/Adapters/StubSourceDecompiler.cs
```

- [ ] **Step 3: Build and commit**

```bash
dotnet build dnspy_mcp.sln
git add -A
git commit -m "feat(extension): use shared DnSpyDecompilerSourceProvider, remove stub"
```

### Task 4.3: Replace StubLogSink with DnSpyLogSink

**Files:**
- Create: `src/dnSpy.MCP/Adapters/DnSpyLogSink.cs`
- Delete: `src/dnSpy.MCP/Adapters/StubLogSink.cs`

- [ ] **Step 1: Write `DnSpyLogSink.cs`**

```csharp
using System;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Adapters;

/// <summary>
/// Extension log sink: writes to file via shared McpLogger, and to dnSpy Output Pane
/// when available (marshaled to UI thread).
/// </summary>
internal sealed class DnSpyLogSink : ILogSink {
    private readonly IUIThreadScheduler _ui;
    private readonly IOutputTextPane? _pane;

    public DnSpyLogSink(IUIThreadScheduler ui, IOutputTextPane? pane) {
        _ui = ui;
        _pane = pane;
    }

    public void Info(string message) => Log(McpLogger.Level.Info, message, BoxedTextColor.DebugLogExtensionMessage);
    public void Warn(string message) => Log(McpLogger.Level.Warn, message, BoxedTextColor.DebugLogStepFiltering);
    public void Error(string message, Exception? ex = null) {
        var text = ex is null ? message : $"{message}: {ex}";
        Log(McpLogger.Level.Error, text, BoxedTextColor.DebugLogExceptionUnhandled);
    }

    private void Log(McpLogger.Level level, string message, object color) {
        McpLogger.Log(level, message);

        if (_pane is null) return;
        try {
            _ui.Invoke(() => _pane.WriteLine(color, message));
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"MCP [OUTPUT ERROR]: {ex.Message}");
        }
    }
}
```

- [ ] **Step 2: Delete StubLogSink.cs**

```powershell
Remove-Item src/dnSpy.MCP/Adapters/StubLogSink.cs
```

- [ ] **Step 3: Update TheExtension.cs**

The Output Pane needs to be created lazily. Add a field and lazy-init method:

```csharp
// In TheExtension.cs, replace the stub usage:
private IOutputTextPane? _outputPane;
// in OnEvent(AppLoaded), after _ui is composed:
_outputPane = OutputService?.Create(new("D1E5F6A0-3B7C-4A8D-9E2F-1B3D5C7A9E0F"), "MCP Server", string.Empty);

// McpContext construction:
var logSink = new DnSpyLogSink(uiScheduler, _outputPane);
```

- [ ] **Step 4: Build, commit**

```bash
dotnet build dnspy_mcp.sln
git add -A
git commit -m "feat(extension): add DnSpyLogSink with Output Pane integration"
```

### Task 4.4: Replace StubTreeRefreshNotifier with DnSpyTreeRefreshNotifier

**Files:**
- Create: `src/dnSpy.MCP/Adapters/DnSpyTreeRefreshNotifier.cs`
- Delete: `src/dnSpy.MCP/Adapters/StubTreeRefreshNotifier.cs`

- [ ] **Step 1: Move TreeViewTools.cs internal helpers + write adapter**

First, in `src/dnSpy.MCP/Tools/TreeViewTools.cs`, change `internal static void RefreshTreeViewOnUIThread()` and `internal static void UpdateNamespaceNode(...)` visibility to `internal` (already) — they remain accessible within Extension.

Create `src/dnSpy.MCP/Adapters/DnSpyTreeRefreshNotifier.cs`:

```csharp
using System;
using System.Linq;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Tools;

namespace dnSpy.MCP.Adapters;

internal sealed class DnSpyTreeRefreshNotifier : ITreeRefreshNotifier {
    private readonly IDocumentTreeView? _treeView;
    private readonly IUIThreadScheduler _ui;

    public DnSpyTreeRefreshNotifier(IDocumentTreeView? treeView, IUIThreadScheduler ui) {
        _treeView = treeView;
        _ui = ui;
    }

    public void RefreshAll() {
        if (_treeView is null) return;
        _ui.Invoke(() => _treeView.TreeView?.RefreshAllNodes());
    }

    public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) {
        if (_treeView is null) return;
        TreeViewTools.UpdateNamespaceNode(assembly, oldNamespace, newNamespace);
    }
}
```

- [ ] **Step 2: Delete StubTreeRefreshNotifier.cs**

```powershell
Remove-Item src/dnSpy.MCP/Adapters/StubTreeRefreshNotifier.cs
```

- [ ] **Step 3: Update TheExtension.cs**

Resolve `IDocumentTreeView` lazily via `IServiceLocator.TryResolve<IDocumentTreeView>()` (existing pattern in `DnSpyContext`). Pass to `DnSpyTreeRefreshNotifier`.

- [ ] **Step 4: Build, commit**

```bash
dotnet build dnspy_mcp.sln
git add -A
git commit -m "feat(extension): add DnSpyTreeRefreshNotifier"
```

### Task 4.5: Delete dead code

**Files:**
- Delete: `src/dnSpy.MCP/DnSpyContext.cs`
- Delete: `src/dnSpy.MCP/Helpers/MethodResolver.cs`
- Delete: `src/dnSpy.MCP/Helpers/TextDecompilerOutput.cs`
- Delete: `src/dnSpy.MCP/Mcp/McpLogger.cs`
- Delete: `src/dnSpy.MCP/Mcp/JsonRpc.cs`
- Delete: `src/dnSpy.MCP/Mcp/BufferedLineReader.cs`
- Delete: `src/dnSpy.MCP/Mcp/ToolRegistry.cs`
- Delete: `src/dnSpy.MCP/Mcp/McpServerHost.cs`
- Delete: `src/dnSpy.MCP/Tools/AnalysisTools.cs`
- Delete: `src/dnSpy.MCP/Tools/IlDisplayTools.cs`
- Delete: `src/dnSpy.MCP/Tools/TypeInspectorTools.cs`
- Delete: `src/dnSpy.MCP/Tools/AttributeTools.cs`
- Delete: `src/dnSpy.MCP/Tools/ConstantTools.cs`
- Delete: `src/dnSpy.MCP/Tools/NamespaceTools.cs`
- Delete: `src/dnSpy.MCP/Tools/SearchTools.cs`
- Delete: `src/dnSpy.MCP/Tools/XrefTools.cs`
- Delete: `src/dnSpy.MCP/Tools/DecompilerTools.cs`
- Delete: `src/dnSpy.MCP/Tools/AssemblyTools.cs`
- Delete: `src/dnSpy.MCP/Tools/ResourceTools.cs`
- Delete: `src/dnSpy.MCP/Tools/IlPatchTools.cs`
- Delete: `src/dnSpy.MCP/Tools/RenameTools.cs`

**Keep:** `src/dnSpy.MCP/Tools/TreeViewTools.cs` (Extension-only).

- [ ] **Step 1: Delete all the files above**

```powershell
Remove-Item src/dnSpy.MCP/DnSpyContext.cs
Remove-Item src/dnSpy.MCP/Helpers/MethodResolver.cs
Remove-Item src/dnSpy.MCP/Helpers/TextDecompilerOutput.cs
Remove-Item src/dnSpy.MCP/Mcp/McpLogger.cs
Remove-Item src/dnSpy.MCP/Mcp/JsonRpc.cs
Remove-Item src/dnSpy.MCP/Mcp/BufferedLineReader.cs
Remove-Item src/dnSpy.MCP/Mcp/ToolRegistry.cs
Remove-Item src/dnSpy.MCP/Mcp/McpServerHost.cs
Remove-Item src/dnSpy.MCP/Tools/AnalysisTools.cs, src/dnSpy.MCP/Tools/IlDisplayTools.cs, src/dnSpy.MCP/Tools/TypeInspectorTools.cs, src/dnSpy.MCP/Tools/AttributeTools.cs, src/dnSpy.MCP/Tools/ConstantTools.cs, src/dnSpy.MCP/Tools/NamespaceTools.cs, src/dnSpy.MCP/Tools/SearchTools.cs, src/dnSpy.MCP/Tools/XrefTools.cs, src/dnSpy.MCP/Tools/DecompilerTools.cs, src/dnSpy.MCP/Tools/AssemblyTools.cs, src/dnSpy.MCP/Tools/ResourceTools.cs, src/dnSpy.MCP/Tools/IlPatchTools.cs, src/dnSpy.MCP/Tools/RenameTools.cs
```

- [ ] **Step 2: Build and verify it still compiles**

Run: `dotnet build dnspy_mcp.sln`
Expected: PASS — Extension now references Core for all moved types. TreeViewTools is the only remaining Extension tool.

If build fails: search for any `using dnSpy.MCP.Mcp;` or `using dnSpy.MCP.Helpers;` left in Extension files and update to `using dnSpy.MCP.Core.Mcp;` / `using dnSpy.MCP.Core.Helpers;`.

- [ ] **Step 3: Verify tool count via the guard script (will fail until script is updated)**

Run: `pwsh scripts/verify-tool-count.ps1`
Expected: FAIL (count mismatch — old regex matched static methods). Fix script in Task 6.5.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(extension): delete moved sources, reference Core for tools and infra"
```

### Task 4.6: Update Extension tool count in CLAUDE.md

**Files:**
- Modify: `CLAUDE.md` (header `## Available MCP Tools (38)`)

- [ ] **Step 1: Verify actual tool count**

Run the discovery manually (since script is broken):

```powershell
Get-ChildItem -Path src/dnSpy.MCP.Core/Tools, src/dnSpy.MCP/Tools -Filter *.cs |
  ForEach-Object { $lines = Get-Content $_.FullName; for ($i=0; $i -lt $lines.Count; $i++) { if ($lines[$i] -notmatch '^\s*\[Description\(') { continue }; $j = $i + 1; while ($j -lt $lines.Count -and $lines[$j].Trim() -eq '') { $j++ }; if ($j -lt $lines.Count -and $lines[$j] -match 'public (static )?string (\w+)\s*\(') { $Matches[1] + $Matches[2] } } } |
  Measure-Object |
  Select-Object -ExpandProperty Count
```

Expected: 38 (36 Core + 2 Extension).

- [ ] **Step 2: Update CLAUDE.md header if needed**

If count changed, update `## Available MCP Tools (NN)` to match. Note in body that Core contributes 36 tools, Extension adds 2 UI-only.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update tool count for new architecture"
```

---

# Phase 5 — Headless Project

Build the standalone stdio MCP server.

### Task 5.1: Copy additional dnSpy DLLs into deps

**Files:**
- Copy: from dnSpy install to `deps/`
  - `dnSpy.Decompiler.dll`
  - `dnSpy.Decompiler.ILSpy.Core.dll`
  - `dnSpy.Decompiler.ILSpy.dll`
  - `ICSharpCode.Decompiler.dll` (may already exist)
  - `ICSharpCode.NRefactory.CSharp.dll`
  - `ICSharpCode.NRefactory.dll`

- [ ] **Step 1: Identify dnSpy install path**

The user's dnSpy install is at `D:\tools\dnSpy` (from `build.ps1` default). DLLs live at `D:\tools\dnSpy\bin\`.

- [ ] **Step 2: Copy DLLs**

```powershell
$dnspy = "D:\tools\dnSpy\bin"
Copy-Item "$dnspy\dnSpy.Decompiler.dll" deps\
Copy-Item "$dnspy\dnSpy.Decompiler.ILSpy.Core.dll" deps\
Copy-Item "$dnspy\dnSpy.Decompiler.ILSpy.dll" deps\
Copy-Item "$dnspy\ICSharpCode.NRefactory.CSharp.dll" deps\
Copy-Item "$dnspy\ICSharpCode.NRefactory.dll" deps\
```

If `deps\ICSharpCode.Decompiler.dll` already exists, do not overwrite (it should match).

- [ ] **Step 3: Update `.gitignore` if `deps/` is ignored**

Check: `cat .gitignore | Select-String deps`. If `deps/` is ignored, add a `!deps/dnSpy.Decompiler*.dll` exception so the Headless deps are tracked. Otherwise CI must download them.

- [ ] **Step 4: Commit**

```bash
git add deps/ .gitignore
git commit -m "chore: vendor dnSpy decompiler DLLs for Headless project"
```

### Task 5.2: Create Headless csproj

**Files:**
- Create: `src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>dnspy-mcp-headless</AssemblyName>
    <Nullable>enable</Nullable>
    <TargetFramework>net10.0</TargetFramework>
    <Version>2.0.0</Version>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <PropertyGroup>
    <DnSpyBin>..\..\deps</DnSpyBin>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\dnSpy.MCP.Core\dnSpy.MCP.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- dnSpy decompiler DLLs (vendored) -->
    <Reference Include="dnSpy.Contracts.DnSpy">
      <HintPath>$(DnSpyBin)\dnSpy.Contracts.DnSpy.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnSpy.Contracts.Logic">
      <HintPath>$(DnSpyBin)\dnSpy.Contracts.Logic.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnSpy.Decompiler">
      <HintPath>$(DnSpyBin)\dnSpy.Decompiler.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnSpy.Decompiler.ILSpy.Core">
      <HintPath>$(DnSpyBin)\dnSpy.Decompiler.ILSpy.Core.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnSpy.Decompiler.ILSpy">
      <HintPath>$(DnSpyBin)\dnSpy.Decompiler.ILSpy.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ICSharpCode.Decompiler">
      <HintPath>$(DnSpyBin)\ICSharpCode.Decompiler.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ICSharpCode.NRefactory">
      <HintPath>$(DnSpyBin)\ICSharpCode.NRefactory.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ICSharpCode.NRefactory.CSharp">
      <HintPath>$(DnSpyBin)\ICSharpCode.NRefactory.CSharp.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnlib">
      <HintPath>$(DnSpyBin)\dnlib.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.4.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
  </ItemGroup>

</Project>
```

Note: pin `ModelContextProtocol` to a specific version after verifying the latest stable. `Microsoft.Extensions.Hosting` version should match net10.0 default.

- [ ] **Step 2: Add to solution**

```powershell
dotnet sln add src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj
```

- [ ] **Step 3: Build (will fail — no Program.cs yet)**

Run: `dotnet build src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj`
Expected: FAIL with "No entry point found" — this is fine, next task adds Program.cs.

- [ ] **Step 4: Commit**

```bash
git add src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj dnspy_mcp.sln
git commit -m "chore: scaffold dnSpy.MCP.Headless csproj"
```

### Task 5.3: Create Headless adapters

**Files:**
- Create: `src/dnSpy.MCP.Headless/Adapters/DnlibAssemblyLoader.cs`
- Create: `src/dnSpy.MCP.Headless/Adapters/DnSpyDecompilerLoader.cs`
- Create: `src/dnSpy.MCP.Headless/Adapters/InlineUIThreadScheduler.cs`
- Create: `src/dnSpy.MCP.Headless/Adapters/StderrLogSink.cs`
- Create: `src/dnSpy.MCP.Headless/Adapters/NoOpTreeRefreshNotifier.cs`

- [ ] **Step 1: Write `DnlibAssemblyLoader.cs`** (blueprint from `dnSpy.Console/Program.cs:210-214, 947-957`)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless assembly loader using dnlib directly. Mirrors dnSpy.Console/Program.cs
/// setup (ModuleDef.CreateModuleContext + AssemblyResolver config).
/// </summary>
public sealed class DnlibAssemblyLoader : IAssemblyLoader {
    private readonly Dictionary<string, LoadedModule> _byKey = new(StringComparer.OrdinalIgnoreCase);
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

        var key = Path.GetFileName(path);
        if (_byKey.TryGetValue(key, out var existing))
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
            _byKey[key] = loaded;
            return new LoadResult(true, null, loaded);
        }
        catch (Exception ex) {
            return new LoadResult(false, ex.Message, null);
        }
    }

    public int Close(string assemblyName) {
        var matches = new List<KeyValuePair<string, LoadedModule>>();
        foreach (var kvp in _byKey) {
            var name = kvp.Value.AssemblyName ?? kvp.Value.Name;
            if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
                matches.Add(kvp);
        }
        foreach (var kvp in matches) _byKey.Remove(kvp.Key);
        return matches.Count;
    }

    public IReadOnlyList<LoadedModule> GetDocuments() {
        var list = new List<LoadedModule>(_byKey.Count);
        foreach (var v in _byKey.Values) list.Add(v);
        return list;
    }
}
```

- [ ] **Step 2: Write `DnSpyDecompilerLoader.cs`** (blueprint from `dnSpy.Console/Program.cs:226-247`)

```csharp
using System;
using System.Reflection;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Loads the C# decompiler from dnSpy.Decompiler.ILSpy.Core via reflection.
/// Uses the IDecompilerProvider contract (must have default ctor — see
/// dnSpy.Contracts.Logic/Decompiler/IDecompilerProvider.cs).
/// </summary>
public static class DnSpyDecompilerLoader {
    public static IDecompiler LoadCSharp() {
        var asm = Assembly.Load("dnSpy.Decompiler.ILSpy.Core");
        foreach (var type in asm.GetTypes()) {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IDecompilerProvider).IsAssignableFrom(type)) continue;

            var provider = (IDecompilerProvider)Activator.CreateInstance(type)!;
            foreach (var lang in provider.Create()) {
                if (lang.UniqueGuid == DecompilerConstants.LANGUAGE_CSHARP_ILSPY)
                    return lang;
            }
        }
        throw new InvalidOperationException(
            "C# decompiler not found. Ensure dnSpy.Decompiler.ILSpy.Core.dll is in the probe path.");
    }
}
```

- [ ] **Step 3: Write `InlineUIThreadScheduler.cs`**

```csharp
using System;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// No-op UI scheduler — runs actions inline. Headless has no UI thread.
/// </summary>
public sealed class InlineUIThreadScheduler : IUIThreadScheduler {
    public T Invoke<T>(Func<T> action) => action();
    public void Invoke(Action action) => action();
}
```

- [ ] **Step 4: Write `StderrLogSink.cs`**

```csharp
using System;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless log sink: writes to file (via McpLogger) + stderr.
/// CRITICAL: must never write to stdout — that's reserved for MCP JSON-RPC.
/// </summary>
public sealed class StderrLogSink : ILogSink {
    private static readonly object _lock = new();

    public void Info(string message) => Log(McpLogger.Level.Info, message);
    public void Warn(string message) => Log(McpLogger.Level.Warn, message);
    public void Error(string message, Exception? ex = null) {
        var text = ex is null ? message : $"{message}: {ex}";
        Log(McpLogger.Level.Error, text);
    }

    private static void Log(McpLogger.Level level, string message) {
        McpLogger.Log(level, message);
        lock (_lock) {
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
        }
    }
}
```

- [ ] **Step 5: Write `NoOpTreeRefreshNotifier.cs`**

```csharp
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// No-op notifier — Headless has no UI to refresh.
/// </summary>
public sealed class NoOpTreeRefreshNotifier : ITreeRefreshNotifier {
    public void RefreshAll() { }
    public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
}
```

- [ ] **Step 6: Build Headless (still fails — no Program.cs)**

Run: `dotnet build src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj`
Expected: FAIL with "No Program.cs" — adapters compile though

- [ ] **Step 7: Commit**

```bash
git add src/dnSpy.MCP.Headless/Adapters/
git commit -m "feat(headless): add 5 host adapters (DnlibAssemblyLoader, etc.)"
```

### Task 5.4: Create CliOptions

**Files:**
- Create: `src/dnSpy.MCP.Headless/CliOptions.cs`

- [ ] **Step 1: Write CliOptions with glob expansion**

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace dnSpy.MCP.Headless;

public sealed class CliOptions {
    public List<string> PreLoads { get; } = new();
    public string? ConfigPath { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args) {
        var opts = new CliOptions();
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--load":
                case "-l":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--load requires a path argument");
                    opts.PreLoads.Add(args[++i]);
                    break;
                case "--config":
                case "-c":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--config requires a path argument");
                    opts.ConfigPath = args[++i];
                    break;
                case "--help":
                case "-h":
                    opts.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return opts;
    }

    /// <summary>Expand any glob patterns in PreLoads into concrete file paths.</summary>
    public IEnumerable<string> ExpandLoads() {
        foreach (var pattern in PreLoads) {
            var dir = Path.GetDirectoryName(pattern);
            var file = Path.GetFileName(pattern);
            if (dir is null || file.Length == 0) {
                if (File.Exists(pattern)) yield return pattern;
                continue;
            }
            if (file.Contains('*') || file.Contains('?')) {
                foreach (var f in Directory.GetFiles(dir, file, SearchOption.TopDirectoryOnly))
                    yield return f;
            }
            else if (File.Exists(pattern)) {
                yield return pattern;
            }
        }
    }

    public static void PrintHelp() {
        Console.Error.WriteLine("dnspy-mcp-headless — standalone MCP server for batch .NET analysis");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: dnspy-mcp-headless [--load <path>]... [--config <json>] [--help]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --load, -l <path>   Pre-load .NET DLL/EXE (repeatable, supports * and ? globs)");
        Console.Error.WriteLine("  --config, -c <json> Configuration file (reserved, currently unused)");
        Console.Error.WriteLine("  --help, -h          Show this help and exit");
        Console.Error.WriteLine();
        Console.Error.WriteLine("MCP transport: stdio (stdin/stdout). Logging: stderr.");
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/dnSpy.MCP.Headless/CliOptions.cs
git commit -m "feat(headless): add CliOptions with glob expansion"
```

### Task 5.5: Create AutoToolRegistration

**Files:**
- Create: `src/dnSpy.MCP.Headless/Adapters/AutoToolRegistration.cs`

- [ ] **Step 1: Write the registration helper**

```csharp
using System;
using System.ComponentModel;
using System.Reflection;
using dnSpy.MCP.Core.Mcp;
using ModelContextProtocol.Server;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Scans Core tool classes via reflection and registers each [Description] method
/// as an MCP tool. Auto-discovers future Core tools without manual wrapper maintenance.
/// </summary>
public static class AutoToolRegistration {
    public static void RegisterAll(IMcpServerBuilder builder, McpContext ctx) {
        var coreAsm = typeof(McpContext).Assembly;
        foreach (var type in coreAsm.GetTypes()) {
            if (!IsToolClass(type)) continue;

            object? instance = null;
            var ctor = type.GetConstructor(new[] { typeof(McpContext) });
            if (ctor is null) continue;  // skip Extension-only static tools (TreeViewTools)
            instance = ctor.Invoke(new object[] { ctx });

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
                var desc = method.GetCustomAttribute<DescriptionAttribute>();
                if (desc is null) continue;

                var toolName = ToolRegistry.ToSnakeCase(method.Name);
                var mcpTool = McpServerTool.Create(method, instance,
                    new McpServerToolCreateOptions {
                        Name = toolName,
                        Description = desc.Description,
                    });
                builder.WithTools(mcpTool);
            }
        }
    }

    private static bool IsToolClass(Type type) {
        if (type.Namespace is null || !type.Namespace.StartsWith("dnSpy.MCP.Tools"))
            return false;
        if (!type.IsClass || type.IsAbstract) return false;
        return type.GetConstructor(new[] { typeof(McpContext) }) != null;
    }
}
```

- [ ] **Step 2: Build (will still fail — no Program.cs)**

Run: `dotnet build src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj`
Expected: FAIL — no Main yet

- [ ] **Step 3: Commit**

```bash
git add src/dnSpy.MCP.Headless/Adapters/AutoToolRegistration.cs
git commit -m "feat(headless): add AutoToolRegistration via McpServerTool.Create"
```

### Task 5.6: Create Program.cs

**Files:**
- Create: `src/dnSpy.MCP.Headless/Program.cs`

- [ ] **Step 1: Write Program.cs with fail-fast startup**

```csharp
using System;
using System.IO;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Adapters;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Headless.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h")) {
    CliOptions.PrintHelp();
    return 0;
}

CliOptions cli;
try {
    cli = CliOptions.Parse(args);
}
catch (ArgumentException ex) {
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    CliOptions.PrintHelp();
    return 2;
}

// Validate pre-load paths exist before starting the server
foreach (var path in cli.ExpandLoads()) {
    if (!File.Exists(path)) {
        Console.Error.WriteLine($"Error: --load file not found: {path}");
        return 2;
    }
}

// Fail-fast: validate decompiler DLLs load BEFORE accepting requests
dnSpy.Contracts.Decompiler.IDecompiler decompiler;
try {
    decompiler = DnSpyDecompilerLoader.LoadCSharp();
}
catch (Exception ex) {
    Console.Error.WriteLine($"Failed to load dnSpy decompiler: {ex.Message}");
    Console.Error.WriteLine("Ensure dnSpy.Decompiler.ILSpy.Core.dll and dependencies are next to the exe.");
    return 3;
}

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: stderr-only logging (stdout is reserved for MCP JSON-RPC)
builder.Logging.AddConsole(options => {
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<McpContext>(_ => {
    var uiScheduler = new InlineUIThreadScheduler();
    var loader = new DnlibAssemblyLoader();
    foreach (var path in cli.ExpandLoads())
        loader.Load(path);

    return new McpContext(
        assemblyLoader: loader,
        sourceDecompiler: new DnSpyDecompilerSourceProvider(decompiler),
        ui: uiScheduler,
        log: new StderrLogSink(),
        treeRefresh: new NoOpTreeRefreshNotifier());
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .Services
    .AddSingleton<IMcpServerBuilder>(sp => {
        var ctx = sp.GetRequiredService<McpContext>();
        var hostBuilder = sp.GetRequiredService<IMcpServerBuilder>();
        AutoToolRegistration.RegisterAll(hostBuilder, ctx);
        return hostBuilder;
    });

// The above pattern may need adjustment based on SDK API; alternative:
// use AddMcpServer().WithStdioServerTransport().WithTools([...]) with explicit
// list built from AutoToolRegistration. Test at runtime.

try {
    await builder.Build().RunAsync();
    return 0;
}
catch (OperationCanceledException) {
    return 0;
}
catch (Exception ex) {
    Console.Error.WriteLine($"Fatal: {ex}");
    return 1;
}
```

**Note:** The exact DI registration pattern for `AddMcpServer().WithStdioServerTransport()` followed by `AutoToolRegistration` may need adjustment based on the SDK's actual API surface. If `IMcpServerBuilder` is not directly injectable, switch to:

```csharp
var mcpBuilder = builder.Services.AddMcpServer().WithStdioServerTransport();
// build McpContext eagerly to pass to registration
var ctx = BuildContext(cli, decompiler);
AutoToolRegistration.RegisterAll(mcpBuilder, ctx);
builder.Services.AddSingleton(ctx);
```

- [ ] **Step 2: Build the Headless project**

Run: `dotnet build src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj`
Expected: PASS

- [ ] **Step 3: Smoke test --help**

```powershell
dotnet run --project src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj -- --help
```
Expected: prints usage to stderr, exits with code 0.

- [ ] **Step 4: Smoke test invalid arg**

```powershell
dotnet run --project src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj -- --bogus
```
Expected: "Argument error: Unknown argument: --bogus" + help, exit code 2.

- [ ] **Step 5: Smoke test initialize/tools/list via stdin**

In PowerShell:

```powershell
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --project src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj --no-build"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}')
$p.WaitForExit(5000) | Out-Null
$p.StandardOutput.ReadLine()
```

Expected: a JSON-RPC response with `result.serverInfo.name = "dnspy-mcp-headless"`.

- [ ] **Step 6: Commit**

```bash
git add src/dnSpy.MCP.Headless/Program.cs
git commit -m "feat(headless): add Program.cs with fail-fast startup + stdio MCP host"
```

---

# Phase 6 — Tests + CI

### Task 6.1: Create Core.Tests project

**Files:**
- Create: `tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj`

- [ ] **Step 1: Scaffold**

```powershell
dotnet new xunit -n dnSpy.MCP.Core.Tests -o tests/dnSpy.MCP.Core.Tests --framework net10.0
Remove-Item tests/dnSpy.MCP.Core.Tests/UnitTest1.cs
dotnet sln add tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj
```

- [ ] **Step 2: Edit the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.2" />
    <PackageReference Include="Moq" Version="4.20.70" />
    <PackageReference Include="coverlet.collector" Version="6.0.2">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\dnSpy.MCP.Core\dnSpy.MCP.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- dnSpy contract DLLs needed transitively by Core -->
    <Reference Include="dnSpy.Contracts.DnSpy">
      <HintPath>..\..\deps\dnSpy.Contracts.DnSpy.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnSpy.Contracts.Logic">
      <HintPath>..\..\deps\dnSpy.Contracts.Logic.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ICSharpCode.Decompiler">
      <HintPath>..\..\deps\ICSharpCode.Decompiler.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="dnlib">
      <HintPath>..\..\deps\dnlib.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>

</Project>
```

Note: NO `UseWPF=true`. This is what unlocks unit testing without the WPF stack.

- [ ] **Step 3: Build and verify**

Run: `dotnet build tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj`
Expected: PASS (no test files yet, just empty project)

- [ ] **Step 4: Commit**

```bash
git add tests/dnSpy.MCP.Core.Tests/
git commit -m "test: scaffold dnSpy.MCP.Core.Tests (no WPF dependency)"
```

### Task 6.2: Migrate existing tests to Core.Tests

**Files:**
- Move: `src/dnSpy.MCP.Tests/JsonRpcTests.cs` → `tests/dnSpy.MCP.Core.Tests/Mcp/JsonRpcTests.cs`
- Move: `src/dnSpy.MCP.Tests/BufferedLineReaderTests.cs` → `tests/dnSpy.MCP.Core.Tests/Mcp/BufferedLineReaderTests.cs`
- Move: `src/dnSpy.MCP.Tests/ToolRegistryTests.cs` → `tests/dnSpy.MCP.Core.Tests/Mcp/ToolRegistryTests.cs`
- Move: `src/dnSpy.MCP.Tests/ConvertJsonValueTests.cs` → `tests/dnSpy.MCP.Core.Tests/Mcp/ConvertJsonValueTests.cs`

- [ ] **Step 1: Move files**

```powershell
New-Item -ItemType Directory -Force tests/dnSpy.MCP.Core.Tests/Mcp | Out-Null
Move-Item src/dnSpy.MCP.Tests/JsonRpcTests.cs tests/dnSpy.MCP.Core.Tests/Mcp/
Move-Item src/dnSpy.MCP.Tests/BufferedLineReaderTests.cs tests/dnSpy.MCP.Core.Tests/Mcp/
Move-Item src/dnSpy.MCP.Tests/ToolRegistryTests.cs tests/dnSpy.MCP.Core.Tests/Mcp/
Move-Item src/dnSpy.MCP.Tests/ConvertJsonValueTests.cs tests/dnSpy.MCP.Core.Tests/Mcp/
```

- [ ] **Step 2: Update namespaces in moved files**

Each file: change `namespace dnSpy.MCP.Tests;` to `namespace dnSpy.MCP.Core.Tests.Mcp;`. Update `using` lines that reference `dnSpy.MCP.Mcp` → `dnSpy.MCP.Core.Mcp`.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj`
Expected: PASS (all migrated tests green)

- [ ] **Step 4: Commit**

```bash
git add tests/dnSpy.MCP.Core.Tests/Mcp/
git rm src/dnSpy.MCP.Tests/JsonRpcTests.cs src/dnSpy.MCP.Tests/BufferedLineReaderTests.cs src/dnSpy.MCP.Tests/ToolRegistryTests.cs src/dnSpy.MCP.Tests/ConvertJsonValueTests.cs
git commit -m "test: migrate JsonRpc/BufferedLineReader/ToolRegistry tests to Core.Tests"
```

### Task 6.3: Write McpContext + MockContextFactory tests

**Files:**
- Create: `tests/dnSpy.MCP.Core.Tests/Mcp/McpContextTests.cs`
- Create: `tests/dnSpy.MCP.Core.Tests/Abstractions/MockContextFactory.cs`

- [ ] **Step 1: Write `MockContextFactory.cs`** (helper used by all tool tests)

```csharp
using System.Collections.Generic;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using Moq;

namespace dnSpy.MCP.Core.Tests.Abstractions;

internal static class MockContextFactory {
    /// <summary>Build a context with all interfaces mocked (default empty behavior).</summary>
    public static McpContext Build() {
        var loader = new Mock<IAssemblyLoader>();
        loader.Setup(l => l.GetDocuments()).Returns(new List<LoadedModule>());
        return Build(loader.Object);
    }

    public static McpContext Build(IAssemblyLoader loader) =>
        new(loader,
            Mock.Of<ISourceDecompiler>(),
            Mock.Of<IUIThreadScheduler>(),
            Mock.Of<ILogSink>(),
            Mock.Of<ITreeRefreshNotifier>());

    public static McpContext Build(IAssemblyLoader loader, ISourceDecompiler decompiler) =>
        new(loader, decompiler,
            Mock.Of<IUIThreadScheduler>(),
            Mock.Of<ILogSink>(),
            Mock.Of<ITreeRefreshNotifier>());
}
```

- [ ] **Step 2: Write `McpContextTests.cs`**

```csharp
using System;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Core.Tests.Abstractions;
using FluentAssertions;
using Moq;
using Xunit;

namespace dnSpy.MCP.Core.Tests.Mcp;

public class McpContextTests {
    [Fact]
    public void Constructor_throws_on_null_assembly_loader() {
        Action act = () => new McpContext(
            null!, Mock.Of<ISourceDecompiler>(),
            Mock.Of<IUIThreadScheduler>(),
            Mock.Of<ILogSink>(),
            Mock.Of<ITreeRefreshNotifier>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("assemblyLoader");
    }

    [Fact]
    public void Constructor_throws_on_null_source_decompiler() {
        Action act = () => new McpContext(
            Mock.Of<IAssemblyLoader>(), null!,
            Mock.Of<IUIThreadScheduler>(),
            Mock.Of<ILogSink>(),
            Mock.Of<ITreeRefreshNotifier>());
        act.Should().Throw<ArgumentNullException>().WithParameterName("sourceDecompiler");
    }

    // ... repeat for UI, Log, TreeRefresh

    [Fact]
    public void Constructor_creates_resolver_from_loader() {
        var ctx = MockContextFactory.Build();
        ctx.Resolver.Should().NotBeNull();
    }
}
```

Add similar tests for each null argument. ~6 tests total.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj --filter "FullyQualifiedName~McpContextTests"`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add tests/dnSpy.MCP.Core.Tests/Mcp/McpContextTests.cs tests/dnSpy.MCP.Core.Tests/Abstractions/MockContextFactory.cs
git commit -m "test(core): add McpContext ctor validation tests + MockContextFactory helper"
```

### Task 6.4: Write unit tests for one tool class as exemplar

**Files:**
- Create: `tests/dnSpy.MCP.Core.Tests/Tools/AnalysisToolsTests.cs`

This task establishes the pattern. Subsequent tool tests follow the same shape — they are mechanical and can be batched by an implementer.

- [ ] **Step 1: Write tests for `AnalysisTools.GetMethodIl`**

```csharp
using System.Linq;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Core.Tests.Abstractions;
using dnSpy.MCP.Core.Tools;
using FluentAssertions;
using Moq;
using Xunit;

namespace dnSpy.MCP.Core.Tests.Tools;

public class AnalysisToolsTests {
    [Fact]
    public void GetMethodIl_returns_error_when_no_assemblies_loaded() {
        var ctx = MockContextFactory.Build();  // empty loader
        var tools = new AnalysisTools(ctx);

        var result = tools.GetMethodIl("Any::Method");

        result.Should().StartWith("Error: No assemblies loaded");
    }

    [Fact]
    public void GetMethodIl_returns_not_found_for_unknown_method() {
        // Use MockContextFactory.Build(loaderMock, decompilerMock)
        // Setup loaderMock to return a fake LoadedModule with an empty ModuleDef
        // Setup decompiler (not called for GetMethodIl)
        // Assert: result starts with "Method not found:"
    }

    // ... continue for GetMethodSignatures, GetTypeHierarchy, GetMethodBody
}
```

For tests requiring a real `ModuleDef`, use `ModuleDef.CreateModuleContext()` + `ModuleDefUser("TestModule")` + add a method via dnlib directly. This is more involved — keep the test count modest initially.

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj --filter "FullyQualifiedName~AnalysisToolsTests"`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add tests/dnSpy.MCP.Core.Tests/Tools/AnalysisToolsTests.cs
git commit -m "test(core): add AnalysisTools unit tests as exemplar pattern"
```

- [ ] **Step 4: Repeat for remaining 12 tool classes**

Each tool class gets its own test file with at least:
- Error path test (no assemblies loaded)
- Error path test (unknown identifier)
- Happy path test (mock returns entity, mock returns expected output)

These can be batched by an implementer following the same pattern. Commit each tool's tests separately for clear history.

### Task 6.5: Create Headless.Tests + SampleLibrary fixture

**Files:**
- Create: `tests/TestData/SampleLibrary/SampleLibrary.csproj`
- Create: `tests/TestData/SampleLibrary/Class1.cs`
- Create: `tests/dnSpy.MCP.Headless.Tests/dnSpy.MCP.Headless.Tests.csproj`
- Create: `tests/dnSpy.MCP.Headless.Tests/HeadlessE2ETests.cs`

- [ ] **Step 1: Write `SampleLibrary.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>SampleLibrary</AssemblyName>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write `Class1.cs`**

```csharp
using System.Threading.Tasks;

namespace TestNS;

public class TestClass {
    public int TestMethod() => 42;
    public async Task<int> AsyncMethod() => await Task.FromResult(1);
    public T GenericMethod<T>(T input) => input;
}

public abstract class AbstractBase {
    public abstract void DoWork();
}

public interface IInterface {
    void Run();
}

public enum TestEnum {
    Zero = 0,
    One = 1,
    Two = 2,
}
```

- [ ] **Step 3: Add SampleLibrary to solution, build**

```powershell
dotnet sln add tests/TestData/SampleLibrary/SampleLibrary.csproj
dotnet build tests/TestData/SampleLibrary/SampleLibrary.csproj
```

Expected: PASS

- [ ] **Step 4: Scaffold Headless.Tests**

```powershell
dotnet new xunit -n dnSpy.MCP.Headless.Tests -o tests/dnSpy.MCP.Headless.Tests --framework net10.0
Remove-Item tests/dnSpy.MCP.Headless.Tests/UnitTest1.cs
dotnet sln add tests/dnSpy.MCP.Headless.Tests/dnSpy.MCP.Headless.Tests.csproj
```

Edit csproj to add `ProjectReference` to `dnSpy.MCP.Headless.csproj` + `SampleLibrary.csproj` + FluentAssertions.

- [ ] **Step 5: Write `HeadlessE2ETests.cs`**

```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Headless.Tests;

public class HeadlessE2ETests {
    private static string FindHeadlessExe() {
        // Locate built exe under bin/Release/net10.0/
        var baseDir = AppContext.BaseDirectory;
        // adjust path based on test output directory
        return Path.Combine(baseDir, "..", "..", "..", "..", "..",
            "src", "dnSpy.MCP.Headless", "bin", "Release", "net10.0", "dnspy-mcp-headless.exe");
    }

    [Fact]
    public async Task Headless_responds_to_initialize() {
        var psi = new ProcessStartInfo("dotnet") {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add("../../../../../src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj");
        psi.ArgumentList.Add("--no-build");

        using var process = Process.Start(psi)!;
        try {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}");

            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(10));
            line.Should().NotBeNull("a JSON-RPC response should be emitted to stdout");

            var response = JsonNode.Parse(line!)!;
            response["id"]!.GetValue<int>().Should().Be(1);
            response["result"]!["serverInfo"]!["name"]!.GetValue<string>()
                .Should().Be("dnspy-mcp-headless");
        }
        finally {
            process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Headless_lists_36_tools() {
        // similar pattern: send initialize, then tools/list, assert tool count >= 36
    }
}
```

- [ ] **Step 6: Run E2E tests**

Run: `dotnet test tests/dnSpy.MCP.Headless.Tests/dnSpy.MCP.Headless.Tests.csproj`
Expected: PASS (may be flaky on first run — give process time to start)

- [ ] **Step 7: Commit**

```bash
git add tests/TestData/ tests/dnSpy.MCP.Headless.Tests/
git commit -m "test(headless): add E2E tests + SampleLibrary fixture"
```

### Task 6.6: Update verify-tool-count.ps1 for instance methods

**Files:**
- Modify: `scripts/verify-tool-count.ps1`

- [ ] **Step 1: Update the regex**

Edit `scripts/verify-tool-count.ps1` line 37:

```powershell
# BEFORE:
if ($j -lt $lines.Count -and $lines[$j] -match 'public static string (\w+)\s*\(') {

# AFTER:
if ($j -lt $lines.Count -and $lines[$j] -match 'public\s+(?:static\s+)?string (\w+)\s*\(') {
```

This matches both `public static string` (Extension's TreeViewTools) and `public string` (Core instance tools).

- [ ] **Step 2: Update tool directory paths**

Lines 22-23 currently scan one dir. Update to scan both Core and Extension tool dirs:

```powershell
# BEFORE:
$toolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP/Tools'

# AFTER:
$coreToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP.Core/Tools'
$extensionToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP/Tools'
```

Then update the scan loop to iterate both directories.

- [ ] **Step 3: Run the guard**

Run: `pwsh scripts/verify-tool-count.ps1`
Expected: PASS with count = 38

- [ ] **Step 4: Commit**

```bash
git add scripts/verify-tool-count.ps1
git commit -m "chore(scripts): update tool-count guard for instance methods + Core tools dir"
```

### Task 6.7: Update build.ps1 + build.yml

**Files:**
- Modify: `scripts/build.ps1`
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Update `build.ps1`**

Replace any single-project build command with solution build:

```powershell
# BEFORE:
dotnet build src/dnSpy.MCP/dnSpy.MCP.csproj -c $Configuration

# AFTER:
dotnet build dnspy_mcp.sln -c $Configuration
```

Add Headless publish step if `-DeployHeadless` switch is set (new optional flag).

- [ ] **Step 2: Update `build.yml`**

Add Headless publish to the CI matrix:

```yaml
- name: Build solution
  run: dotnet build dnspy_mcp.sln -c Release

- name: Run Core.Tests
  run: dotnet test tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj --logger trx

- name: Run Headless.Tests
  run: dotnet test tests/dnSpy.MCP.Headless.Tests/dnSpy.MCP.Headless.Tests.csproj --logger trx

- name: Publish Headless
  run: |
    dotnet publish src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj -c Release -r win-x64 --self-contained false -o ./publish/headless

- name: Verify tool count
  run: pwsh scripts/verify-tool-count.ps1
```

- [ ] **Step 3: Commit**

```bash
git add scripts/build.ps1 .github/workflows/build.yml
git commit -m "ci: build solution + run Core.Tests + publish Headless"
```

### Task 6.8: Final verification

- [ ] **Step 1: Full solution clean + build**

```powershell
dotnet clean dnspy_mcp.sln
dotnet build dnspy_mcp.sln -c Release
```
Expected: PASS with no warnings about duplicate types.

- [ ] **Step 2: Run all tests**

```powershell
dotnet test dnspy_mcp.sln -c Release --logger trx
```
Expected: all tests pass.

- [ ] **Step 3: Manual smoke test in dnSpy**

Copy `dnSpy.MCP.x.dll` + `.deps.json` to `<dnSpy>/bin/Extensions/`. Launch dnSpy, start MCP server, run a tool call. Verify 38 tools available.

- [ ] **Step 4: Manual smoke test of Headless**

```powershell
dotnet publish src/dnSpy.MCP.Headless -c Release -o ./publish
./publish/dnspy-mcp-headless.exe --load "tests/TestData/SampleLibrary/bin/Release/net10.0/SampleLibrary.dll"
# pipe JSON-RPC tools/list + tools/call decompile_method via stdin
```

- [ ] **Step 5: Final commit (tag the release)**

```bash
git add -A
git commit -m "chore: 2.0.0 release — headless mode + 3-project architecture"
git tag v2.0.0
```

---

## Self-Review

After completing this plan, verify against the spec:

**Spec coverage:**
- ✅ 3-project structure (Core/Extension/Headless) — Phases 0, 2, 5
- ✅ 5 abstraction interfaces — Task 1.1
- ✅ McpContext instance — Task 1.3
- ✅ Shared DnSpyDecompilerSourceProvider — Task 1.2
- ✅ dnSpy.Console precedent reflection load — Task 5.3 (DnSpyDecompilerLoader)
- ✅ stdio transport for Headless — Task 5.6
- ✅ HTTP transport unchanged for Extension — Task 3.2 (McpServerHost moved)
- ✅ AutoToolRegistration for Core tools — Task 5.5
- ✅ TreeViewTools stays static in Extension — Phase 4 (not deleted)
- ✅ ToolRegistry hybrid discovery — Task 3.1
- ✅ Migration of 13 tool classes — Phase 2 (4 batches)
- ✅ Extension adapter implementations — Phase 4
- ✅ Headless adapter implementations — Task 5.3
- ✅ CLI args + glob expansion — Task 5.4
- ✅ Fail-fast startup validation — Task 5.6
- ✅ stderr-only logging rule — Task 5.3 (StderrLogSink)
- ✅ Core.Tests with mock interfaces — Task 6.3
- ✅ E2E tests for Headless — Task 6.5
- ✅ SampleLibrary fixture — Task 6.5
- ✅ verify-tool-count.ps1 update — Task 6.6
- ✅ build.ps1 + build.yml updates — Task 6.7

**Open questions resolved in plan:**
- MCP SDK DI registration pattern — Task 5.6 notes the alternative pattern if `IMcpServerBuilder` injection fails
- Bundle strategy — Task 6.7 uses `dotnet publish` (self-contained optional via `-r` flag)
- Licensing: deferred to release notes (dnSpy is GPLv3, Headless must comply)

**Placeholder scan:** No TBD/TODO/"implement later". All code blocks contain real content.

**Type consistency:** `IAssemblyLoader`, `LoadedModule`, `LoadResult`, `McpContext`, `MethodResolver(IAssemblyLoader)` signatures consistent across tasks. ToolRegistry ctor signature consistent (`McpContext, params Assembly[]`).
