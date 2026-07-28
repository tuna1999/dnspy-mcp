# Headless Mode Design

- **Date**: 2026-07-28
- **Status**: Approved (brainstorming complete, ready for implementation plan)
- **Author**: tuna99
- **Target version**: dnSpy.MCP 2.0.0

## Summary

Refactor the existing dnSpy.MCP extension into a 3-project solution that supports
both (a) the existing in-dnSpy extension with HTTP transport, and (b) a new
standalone headless console executable with stdio MCP transport for batch
analysis. Tools share a common `dnSpy.MCP.Core` library with 5 abstraction
interfaces, enabling full testability without dnSpy running.

## Goals

1. Run dnSpy.MCP tools without opening dnSpy.exe — enables batch analysis of
   hundreds of DLLs in CI/CD, malware pipelines, and AI-agent-driven scans.
2. Use **stdio MCP transport** (per MCP ecosystem convention) so Claude Desktop,
   Cursor, and VS Code can auto-spawn the headless binary.
3. Decompile output **identical to dnSpy.exe** — reuse the same
   `dnSpy.Decompiler.ILSpy.Core.dll` rather than re-wrapping ILSpy.
4. Make tool classes **unit-testable** — replace the static `DnSpyContext`
   ambient dependency with an instance `McpContext` injected via constructor.

## Non-goals

1. **No replacement of the existing HTTP Extension.** The in-dnSpy extension
   (port 5150) stays as-is and continues to work for interactive use inside dnSpy.
2. **No migration to official MCP SDK in Core.** Custom `ToolRegistry` reflection
   discovery stays. Only Headless uses MCP SDK (for stdio transport + DI).
3. **No HTTP transport in Headless.** Stdio is sufficient for the batch use case.
   HTTP can be added later if a remote-deployment use case emerges.
4. **No preservation of `DnSpyContext` backward compatibility.** It is replaced
   by `McpContext`. Existing Extension code is refactored to use the new context.

## Architecture

### Solution structure

```
dnspy_mcp/
├── dnspy_mcp.sln
├── src/
│   ├── dnSpy.MCP.Core/                  ← NEW: pure analysis lib (no WPF)
│   │   ├── Abstractions/                ← 5 interfaces
│   │   ├── Adapters/                    ← shared dnSpy decompiler bridge
│   │   ├── Mcp/                         ← McpContext, ToolRegistry, McpServerHost
│   │   ├── Helpers/                     ← MethodResolver
│   │   └── Tools/                       ← 13 instance tool classes
│   │
│   ├── dnSpy.MCP/                       ← Extension (refactored to ref Core)
│   │   ├── Adapters/                    ← 4 WPF/dnSpy-backed impls
│   │   ├── Tools/TreeViewTools.cs       ← stays static, Extension-only
│   │   └── Settings/                    ← unchanged
│   │
│   └── dnSpy.MCP.Headless/              ← NEW: standalone Exe
│       ├── Program.cs                   ← host + DI + CLI parse
│       ├── CliOptions.cs
│       └── Adapters/                    ← 5 inline/dnlib impls
│
└── tests/
    ├── dnSpy.MCP.Core.Tests/            ← NEW: unit tests, mock interfaces
    ├── dnSpy.MCP.Tests/                 ← existing, migrated
    ├── dnSpy.MCP.Headless.Tests/        ← NEW: E2E via process spawn
    └── TestData/SampleLibrary/          ← fixture project, built with solution
```

### Three-project rationale (B3' — DI-based hybrid)

Rejected alternatives:

- **B1 — Keep dnSpy.Contracts in Core**: MEF composition cannot resolve services
  without dnSpy.exe host (Contracts DLLs only contain interfaces). Would force
  re-implementing service layer anyway, so no benefit over B2.
- **B2 — Drop dnSpy.Contracts, wrap ILSpy directly**: Loses output-format parity
  with dnSpy.exe. Stale `PEFile` snapshot risk after `update_method_body`.
  Discovered `dnSpy.Console/Program.cs` precedent that already solves this
  cleanly via `IDecompilerProvider` reflection load.
