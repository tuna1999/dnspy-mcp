# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

### Setup (one-time)

```powershell
mkdir deps
# Option A (recommended): run the sync script against a local dnSpy install
pwsh scripts/sync-deps.ps1  # uses D:\ProgramFiles\StandaloneTools\RETools\dnSpy\win64\bin by default
pwsh scripts/sync-deps.ps1 -DnSpyBin "C:\path\to\dnSpy\bin"  # override path

# Option B (manual): copy these DLLs from a dnSpy installation's bin/ folder:
#   dnSpy.Contracts.DnSpy.dll
#   dnSpy.Contracts.Logic.dll
#   dnlib.dll
#   ICSharpCode.Decompiler.dll
#   # Headless-only:
#   dnSpy.Decompiler.dll
#   dnSpy.Decompiler.ILSpy.Core.dll
#   ICSharpCode.NRefactory.dll
#   ICSharpCode.NRefactory.CSharp.dll
```

### Local Development

```powershell
# Build entire solution (Core + Extension + Headless + Tests)
dotnet build dnspy_mcp.sln -c Release

# Deploy extension only (requires dnSpy closed)
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy

# Run Headless (stdio MCP server)
dotnet run --project src/dnSpy.MCP.Headless/dnSpy.MCP.Headless.csproj -- --load path\to\file.dll
```

Options: `-Clean`, `-Deploy`, `-DeployDir <path>`, `-Configuration <Debug|Release>`

### CI

GitHub Actions (`build.yml`) auto-downloads dnSpy deps and runs `dotnet build dnspy_mcp.sln -c Release`. No manual setup needed.

**Tool-count guard**: after adding/removing a tool, run `pwsh scripts/verify-tool-count.ps1`. It cross-checks the count discovered by reflection against the `## Available MCP Tools (NN)` header here — scans BOTH Core tools (instance methods on sealed classes with McpContext ctor) AND Extension-only tools (static methods, e.g. TreeViewTools). Fails on drift so docs and code can't silently diverge.

### Build output

- **Core lib**: `src/dnSpy.MCP.Core/bin/Release/net10.0/dnSpy.MCP.Core.dll`
- **Extension DLL**: `src/dnSpy.MCP/bin/Release/net10.0-windows/dnSpy.MCP.x.dll`
- **Headless exe**: `src/dnSpy.MCP.Headless/bin/Release/net10.0/dnspy-mcp-headless.dll`
- **Deploy to dnSpy**: copy `dnSpy.MCP.x.dll`, `.deps.json`, `.pdb`, AND `dnSpy.MCP.Core.dll` to `<dnSpy>/bin/Extensions/`

## Project Layout

