# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

### Setup (one-time)
```powershell
mkdir deps
# Copy these DLLs from a dnSpy installation's bin/ folder:
#   dnSpy.Contracts.DnSpy.dll
#   dnSpy.Contracts.Logic.dll
#   ICSharpCode.Decompiler.dll
#   dnlib.dll
```

### Local Development
```powershell
# Build only (Release default)
dotnet build src/dnSpy.MCP/dnSpy.MCP.csproj -c Release

# Deploy extension (requires dnSpy closed)
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy
```

Options: `-Clean`, `-Deploy`, `-DeployDir <path>`, `-Configuration <Debug|Release>`

### CI
GitHub Actions (`build.yml`) auto-downloads dnSpy deps and runs `dotnet build -c Release`. No manual setup needed.

### Build output
- **Release DLL**: `src/dnSpy.MCP/bin/Release/net8.0-windows/dnSpy.MCP.x.dll`
- **Deploy to dnSpy**: copy `dnSpy.MCP.x.dll`, `.deps.json`, `.pdb` (optional) to `<dnSpy>/bin/Extensions/`

## Project Layout

```
dnspy_mcp/
├── src/dnSpy.MCP/           # Standalone extension project
│   ├── TheExtension.cs       # MEF [ExportExtension] entry point
│   ├── DnSpyContext.cs       # Static service bridge + lazy IServiceLocator resolution
│   ├── MenuCommands.cs        # MCP Server menu items
│   ├── Mcp/
│   │   ├── McpServerHost.cs  # HttpListener + JSON-RPC 2.0 dispatch
│   │   ├── ToolRegistry.cs   # Reflection-based tool discovery
│   │   ├── McpLogger.cs     # File + Output Window logging
│   │   └── McpServerOptions.cs
│   ├── Tools/                # 10 tool classes, 28 tools
│   └── Helpers/
│       ├── MethodResolver.cs
│       └── TextDecompilerOutput.cs
├── deps/                     # dnSpy DLL references
└── scripts/build.ps1
```

## Architecture

### Why HttpListener Instead of MCP SDK?
The official MCP SDK 1.2.0 pulls `Microsoft.Extensions.*` 10.x but dnSpy uses .NET 8.0 with `Microsoft.Extensions.*` 8.x — a hard version conflict. Solution: custom HTTP transport via `System.Net.HttpListener`.

### Extension Lifecycle
```
dnSpy starts
  → MEF discovers dnSpy.MCP.x.dll
  → TheExtension constructor: [Import] gets services
  → OnEvent(ExtensionEvent.AppLoaded): DnSpyContext.Initialize(...) + EnsureOutputPane()
  → User clicks Start → HttpListener starts on :5150
```

Server starts on **manual click** (not at launch) so `EnsureOutputPane()` runs on a fully initialized WPF UI thread.

### Tool Discovery
Tools are `public static` methods in `dnSpy.MCP.Tools.*` with `[Description("...")]`. `ToolRegistry.DiscoverTools()` scans via reflection. Tool names auto-convert to `snake_case`.

### Service Access
`DnSpyContext.cs` is a static singleton bridging MEF to MCP tools. Three access patterns:

**Direct services** (always available after init):
```csharp
DnSpyContext.DocumentService
DnSpyContext.DecompilerService
```

**Cached resolver** (shared `MethodResolver`, lazy-initialized):
```csharp
DnSpyContext.Resolver  // returns MethodResolver backed by DocumentService
```
Tools should use `DnSpyContext.Resolver` instead of creating `new MethodResolver()`.

**Lazy services** (resolved via `IServiceLocator.TryResolve<T>()` on first access):
```csharp
DnSpyContext.TabService     // IDocumentTabService
DnSpyContext.TreeView       // IDocumentTreeView
```

`IServiceLocator` is imported via MEF and passed to `DnSpyContext.Initialize()` at `AppLoaded`. The lazy pattern avoids MEF import order issues with these services.

### Method Resolution
All method-accepting tools use `MethodResolver.ResolveMethodFlexible(string identifier)` which tries in order:
1. Hex token (`0x...`)
2. Plain integer token
3. Full name (`Namespace.Class::Method`)
4. Fallback short name search (returns **first** match)

Do NOT duplicate this logic — call `DnSpyContext.Resolver.ResolveMethodFlexible()`.

### Server Hardening
`McpServerHost` has these protections:
- **Request body limit**: 1MB max (`ContentLength64` check)
- **Concurrency limit**: `SemaphoreSlim(4)` — max 4 simultaneous requests
- **`volatile _running`**: thread-safe flag, set after listener starts
- **Roslyn sandbox**: `BuildRoslynReferences()` loads only 5 core BCL assemblies (not full TPA)
- **Compilation timeout**: 10 seconds max via `Task.Run().WaitAsync()`

### WPF Thread Safety
MCP tools run on **background threads** (HttpListener thread pool). All WPF TreeView/UI access must marshal to the UI thread:
```csharp
// CORRECT
var dispatcher = Application.Current?.Dispatcher;
if (dispatcher?.CheckAccess() == false)
    dispatcher.Invoke(() => { /* WPF access here */ });

// WRONG: direct access from background thread throws InvalidOperationException
```

`TreeViewTools.RunOnUIThread()` provides reusable helpers. Metadata mutation tools (rename, patch) auto-refresh tree view internally.

## Tool Invocation Flow

```
AI agent POST http://127.0.0.1:5150/  (JSON-RPC 2.0 batch)
  → McpServerHost.HandleRequest()
    → ToolRegistry.GetTool("tool_name")
      → MethodInfo.Invoke(null, args)
        → accesses DnSpyContext.DocumentService / DecompilerService
          → decompilerService.Decompiler.Decompile(method, output, new DecompilationContext())
```

## Available MCP Tools (28)

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
| `get_il_opcodes_formatted` | Formatted IL opcodes with offsets |
| `get_method_signatures` | Method metadata: params, return, flags, generics |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL bytes with MaxStack/InitLocals info |
| `update_method_body` | Patch method IL using C# statements (dry-run by default) |

### Cross-References
| Tool | Description |
|------|-------------|
| `get_xrefs_to` | Find all references to a method or field |
| `get_callees` | Methods/fields called by a method |

### Assembly
| Tool | Description |
|------|-------------|
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

### UI & Rename
| Tool | Description |
|------|-------------|
| `get_selected_node` | Currently selected node in TreeView |
| `refresh_u_i` | Refresh TreeView after metadata changes |
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