- **B3 — `#if HEADLESS` build flag**: ~25-35 directive points across 12 files,
  invisible to IntelliSense in the other configuration, anti-pattern at this
  seam density.

Chosen: **B3' (DI-based)** — interfaces + 2 host projects. 95% code reuse,
compiler-enforced seam, IDE-friendly, testable. Extends the existing
`IMcpExtension` interface pattern already used to break MEF import cycles.

### Dependency graph

```
┌──────────────────────────────────────────────────────────────┐
│ dnSpy.MCP.Core (lib, net10.0, no WPF)                        │
│                                                              │
│  References:                                                 │
│   • dnlib.dll                                                │
│   • dnSpy.Contracts.DnSpy.dll  (LoadedModule, IDecompiler)   │
│   • dnSpy.Contracts.Logic.dll  (TextWriterDecompilerOutput)  │
│   • Microsoft.CodeAnalysis.CSharp (IL patch compile)          │
└──────────────────────────────────────────────────────────────┘
            ▲                                  ▲
            │                                  │
┌───────────┴──────────────┐    ┌──────────────┴────────────────────┐
│ dnSpy.MCP (Extension)    │    │ dnSpy.MCP.Headless (Exe)           │
│ net10.0-windows, WPF     │    │ net10.0, no WPF                   │
│                          │    │                                    │
│ Refs Core +:             │    │ Refs Core +:                       │
│  • VS.Composition (MEF)  │    │  • dnSpy.Decompiler.dll            │
│  • VS.Text.UI.Wpf        │    │  • dnSpy.Decompiler.ILSpy.Core.dll │
│                          │    │  • ICSharpCode.Decompiler.dll      │
│                          │    │  • ICSharpCode.NRefactory*.dll     │
│                          │    │  • ModelContextProtocol (SDK)      │
│                          │    │  • Microsoft.Extensions.Hosting    │
└──────────────────────────┘    └────────────────────────────────────┘
```

## Components

### McpContext (instance, replaces DnSpyContext)

Typed composition root — holds 5 dependencies and constructs a `MethodResolver`
from the loader. Not a service locator: tools reference concrete properties
(`ctx.AssemblyLoader`), not `ctx.GetService<T>()`.

```csharp
public sealed class McpContext {
    public IAssemblyLoader AssemblyLoader { get; }
    public ISourceDecompiler SourceDecompiler { get; }
    public IUIThreadScheduler UI { get; }
    public ILogSink Log { get; }
    public ITreeRefreshNotifier TreeRefresh { get; }
    public MethodResolver Resolver { get; }

    public McpContext(IAssemblyLoader assemblyLoader,
                      ISourceDecompiler sourceDecompiler,
                      IUIThreadScheduler ui,
                      ILogSink log,
                      ITreeRefreshNotifier treeRefresh) { /* null-check each */ }
}
```

### 5 Abstraction interfaces

| Interface | Methods | Extension impl | Headless impl |
|---|---|---|---|
| `IAssemblyLoader` | `Load`, `Close`, `GetDocuments` | Wraps `IDsDocumentService` | `ModuleDefMD.Load` + dictionary registry |
| `ISourceDecompiler` | `DecompileMethod/Type/Field/Property/Event/Module` | (shared adapter, see below) | (shared adapter, see below) |
| `IUIThreadScheduler` | `Invoke<T>`, `Invoke` | WPF `Dispatcher.Invoke` | Inline `action()` (no-op marshal) |
| `ILogSink` | `Info`, `Warn`, `Error` | File + dnSpy Output Pane | **stderr only** (stdio MCP rule) |
| `ITreeRefreshNotifier` | `RefreshAll`, `NotifyNamespaceRenamed` | Delegates to `TreeViewTools` | No-op |

Records:
- `LoadResult(bool Success, string? Error, LoadedModule? Module)`
- `LoadedModule(string Name, string? AssemblyName, ModuleDef Module, string Path)`
  — Module property is mutable; in-place IL mutations are visible through it.

### Shared adapter — DRY for decompiler bridge

`DnSpyDecompilerSourceProvider` lives in Core, used by **both** hosts.
Composition root supplies the `dnSpy.Contracts.Decompiler.IDecompiler` instance:

- Extension: `_decompilerService.Decompiler` (from MEF)
- Headless: `DnSpyDecompilerLoader.LoadCSharp()` (reflection load of
  `dnSpy.Decompiler.ILSpy.Core.dll` via `IDecompilerProvider`)

```csharp
public sealed class DnSpyDecompilerSourceProvider : ISourceDecompiler {
    public DnSpyDecompilerSourceProvider(IDecompiler decompiler) { /* null-check */ }

    public string DecompileMethod(MethodDef method) =>
        DecompileCore((d, o, c) => d.Decompile(method, o, c));

    // DecompileType, DecompileField, DecompileProperty, DecompileEvent, DecompileModule: same pattern

    private static string DecompileCore(
        Action<IDecompiler, IDecompilerOutput, DecompilationContext> decompose) {
        var writer = new StringWriter();
        using var output = new TextWriterDecompilerOutput(writer, new Indenter(4, 4, true));
        decompose(_decompiler, output, new DecompilationContext());
        return writer.ToString();
    }
}
```

**Reuse** of `TextWriterDecompilerOutput` (built into `dnSpy.Contracts.Logic`)
**replaces** the custom `TextDecompilerOutput` (~53 LOC deleted). Output format
is guaranteed identical to dnSpy.exe.

### dnSpy.Console precedent

`dnspy-source/dnSpy/dnSpy.Console/Program.cs` (1057 LOC) is the established
headless pattern in dnSpy itself:

```csharp
// From Program.cs:226-247
static IEnumerable<IDecompiler> GetLanguagesInAssembly(string asmName) {
    var asm = TryLoad(asmName);  // Assembly.Load
    foreach (var type in asm.GetTypes()) {
        if (!type.IsAbstract && !type.IsInterface
            && typeof(IDecompilerProvider).IsAssignableFrom(type)) {
            var p = (IDecompilerProvider)Activator.CreateInstance(type)!;
            foreach (var l in p.Create()) yield return l;
        }
    }
}
```

Contract from `dnSpy.Contracts.Logic/Decompiler/IDecompilerProvider.cs`:

```csharp
/// Returns decompilers. It must have a default constructor.
public interface IDecompilerProvider { IEnumerable<IDecompiler> Create(); }
```

Comment in `CSharpDecompiler.cs:42`:
> `// Keep the default ctor. It's used by dnSpy.Console.exe`

This is a stable public contract designed for headless use. The Headless
project follows this exact pattern.

### Tool class refactor

Each Core tool class converts from static to instance:

```csharp
public sealed class DecompilerTools {
    private readonly McpContext _ctx;
    public DecompilerTools(McpContext ctx) => _ctx = ctx;

    [Description("Decompile a specific method to C# code...")]
    public string DecompileMethod(string methodFullNameOrToken) {
        if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
            return "Error: No assemblies loaded.";
        var method = _ctx.Resolver.ResolveMethodFlexible(methodFullNameOrToken);
        if (method == null) return $"Method not found: {methodFullNameOrToken}";
        try { return _ctx.SourceDecompiler.DecompileMethod(method); }
        catch (Exception ex) {
            _ctx.Log.Error($"Decompile failed: {methodFullNameOrToken}", ex);
            return $"Decompilation failed: {ex.Message}";
        }
    }
}
```

`TreeViewTools` stays static (Extension-only — uses `DnSpyContext.TreeView`,
no `McpContext` needed). Provides `get_selected_node` and `refresh_u_i`.

### ToolRegistry — hybrid instance/static discovery

```csharp
public ToolRegistry(McpContext ctx, params Assembly[] assemblies) {
    DiscoverTools(ctx, assemblies);
}

private static bool IsToolClass(Type type) {
    if (type.Namespace?.StartsWith("dnSpy.MCP.Tools") != true) return false;
    if (!type.IsClass || type.IsAbstract) return false;
    // Either static class OR instance class with ctor(McpContext)
    return type.IsStatic || type.GetConstructor(new[] { typeof(McpContext) }) != null;
}
```