```
dnspy_mcp/
├── dnspy_mcp.sln                # Solution referencing all 3 projects + tests
├── src/
│   ├── dnSpy.MCP.Core/          # Pure analysis library (no WPF, net10.0)
│   │   ├── Abstractions/        # 5 host-agnostic interfaces
│   │   │   ├── IAssemblyLoader.cs
│   │   │   ├── ISourceDecompiler.cs
│   │   │   ├── IUIThreadScheduler.cs
│   │   │   ├── ILogSink.cs
│   │   │   └── ITreeRefreshNotifier.cs
│   │   ├── Adapters/
│   │   │   └── DnSpyDecompilerSourceProvider.cs  # Shared IDecompiler bridge
│   │   ├── Mcp/
│   │   │   ├── McpContext.cs        # Instance composition root (5 deps + Resolver)
│   │   │   ├── ToolRegistry.cs      # Hybrid instance/static reflection discovery
│   │   │   ├── McpServerHost.cs     # TcpListener + JSON-RPC 2.0 dispatch (HTTP)
│   │   │   ├── JsonRpc.cs           # JSON-RPC 2.0 protocol helpers
│   │   │   ├── BufferedLineReader.cs # HTTP/stdio line reader
│   │   │   └── McpLogger.cs         # File-only logging (Level enum)
│   │   ├── Helpers/
│   │   │   └── MethodResolver.cs    # ctor(IAssemblyLoader) — host-agnostic
│   │   ├── Settings/
│   │   │   └── McpSettings.cs       # POCO base (ViewModelBase)
│   │   └── Tools/                   # 13 instance tool classes, 36 tools
│   │       ├── DecompilerTools.cs
│   │       ├── AssemblyTools.cs
│   │       ├── SearchTools.cs
│   │       ├── AnalysisTools.cs
│   │       ├── XrefTools.cs
│   │       ├── IlDisplayTools.cs
│   │       ├── IlPatchTools.cs      # IL patching via Roslyn compilation
│   │       ├── ResourceTools.cs
│   │       ├── TypeInspectorTools.cs
│   │       ├── AttributeTools.cs
│   │       ├── ConstantTools.cs
│   │       ├── NamespaceTools.cs
│   │       └── RenameTools.cs
│   │
│   ├── dnSpy.MCP/               # Extension (WPF, net10.0-windows) — refs Core
│   │   ├── TheExtension.cs       # MEF [ExportExtension] entry, composes McpContext
│   │   ├── IMcpExtension.cs      # Internal contract for menu commands
│   │   ├── MenuCommands.cs       # MCP Server menu items
│   │   ├── Adapters/             # dnSpy-backed adapter implementations
│   │   │   ├── DnSpyAssemblyLoader.cs       # Wraps IDsDocumentService
│   │   │   ├── WpfUIThreadScheduler.cs      # Dispatcher.Invoke
│   │   │   ├── DnSpyLogSink.cs              # File + Output Pane
│   │   │   └── DnSpyTreeRefreshNotifier.cs  # TreeView + tab refresh
│   │   ├── Settings/             # dnSpy Options integration
│   │   │   ├── McpSettingsImpl.cs       # MEF-exported subclass (load/save via ISettingsService)
│   │   │   ├── McpSettingsPage.cs       # Options dialog integration
│   │   │   ├── McpSettingsControl.xaml  # Settings UI
│   │   │   └── McpSettingsControl.xaml.cs
│   │   └── Tools/
│   │       └── TreeViewTools.cs  # Extension-only (2 tools: get_selected_node, refresh_ui)
│   │
│   └── dnSpy.MCP.Headless/      # Standalone exe (no WPF, net10.0) — refs Core + MCP SDK
│       ├── Program.cs            # Host + DI + CLI parse + stdio MCP transport
│       ├── CliOptions.cs         # --load / --config / --help args
│       └── Adapters/             # Headless-specific adapter implementations
│           ├── DnlibAssemblyLoader.cs       # ModuleDefMD.Load + registry
│           ├── DnSpyDecompilerLoader.cs     # Reflection load IDecompilerProvider
│           ├── InlineUIThreadScheduler.cs   # No-op (no UI thread)
│           ├── StderrLogSink.cs             # stderr only (MCP stdio rule)
│           ├── NoOpTreeRefreshNotifier.cs   # No-op
│           └── AutoToolRegistration.cs      # Reflection wrap Core tools to MCP SDK
│
├── deps/                         # dnSpy DLL references (Contracts, Logic, dnlib, Decompilers)
├── tests/                        # Test projects (Phase 6 — TBD)
└── scripts/
    ├── build.ps1                 # Build + deploy script
    └── verify-tool-count.ps1     # Tool-count guard (scans Core + Extension tools dirs)
```

## Architecture

### Three-Project Structure (B3' DI-based Hybrid)

- **`dnSpy.MCP.Core`** (lib, net10.0, no WPF): pure analysis library with 5 abstraction interfaces, `McpContext` composition root, 13 instance tool classes, `ToolRegistry` reflection discovery, `McpServerHost` HTTP transport.
- **`dnSpy.MCP`** (Extension, net10.0-windows, WPF): MEF entry, composes `McpContext` with dnSpy-backed adapters, references Core. Hosts the in-dnSpy HTTP MCP server.
- **`dnSpy.MCP.Headless`** (Exe, net10.0, no WPF): standalone stdio MCP server for batch analysis. Uses MCP SDK + dnSpy decompiler DLLs via reflection (`IDecompilerProvider`).

### Why HttpListener Instead of MCP SDK in Extension?

The official MCP SDK 1.2.0 pulls `Microsoft.Extensions.*` 10.x which may conflict with dnSpy's transitive dependencies on .NET 10. Solution: Extension uses custom HTTP transport via `System.Net.HttpListener` (in Core). Headless uses MCP SDK's stdio transport (no conflict because it runs in its own process).

### Extension Lifecycle

