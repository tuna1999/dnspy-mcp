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
│   ├── Tools/                # 10 tool classes, 29 tools
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
`DnSpyContext.cs` is a static singleton bridging MEF to MCP tools. Two service-access strategies:

**Direct services** (always available after init):
```csharp
DnSpyContext.DocumentService
DnSpyContext.DecompilerService
```

**Lazy services** (resolved via `IServiceLocator.TryResolve<T>()` on first access):
```csharp
DnSpyContext.TabService     // IDocumentTabService
DnSpyContext.TreeView       // IDocumentTreeView
```

`IServiceLocator` is imported via MEF and passed to `DnSpyContext.Initialize()` at `AppLoaded`. The lazy pattern avoids MEF import order issues with these services.

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

## Available MCP Tools (29)

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
| `get_callers` | Alias for `get_xrefs_to` |

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