When scanning, instance classes are instantiated once and reused across all
their tool methods. Static classes pass `instance = null` to `Method.Invoke`.

### McpServerHost change (Extension only)

Constructor gains a `ToolRegistry` parameter (previously it created the
registry internally):

```csharp
public McpServerHost(McpSettings settings, ToolRegistry registry) {
    _settings = settings;
    _concurrency = new SemaphoreSlim(settings.MaxConcurrency);
    _registry = registry ?? throw new ArgumentNullException(nameof(registry));
}
```

## Data Flow

### Extension: HTTP request lifecycle (unchanged shape)

```
AI Agent → POST http://127.0.0.1:5150/ (JSON-RPC 2.0, Bearer auth)
  → McpServerHost.HandleConnection
    → BufferedLineReader parses HTTP request line + headers
    → Auth check (CryptographicOperations.FixedTimeEquals)
    → Size check (≤ MaxRequestSizeMB)
    → Parse JSON-RPC batch
    → For each request:
      → HandleToolCallAsync
        → _registry.GetTool(name)
        → if tool.IsMutation: await _mutationLock.WaitAsync(timeout)
        → tool.Invoke(args) → Method.Invoke(instance, callArgs)
          → instance is tool class injected with McpContext
            → _ctx.AssemblyLoader / Resolver / SourceDecompiler / TreeRefresh
        → finally: heldLock?.Release()
      → wrap result in JSON-RPC response
    → write response to stream
```

Concurrency: read-only tools parallel up to `MaxConcurrency` (default 4);
mutation tools (`update_method_body`, `rename_*`) serialized via `_mutationLock`.

### Headless: stdio request lifecycle

```
MCP Client spawns dnspy-mcp-headless.exe (child process)
  → Microsoft.Extensions.Hosting builds host
  → McpContext created as singleton in DI:
    → DnlibAssemblyLoader pre-loads CLI --load paths
    → DnSpyDecompilerLoader.LoadCSharp() loads dnSpy.Contracts.IDecompiler
  → AddMcpServer().WithStdioServerTransport()
  → AutoToolRegistration.RegisterAll(builder, ctx)  ← Core has no [McpServerToolType]
  → run

Client writes JSON-RPC frame to stdin (newline-delimited)
  → MCP SDK StdioServerTransport parses frame
  → dispatch: initialize / tools/list / tools/call
  → tools/call → McpServerTool.Create-wrapped delegate
    → AutoToolRegistration mapped tool name → Core tool method
    → tool executes (IDENTICAL to Extension: _ctx.SourceDecompiler.DecompileMethod)
  → response → stdout

Client logs/errors captured from stderr
```

Concurrency: stdio is single-connection, sequential. No `_mutationLock` needed.

### Auto-wrap Core tools into MCP SDK

`AutoToolRegistration.RegisterAll(builder, ctx)` scans Core assembly via
reflection and wraps each `[Description]`-attributed method as an
`McpServerTool.Create(method, instance)`:

```csharp
public static void RegisterAll(IMcpServerBuilder builder, McpContext ctx) {
    var coreAsm = typeof(McpContext).Assembly;
    foreach (var type in coreAsm.GetTypes()) {
        if (!IsToolClass(type)) continue;
        var instance = Activator.CreateInstance(type, ctx);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
            var desc = method.GetCustomAttribute<DescriptionAttribute>();
            if (desc == null) continue;
            var toolName = ToolRegistry.ToSnakeCase(method.Name);
            builder.WithTools(McpServerTool.Create(method, instance,
                new McpServerToolCreateOptions { Name = toolName, Description = desc.Description }));
        }
    }
}
```

~40 LOC vs ~650 LOC manual wrappers. Auto-discovers future Core tools.

### CLI args

```
dnspy-mcp-headless.exe [--load <path>...] [--config <json>] [--help]
```

- `--load <path>` (repeatable, supports `*`/`?` glob): pre-load assemblies at startup
- `--config <json>`: optional config file (currently unused; placeholder for future)
- `--help`: print usage to stderr, exit 0

Glob expansion via `Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)`.

### Decompile trace — verification of architecture