```
dnSpy starts
  → MEF discovers dnSpy.MCP.x.dll
  → TheExtension constructor: [Import] gets services
  → OnEvent(ExtensionEvent.AppLoaded):
      - Resolve IDocumentTreeView + IDocumentTabService via IServiceLocator
      - TreeViewTools.Initialize(treeView, tabService)
      - Create Output Pane lazily
  → User clicks Start (or AutoStart=true in Settings):
      - Compose McpContext with 5 dnSpy-backed adapters (DnSpyAssemblyLoader, DnSpyDecompilerSourceProvider via DecompilerService.Decompiler, WpfUIThreadScheduler, DnSpyLogSink, DnSpyTreeRefreshNotifier)
      - Build ToolRegistry(ctx, Core assembly + Extension assembly)
      - McpServerHost(Settings, registry) → HttpListener starts
```

Server starts on **manual click** (not at launch) so Output Pane creation runs on a fully initialized WPF UI thread.

### Tool Discovery

Tools are `public` methods on classes in namespace `dnSpy.MCP.Tools*` with `[Description("...")]` attribute. `ToolRegistry.DiscoverTools()` accepts BOTH:

- **Instance classes** with `ctor(McpContext)` — Core's 13 tool classes
- **Static classes** — Extension-only `TreeViewTools` (provides `get_selected_node`, `refresh_ui`)

Tool names auto-convert to `snake_case` via `ToolRegistry.ToSnakeCase`.

### Service Access (McpContext)

`McpContext` is an **instance class** (typed composition root) holding 5 dependencies + derived `MethodResolver`. Tools receive it via constructor injection:

```csharp
public sealed class DecompilerTools {
    private readonly McpContext _ctx;
    public DecompilerTools(McpContext ctx) => _ctx = ctx;

    public string DecompileMethod(string name) {
        var method = _ctx.Resolver.ResolveMethodFlexible(name);
        return _ctx.SourceDecompiler.DecompileMethod(method);
    }
}
```

The 5 abstraction interfaces (`IAssemblyLoader`, `ISourceDecompiler`, `IUIThreadScheduler`, `ILogSink`, `ITreeRefreshNotifier`) seam host-specific dependencies:

- **Extension adapters** (`src/dnSpy.MCP/Adapters/`): wrap dnSpy contracts
- **Headless adapters** (`src/dnSpy.MCP.Headless/Adapters/`): use dnlib directly + dnSpy decompiler via reflection

The shared `DnSpyDecompilerSourceProvider` (in Core/Adapters) wraps dnSpy's `IDecompiler` for BOTH hosts — output is byte-identical to dnSpy.exe.

### Method Resolution

All method-accepting tools use `MethodResolver.ResolveMethodFlexible(string identifier)` (in `_ctx.Resolver`) which tries in order:

1. Hex token (`0x...`)
2. Plain integer token
3. Full name (`Namespace.Class::Method`)
4. Fallback short name search (returns **first** match)

Do NOT duplicate this logic — call `_ctx.Resolver.ResolveMethodFlexible()`.

### Assembly Scoping

dnSpy can open multiple binaries simultaneously. To avoid ambiguous results:

- **`load_assembly`** — load a DLL/EXE into dnSpy programmatically (no manual UI step).
- **`close_assembly`** — unload an assembly by name.
- **`list_loaded_assemblies`** — always call first to know which binaries are loaded.
- **`assembly` parameter** — search tools (`search_types`, `search_methods`, `search_strings`, `grep`, `search_constants`, `get_xrefs_to`) accept an optional `assembly` parameter to scope results to a specific binary. When omitted, all loaded assemblies are searched.
- **Resolve tools** (`decompile_*`, `get_type_members`, `get_fields`, `get_properties`, `get_attributes`, `get_enum_values`) resolve by name across all assemblies — use `list_loaded_assemblies` first to verify context if multiple binaries are loaded.

### Batch Processing

JSON-RPC batch requests (arrays) are processed **in parallel** — all requests in a batch fire concurrently and results are collected in order. This enables efficient batch analysis pipelines:

```
POST /  [{"method":"tools/call","params":{"name":"load_assembly","arguments":{"path":"D:\\bin\\A.dll"}},...},
         {"method":"tools/call","params":{"name":"load_assembly","arguments":{"path":"D:\\bin\\B.dll"}},...}]
```

### Server Hardening

`McpServerHost` has these protections:

- **Request body limit**: 1MB max (`ContentLength64` check)
- **Concurrency limit**: `SemaphoreSlim(4)` — max 4 simultaneous requests
- **`volatile _running`**: thread-safe flag, set after listener starts
- **Auth fail-closed**: if `RequireAuth=true` but `ApiToken` is empty, the server refuses to start (`InvalidOperationException`). Auth config is snapshotted at `StartAsync` so in-flight settings edits can't race the comparison. Token compared with `CryptographicOperations.FixedTimeEquals` (constant-time, no timing leak).
- **Mutation serialization**: destructive tools (`update_method_body`, `rename_*`) run under an exclusive `_mutationLock` so parallel batch requests can't race on dnlib metadata. Tool mutated-ness is detected by name prefix in `ToolRegistry.IsMutationTool`.
- **Non-blocking shutdown**: `Stop()` fire-and-forgets a short (3s) graceful drain so it never freezes the dnSpy UI thread.
- **Roslyn sandbox**: `BuildRoslynReferences()` loads only 5 core BCL assemblies (not full TPA). The target assembly is added as a `MetadataReference` so patch bodies can call its members — see the trust-boundary comment in `CompilePatch`.
- **Compilation timeout**: 10 seconds max via `Task.Run().WaitAsync()`
- **Tool execution timeout**: configurable via `ToolTimeoutSeconds` (default 30s). On timeout the in-flight work is **cancelled** (not just abandoned): `McpServerHost` opens a `ToolCallScope` (AsyncLocal) that `DnSpyDecompilerSourceProvider` forwards into dnSpy's `DecompilationContext.CancellationToken` — slow obfuscated-method decompiles stop burning CPU once the client gets the timeout error.

### WPF Thread Safety

MCP tools run on **background threads** (HttpListener thread pool). All WPF TreeView/UI access must marshal to the UI thread:

```csharp
// CORRECT
var dispatcher = Application.Current?.Dispatcher;
if (dispatcher?.CheckAccess() == false)
    dispatcher.Invoke(() => { /* WPF access here */ });

// WRONG: direct access from background thread throws InvalidOperationException
```

`TreeViewTools.RunOnUIThread()` provides reusable helpers for Extension-only code. Core tool classes use `_ctx.UI.Invoke(...)` (the `IUIThreadScheduler` abstraction). Metadata mutation tools (rename, patch) auto-refresh tree view internally via `_ctx.TreeRefresh.RefreshAll()`.

### Server Endpoints & Auth

- **Health check**: `GET /health` or `GET /ping` — returns JSON with status, uptime, tools count
- **JSON-RPC**: `POST /` — all MCP tool calls go here
- **Auth**: When `RequireAuth=true`, requests must include `Authorization: Bearer <ApiToken>` header
- **Tool timeout**: Each tool call has a configurable timeout (default 30s via `ToolTimeoutSeconds`)
- **Configurable host/port**: Defaults to `127.0.0.1:5150`, configurable in dnSpy Options

## Tool Invocation Flow

```
AI agent POST http://127.0.0.1:5150/  (JSON-RPC 2.0 batch)
  → McpServerHost.HandleRequest()  (Core's HTTP transport)
    → ToolRegistry.GetTool("tool_name")
      → MethodInfo.Invoke(toolEntry.Instance, args)
        → instance is tool class injected with McpContext (e.g. DecompilerTools)
          → _ctx.AssemblyLoader / Resolver / SourceDecompiler / TreeRefresh
            → DnSpyDecompilerSourceProvider.DecompileMethod(method)
              → delegates to dnSpy.Contracts.Decompiler.IDecompiler (output identical to dnSpy.exe)
```

## Available MCP Tools (38)

### Decompiler

| Tool | Description |
|------|-------------|
| `decompile_method` | C# source of a method (full name, token, or partial name) |
| `decompile_type` | C# source of an entire type |
| `decompile_assembly` | First 10 types of assembly |

### Search

| Tool | Description |
|------|-------------|
| `search_types` | Find types by name pattern (`regex:` prefix for regex) |
| `search_methods` | Find methods by name, scoped to type |
| `search_strings` | Find string literals in IL |
| `grep` | Multi-scope search across types/methods/strings |

### Analysis

| Tool | Description |
|------|-------------|
| `get_method_il` | Raw IL instructions with stack/exception info |
| `get_il_opcodes_formatted` | Formatted IL opcodes with offsets (`IlDisplayTools`) |
| `get_method_signatures` | Method metadata: params, return, flags, generics |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL bytes with MaxStack/InitLocals info |
| `update_method_body` | Patch method IL using C# statements (dry-run by default, optional `assemblyName` scope, `IlPatchTools`) |

### Cross-References

| Tool | Description |
|------|-------------|
| `get_xrefs_to` | Find all references to a method or field |
| `get_callees` | Methods/fields called by a method |

### Assembly