Tracing `decompile_method("MyApp.Foo::Bar")` through both hosts:

| Step | Extension | Headless |
|---|---|---|
| 1. Receive | HTTP POST | stdin JSON-RPC frame |
| 2. Parse | BufferedLineReader | MCP SDK StdioServerTransport |
| 3. Dispatch | ToolRegistry.GetTool | AutoToolRegistration map |
| 4. Resolve method | `_ctx.Resolver.ResolveMethodFlexible` | **identical** |
| 5. Load `ModuleDef` | `DnSpyAssemblyLoader` → `IDsDocumentService` | `DnlibAssemblyLoader` → `ModuleDefMD.Load` |
| 6. Decompile | `DnSpyDecompilerSourceProvider.DecompileMethod` | **identical** |
| 7. Bootstrap decompiler | MEF `IDecompilerService.Decompiler` | `DnSpyDecompilerLoader.LoadCSharp()` |
| 8. Return | HTTP response | stdout JSON-RPC |

Steps 4 and 6 are byte-for-byte identical across hosts — interfaces are correctly placed.

## Error Handling

### Categories

| Category | Extension | Headless |
|---|---|---|
| Transport | TCP/reset → log + close | stdin EOF → host exits |
| Auth | 401 (constant-time compare) | N/A (IPC, parent only) |
| Size | 413 (≤ MaxRequestSizeMB) | SDK-enforced |
| JSON-RPC protocol | -32700/-32600/-32601/-32602 | SDK-enforced |
| Tool not found | -32601 | SDK-enforced |
| Tool argument | -32602 with message | SDK wraps tool exception |
| Tool user-error string | Returned as success result (string in content[0].text) | Same — SDK sees a normal string return, no error code |
| Tool throws exception | -32603 with detail | SDK wraps tool exception as -32603 |
| Tool timeout | `Task.Run().WaitAsync(timeout)` | SDK CancellationToken |
| Mutation race | `_mutationLock` serializes | N/A (sequential stdio) |
| DLL load failure | N/A (loaded by dnSpy) | Fail-fast at startup, exit code 3 |

### Tool-level error pattern (unchanged)

Tools return error strings for user-facing issues; throw for infrastructure
errors. Backward-compatible with `ToolRegistry.Invoke` returning `string`.

```csharp
public string DecompileMethod(string methodFullNameOrToken) {
    if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
        return "Error: No assemblies loaded.";          // user-facing
    // ...
    try { return _ctx.SourceDecompiler.DecompileMethod(method); }
    catch (Exception ex) {
        _ctx.Log.Error(...);                              // infra
        return $"Decompilation failed: {ex.Message}";     // user-facing
    }
}
```

### Headless startup validation (fail-fast)

```csharp
try {
    var cli = CliOptions.Parse(args);
    if (cli.ShowHelp) { PrintHelp(); return 0; }
    foreach (var path in cli.PreLoads) {
        if (!File.Exists(path)) {
            Console.Error.WriteLine($"Error: --load file not found: {path}");
            return 2;
        }
    }
    // Validate decompiler DLL loads BEFORE accepting requests
    _ = DnSpyDecompilerLoader.LoadCSharp();
    await builder.Build().RunAsync();
    return 0;
}
catch (FileNotFoundException ex) {
    Console.Error.WriteLine($"Missing dnSpy DLL: {ex.Message}");
    Console.Error.WriteLine("Ensure dnSpy.Decompiler.ILSpy.Core.dll is next to the exe.");
    return 3;
}
```

### Logging channels

| Channel | Extension | Headless |
|---|---|---|
| File (`mcp-server.log`) | ✅ bin dir | ✅ bin dir |
| dnSpy Output Pane | ✅ via `IOutputService` | ❌ |
| **stderr** | ❌ | ✅ **CRITICAL** (stdout reserved for MCP JSON-RPC) |
| `Microsoft.Extensions.Logging` | ❌ | ✅ via SDK |

`StderrLogSink` is the only place that writes to `Console.Error`. No code outside
this class may call `Console.Error.WriteLine` directly — enforced by code review
(this prevents accidental stdout writes that would corrupt the protocol stream).