| Tool | Description |
|------|-------------|
| `load_assembly` | Load a DLL/EXE into dnSpy by absolute path |
| `close_assembly` | Unload an assembly by name |
| `list_loaded_assemblies` | List all binaries loaded in dnSpy |
| `assembly_overview` | Module/assembly summary, type counts |
| `assembly_list_namespaces` | All namespaces in loaded assembly |
| `assembly_list_types` | All types (optional regex filter) |
| `assembly_get_references` | Assembly references (DLLs/NuGets) |

### Resources & Metadata

| Tool | Description |
|------|-------------|
| `get_resources` | Embedded resources list |
| `get_resource_data` | Raw bytes of a named resource |
| `get_metadata` | PE headers, MVID, runtime version |
| `get_global_namespaces` | Types in the global namespace |

### Type Inspection

| Tool | Description |
|------|-------------|
| `get_type_members` | List all members of a type with optional filter |
| `get_fields` | Detailed field info: type, access, static/const, values |
| `get_properties` | Property details: getter/setter, type, access |

### Custom Attributes

| Tool | Description |
|------|-------------|
| `get_attributes` | Attributes on assembly/type/method/field with filter |
| `get_method_attributes` | Shortcut: attributes on a specific method |

### Constants & Enums

| Tool | Description |
|------|-------------|
| `get_enum_values` | Enum members with name + value (hex + decimal) |
| `search_constants` | Search const/literal fields across assemblies |

### UI & Rename

| Tool | Description |
|------|-------------|
| `get_selected_node` | Currently selected node in TreeView |
| `refresh_ui` | Refresh TreeView after metadata changes |
| `rename_namespace` | Rename namespace across matching types (dry-run by default) |
| `rename_class` | Rename one class (dry-run by default) |
| `rename_method` | Rename methods by exact/partial match (dry-run by default) |

## API Conventions & Quirks

### Decompiler API

```csharp
// CORRECT (3 params)
var output = new TextDecompilerOutput();
decompiler.Decompile(method, output, new DecompilationContext());
return output.ToString();  // NOT: output.Text

// WRONG: decompiler.Decompile(method, output)  // missing DecompilationContext
// WRONG: output.Text                              // property doesn't exist
```

### System.Text.Json 8.x Limitations

`JsonArray` does NOT implement LINQ — use for-loop iteration:

```csharp
var list = new List<JsonNode?>();
for (int i = 0; i < jsonArray.Count; i++)
    list.Add(jsonArray[i]);
```

### JSON-RPC Response

`HandleToolCall` returns the **full JSON-RPC response object**. Do NOT wrap again with `CreateResponse()`:

```csharp
// CORRECT
var callResult = HandleToolCall(req);
results.Add(isNotification ? null : callResult);

// WRONG: results.Add(CreateResponse(id, callResult)); // double-wraps!
```

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**IMPORTANT: This project has a knowledge graph. ALWAYS use the
code-review-graph MCP tools BEFORE using Grep/Glob/Read to explore
the codebase.** The graph is faster, cheaper (fewer tokens), and gives
you structural context (callers, dependents, test coverage) that file
scanning cannot.

### When to use graph tools FIRST

- **Exploring code**: `semantic_search_nodes` or `query_graph` instead of Grep
- **Understanding impact**: `get_impact_radius` instead of manually tracing imports
- **Code review**: `detect_changes` + `get_review_context` instead of reading entire files
- **Finding relationships**: `query_graph` with callers_of/callees_of/imports_of/tests_for
- **Architecture questions**: `get_architecture_overview` + `list_communities`

Fall back to Grep/Glob/Read **only** when the graph doesn't cover what you need.

### Key Tools

| Tool | Use when |
|------|----------|
| `detect_changes` | Reviewing code changes — gives risk-scored analysis |
| `get_review_context` | Need source snippets for review — token-efficient |
| `get_impact_radius` | Understanding blast radius of a change |
| `get_affected_flows` | Finding which execution paths are impacted |
| `query_graph` | Tracing callers, callees, imports, tests, dependencies |
| `semantic_search_nodes` | Finding functions/classes by name or keyword |
| `get_architecture_overview` | Understanding high-level codebase structure |
| `refactor_tool` | Planning renames, finding dead code |

### Workflow

1. The graph auto-updates on file changes (via hooks).
2. Use `detect_changes` for code review.
3. Use `get_affected_flows` to understand impact.
4. Use `query_graph` pattern="tests_for" to check coverage.