## Testing Strategy

### Testability unlocked

Memory `dnspy-mcp-test-strategy.md` recorded:
> "what's unit-testable (JsonRpc, BufferedLineReader, ToolRegistry helpers)
>  vs blocked by static DnSpyContext"

With `McpContext` replacing `DnSpyContext`, **all 13 Core tool classes are
now unit-testable** via mock interfaces.

### Test pyramid

```
E2E (3-5 tests)         real Headless exe, JSON-RPC via stdin
Integration (10-15)     McpContext with real adapters, real .dll fixture
Unit (50-100)           mock 5 interfaces, test tool logic in isolation
```

### Test project layout

```
tests/
├── dnSpy.MCP.Core.Tests/        (NEW)
│   ├── dnSpy.MCP.Core.Tests.csproj   (xUnit + FluentAssertions + Moq)
│   ├── Mcp/McpContextTests.cs
│   ├── Mcp/ToolRegistryTests.cs      (migrated)
│   ├── Mcp/JsonRpcTests.cs           (migrated)
│   ├── Mcp/BufferedLineReaderTests.cs (migrated)
│   ├── Helpers/MethodResolverTests.cs (mock IAssemblyLoader)
│   └── Tools/*.cs                    (13 tool classes, each with mock-based tests)
│
├── dnSpy.MCP.Tests/             (existing, refactored)
│   ├── ConvertJsonValueTests.cs (migrated)
│   └── IlPatchToolsTests.cs     (Roslyn compile + IL verify)
│
├── dnSpy.MCP.Headless.Tests/    (NEW)
│   └── HeadlessE2ETests.cs      (process spawn, stdin/stdout JSON-RPC)
│
└── TestData/SampleLibrary/      (NEW)
    ├── SampleLibrary.csproj     (built with solution, deterministic)
    └── Class1.cs                (TestNS.TestClass with TestMethod etc.)
```

### Sample unit test pattern

```csharp
[Fact]
public void DecompileMethod_returns_error_when_no_assemblies_loaded() {
    var loaderMock = new Mock<IAssemblyLoader>();
    loaderMock.Setup(l => l.GetDocuments()).Returns(new List<LoadedModule>());
    var ctx = new McpContext(loaderMock.Object, Mock.Of<ISourceDecompiler>(),
        Mock.Of<IUIThreadScheduler>(), Mock.Of<ILogSink>(), Mock.Of<ITreeRefreshNotifier>());

    var tools = new DecompilerTools(ctx);

    tools.DecompileMethod("Foo::Bar").Should().StartWith("Error: No assemblies loaded");
}
```

### Coverage targets

| Component | Target |
|---|---|
| `McpContext` ctor validation | 100% |
| `ToolRegistry` discovery | 95%+ |
| `MethodResolver` resolution | 90%+ |
| Tool methods | 80%+ |
| `JsonRpc`, `BufferedLineReader` | 95%+ (existing) |
| Adapters (DnSpyAssemblyLoader etc) | 70%+ |
| `McpServerHost` | 60%+ (existing) |

### E2E test (Headless)

```csharp
[Fact]
public async Task Headless_responds_to_initialize_and_tools_list() {
    var psi = new ProcessStartInfo(FindHeadlessExe()) {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(psi)!;

    await process.StandardInput.WriteLineAsync(
        @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}");
    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    var response = JsonNode.Parse(line!)!;
    response["result"]!["serverInfo"]!["name"]!.GetValue<string>()
        .Should().Be("dnspy-mcp-headless");

    // ... tools/list, then assert 30+ tools available
}
```

### Test fixture

`tests/TestData/SampleLibrary/SampleLibrary.csproj` builds alongside the
solution and provides:
- `TestNS.TestClass.TestMethod()` — simple method returning int
- `TestNS.TestClass.AsyncMethod()` — async state machine
- `TestNS.TestClass.GenericMethod<T>()` — generic method
- `TestNS.AbstractBase`, `TestNS.IInterface`, `TestNS.TestEnum`

Deterministic test data — no dependency on system DLLs.

## Migration Plan

Phases (high-level; full step-by-step plan in subsequent writing-plans output):

**Phase 0 — Solution skeleton** (no logic changes; establish build graph)
1. Create empty `dnspy_mcp.sln` with existing `dnSpy.MCP.csproj` + `dnSpy.MCP.Tests.csproj` (verify existing build still works through solution)
2. Create `src/dnSpy.MCP.Core/dnSpy.MCP.Core.csproj` (empty lib, net10.0, no WPF). `dotnet sln add` it.
3. Add `<ProjectReference>` from `dnSpy.MCP.csproj` → `dnSpy.MCP.Core.csproj`. Verify build.

**Phase 1 — Core abstractions** (no breaking changes to Extension yet)
4. Create 5 Abstraction interfaces (`IAssemblyLoader`, `ISourceDecompiler`, `IUIThreadScheduler`, `ILogSink`, `ITreeRefreshNotifier`) + records (`LoadResult`, `LoadedModule`)
5. Create `Adapters/DnSpyDecompilerSourceProvider.cs` (shared, depends only on dnSpy.Contracts)
6. Create `Mcp/McpContext.cs` instance class
7. Create `Helpers/MethodResolver.cs` in Core with ctor accepting `IAssemblyLoader` (copy from Extension, change ctor param)
8. Move `Mcp/JsonRpc.cs`, `Mcp/BufferedLineReader.cs` to Core (verbatim). **Split `Mcp/McpLogger.cs`**: file-logging half moves to Core, dnSpy Output Pane half stays in Extension.
9. **Checkpoint**: `dotnet build dnSpy.MCP.Core.csproj` should pass with no tools yet, just infra.

**Phase 2 — Tool classes migration** (10 sub-steps, one per tool class batch)
10. Move + refactor tool classes to Core, batched by complexity:
    - Batch A (pure dnlib, trivial): `AnalysisTools`, `IlDisplayTools`, `TypeInspectorTools`, `AttributeTools`, `ConstantTools`, `NamespaceTools` (6 files, ~600 LOC)
    - Batch B (use resolver): `SearchTools`, `XrefTools` (2 files, ~250 LOC)
    - Batch C (use loader + decompiler): `DecompilerTools`, `AssemblyTools`, `ResourceTools` (3 files, ~460 LOC)
    - Batch D (mutation, use tree refresh): `IlPatchTools`, `RenameTools` (2 files, ~560 LOC)
    Each batch: convert `static class` → `sealed class`, add ctor(McpContext), replace `DnSpyContext.X` → `_ctx.X`. **After each batch: build Core, fix any errors before next batch.**

**Phase 3 — ToolRegistry refactor** (depends on McpContext from Phase 1)
11. Refactor `ToolRegistry` to support instance + static mix (`ctor(McpContext, params Assembly[])`, hybrid `IsToolClass` filter). Move to Core.
12. **Checkpoint**: write minimal Extension adapter stubs returning `null`/empty (e.g. `class StubAssemblyLoader : IAssemblyLoader { ... }`) to let Extension compile temporarily. This decouples Extension refactor from Headless work.

**Phase 4 — Extension rewire** (depends on Core being complete)
13. Create Extension adapters: `DnSpyAssemblyLoader`, `WpfUIThreadScheduler`, `DnSpyLogSink`, `DnSpyTreeRefreshNotifier` (replace stubs from step 12)
14. Refactor `TheExtension.OnEvent` to compose McpContext + ToolRegistry
15. Update `McpServerHost` ctor to accept `ToolRegistry` (passed from TheExtension)
16. Delete old `DnSpyContext.cs`, `MethodResolver.cs` (moved), `McpLogger.cs` (split), `TextDecompilerOutput.cs` (replaced by dnSpy's)
17. **Checkpoint**: `dotnet build` solution — Extension should run inside dnSpy with all 38 tools available.

**Phase 5 — Headless project** (depends on Core being complete)
18. Create `src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj` (Exe, refs Core + dnSpy DLLs + MCP SDK)
19. Create 5 Headless adapters: `DnlibAssemblyLoader`, `DnSpyDecompilerLoader`, `InlineUIThreadScheduler`, `StderrLogSink`, `NoOpTreeRefreshNotifier`
20. Create `Program.cs` + `CliOptions.cs` + `Adapters/AutoToolRegistration.cs`
21. **Checkpoint**: `dotnet run --project dnSpy.MCP.Headless -- --help` works; manual test `initialize` / `tools/list` via stdin.

**Phase 6 — Tests + CI**
22. Create `tests/dnSpy.MCP.Core.Tests/dnSpy.MCP.Core.Tests.csproj` (xUnit + FluentAssertions + Moq)
23. Migrate existing tests (`JsonRpcTests`, `BufferedLineReaderTests`, `ToolRegistryTests`) to Core.Tests
24. Add new unit tests for 13 tool classes (mock 5 interfaces)
25. Create `tests/dnSpy.MCP.Headless.Tests/` + `tests/TestData/SampleLibrary/` (fixture project)
26. Add E2E test: spawn Headless exe, JSON-RPC via stdin
27. Update `scripts/verify-tool-count.ps1` regex (`public string` not `public static string`), check both Core tools dir + Extension tools dir
28. Update `scripts/build.ps1` to build solution instead of single project; update `build.yml` matrix

### Tool count after migration

- Extension: **38 tools** (13 Core classes × ~3 tools + TreeViewTools × 2)
- Headless: **36 tools** (no `get_selected_node`, no `refresh_u_i`)

`verify-tool-count.ps1` checks Core tools dir for both binaries; Extension
includes its own 2 tools via separate scan.

## LOC budget

| Component | LOC | Notes |
|---|---|---|
| `dnSpy.MCP.Core` | ~3,195 | refactor + new abstractions |
| `dnSpy.MCP` (Extension delta) | ~562 | adapters + TheExtension refactor |
| `dnSpy.MCP.Headless` | ~385 | new project |
| Test projects | ~800 | new unit + E2E + fixture |
| **Total** | **~4,942** | vs current ~2,500 |

Net new code: ~2,400 LOC, of which ~1,500 is mechanical refactor (static → instance,
no new behavior). The actual new capability is ~900 LOC (5 Headless adapters,
shared decompiler bridge, CLI parsing, AutoToolRegistration, tests).

## Open questions for implementation phase

These are deferred to the implementation plan, not blockers for this spec:

1. **MCP SDK `McpServerTool.Create` schema generation**: does it auto-parse
   `[Description]` on parameters to build JSON schema, or do we need to
   build schema manually? If manual, +50 LOC in `AutoToolRegistration`.
2. **dnSpy.Decompiler.ILSpy.Core.dll licensing**: confirm GPLv3-compatible
   distribution model for Headless bundle. dnSpy itself is GPLv3; we already
   distribute the Extension under same terms.
3. **Bundle strategy**: publish Headless as `dotnet tool` (single NuGet) or
   self-contained exe via `PublishSingleFile`? Self-contained is friendlier
   for batch pipelines.
4. **`ScopedWhereUsedAnalyzer` improvement**: the existing `XrefTools.GetXrefsTo`
   scans all modules unconditionally. Adopting the dnSpy.Analyzer pattern
   (accessibility-aware scoping) is a separate enhancement, not part of this spec.

## References

- [`dnspy-source/dnSpy/dnSpy.Console/Program.cs`](../../dnspy-source/dnSpy/dnSpy.Console/Program.cs) — headless precedent
- [`dnspy-source/dnSpy/dnSpy.Contracts.Logic/Decompiler/IDecompilerProvider.cs`](../../dnspy-source/dnSpy/dnSpy.Contracts.Logic/Decompiler/IDecompilerProvider.cs) — "default ctor" contract
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — stdio transport, DI integration
- [Build a Model Context Protocol server in C# — Microsoft .NET Blog](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/)
- [MCP C# SDK Transports](https://modelcontextprotocol-csharp-sdk.mintlify.app/concepts/transports)
- [MCP C# SDK DI and Parameter Resolution](https://deepwiki.com/modelcontextprotocol/csharp-sdk/3.6-dependency-injection-and-parameter-resolution)
