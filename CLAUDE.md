# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

### Setup (one-time)
```powershell
mkdir deps
# Copy dnSpy.Contracts.DnSpy.dll, dnSpy.Contracts.Logic.dll,
# ICSharpCode.Decompiler.dll, dnlib.dll from dnSpy bin to deps
# Or: cp from a dnSpy installation's bin folder
```

### MCP Extension (standalone, recommended for development)
```powershell
# Build only (default)
pwsh scripts/build.ps1

# Build + deploy to staging (build/Extensions/)
pwsh scripts/build.ps1 -Deploy

# Build + deploy directly to dnSpy
pwsh scripts/build.ps1 -Deploy -DeployDir "D:\tools\dnSpy\Extensions"
```
Builds the extension by default. Deploy runs only when `-Deploy` is specified. dnSpy must be closed first.

Options: `-Clean`, `-Deploy`, `-DeployDir <path>`, `-Configuration <Debug|Release>` (default: `Release`)

### Build output
- **Build output (Release default)**: `src/dnSpy.MCP/bin/Release/net8.0-windows/dnSpy.MCP.x.dll`
- **Build output (Debug override)**: `src/dnSpy.MCP/bin/Debug/net8.0-windows/dnSpy.MCP.x.dll`
- **Deploy staging**: `build/Extensions/dnSpy.MCP.x.dll`
- **Runtime deploy**: `<dnSpy-folder>/Extensions/dnSpy.MCP.x.dll`

Only 3 files need to be in dnSpy's Extensions folder: `dnSpy.MCP.x.dll`, `.pdb` (optional), `.deps.json`. dnSpy provides all other dependencies (contracts, NuGet packages) at runtime.

## Project Layout

```
dnspy_mcp/
├── src/
│   └── dnSpy.MCP/            # Standalone project (fast dev iteration)
│       ├── TheExtension.cs   # MEF entry point
│       ├── DnSpyContext.cs  # Static service bridge
│       ├── MenuCommands.cs   # MCP Server menu
│       ├── Mcp/              # HTTP transport + tool discovery
│       ├── Tools/             # 6 tool classes → 21 tools
│       └── Helpers/         # MethodResolver, TextDecompilerOutput
├── deps/                    # DLL references for standalone build
├── build/Extensions/        # Deployed extension output
└── scripts/
    └── build.ps1            # Build & deploy script
```

**When to use which layout:**
- **Standalone** (`src/dnSpy.MCP/`): Faster iteration — rebuild and restart dnSpy. Requires pre-copying DLLs to `deps/`.
- **Integrated**: Clone [dnSpyEx](https://github.com/dnSpyEx/dnSpy) separately and copy `src/dnSpy.MCP/` into `Extensions/` to build as part of dnSpy.sln.

## Architecture

### MCP Server as dnSpy Extension

The MCP server is a dnSpy extension exposing decompilation/analysis tools via the Model Context Protocol over HTTP.

```
dnSpy starts → loads extension via MEF → user clicks Start → HttpListener starts on :5150
                                                          → AI agent connects via HTTP POST
```

**Why HttpListener instead of ASP.NET Core / MCP SDK:** The official MCP SDK 1.2.0 pulls `Microsoft.Extensions.*` 10.x dependencies, but dnSpy runs on .NET 8.0 with `Microsoft.Extensions.*` 8.x — a hard version conflict that cannot be resolved with binding redirects. Solution: custom HTTP transport via `System.Net.HttpListener`.

### Standalone Build

The project references pre-built DLLs from `deps/`, enabling fast iteration without cloning the full dnSpy source. For integrated builds as part of dnSpy.sln, clone [dnSpyEx](https://github.com/dnSpyEx/dnSpy) and copy `src/dnSpy.MCP/` into `Extensions/`.

### Tool Discovery (Reflection-Based)

Tools are discovered at runtime via reflection — no SDK registration needed. To add a new tool:

1. Create a `public static` class in `src/dnSpy.MCP/Tools/` under the `dnSpy.MCP.Tools` namespace
2. Add `public static` methods with a `[Description("...")]` attribute
3. Parameters use `[Description("...")]` for tool parameter docs
4. `ToolRegistry.DiscoverTools()` scans `dnSpy.MCP.Tools.*` for static classes automatically

Tool names use `snake_case` (auto-converted from `PascalCase` method names by `ToolRegistry.ToSnakeCase()`).

### MCP Protocol Implementation

- `src/dnSpy.MCP/Mcp/McpServerHost.cs` — HTTP listener, JSON-RPC 2.0 handler (supports batch requests)
- `src/dnSpy.MCP/Mcp/ToolRegistry.cs` — reflection-based tool discovery, snake_case name conversion
- `src/dnSpy.MCP/Mcp/McpLogger.cs` — in-memory + file logging (`build/Extensions/mcp-server.log`) + dnSpy Output Window
- `src/dnSpy.MCP/Mcp/McpServerOptions.cs` — port configuration (default: 5150)
- Handles: `initialize`, `tools/list`, `tools/call`, `notifications/initialized`, `shutdown`

### Service Access

`DnSpyContext.cs` is a static singleton bridging MEF services to MCP tools. All tools access `IDsDocumentService` and `IDecompilerService` via:
```csharp
var module = DnSpyContext.DocumentService?.GetAssemblies().FirstOrDefault()?.ModuleDef;
```

### MEF Extension Lifecycle

Extension loading order:
1. dnSpy starts → discovers `dnSpy.MCP.x.dll` via MEF
2. `TheExtension` constructor runs — `[Import]` attributes request services
3. `OnEvent(ExtensionEvent.AppLoaded, ...)` fires → `DnSpyContext.Initialize(...)` + create output pane
4. Server starts only on **manual Start click** (not at app launch) to avoid conflicts

**Why lazy start:** `EnsureOutputPane()` must be called from a WPF UI thread with STA. At `AppLoaded`, dnSpy's UI is fully initialized, so it is safe to create the output pane at that point.

### MCP Menu (MenuCommands.cs)

Menu added to dnSpy app menu (`_MCP Server`):
- **Start** — launches HTTP server (must open an assembly first)
- **Status** — shows running/stopped state and port
- **Show Log** — displays recent log entries from memory
- **Clear Log** — clears log file and output pane

## Tool Invocation Flow

```
AI agent POST http://127.0.0.1:5150/  (JSON-RPC 2.0 batch supported)
  → McpServerHost.HandleRequest()
    → ToolRegistry.GetTool("tool_name")
      → MethodInfo.Invoke(null, args)  ← static tools, no DI
        → accesses DnSpyContext.DocumentService / DecompilerService
          → MethodResolver queries IDsDocumentService.GetDocuments()
          → decompilerService.Decompiler.Decompile(method, output, context)
```

## Available MCP Tools (21 total)

| Tool | Description |
|------|-------------|
| `decompile_method` | C# source of a method (full name, token, or partial name) |
| `decompile_type` | C# source of an entire type |
| `decompile_assembly` | First 10 types of assembly (limited for brevity) |
| `search_types` | Find types by name pattern (regex with `regex:` prefix) |
| `search_methods` | Find methods by name pattern |
| `search_strings` | Find string literals in IL |
| `grep` | Multi-scope search across types/methods/strings |
| `get_xrefs_to` | Find all callers/references to a member |
| `get_callees` | List methods/fields called by a method |
| `get_callers` | Alias for `get_xrefs_to` |
| `get_method_il` | Raw IL instructions with stack/exception info |
| `get_method_signatures` | Method metadata: params, return, flags, generics |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL instruction dump |
| `assembly_overview` | Module/assembly summary, type counts |
| `assembly_list_namespaces` | All namespaces in loaded assembly |
| `assembly_list_types` | All types (optional regex filter) |
| `assembly_get_references` | Assembly references (DLLs/NuGets) |
| `get_resources` | Embedded resources list |
| `get_resource_data` | Raw bytes of a named resource |
| `get_metadata` | PE headers, MVID, runtime version |

## Important Files

| File | Purpose |
|------|---------|
| `src/dnSpy.MCP/Mcp/McpServerHost.cs` | HTTP transport + JSON-RPC dispatch |
| `src/dnSpy.MCP/Mcp/ToolRegistry.cs` | Reflection-based tool discovery |
| `src/dnSpy.MCP/Mcp/McpLogger.cs` | In-memory + file logging + Output Window |
| `src/dnSpy.MCP/Mcp/McpServerOptions.cs` | Port configuration |
| `src/dnSpy.MCP/TheExtension.cs` | MEF `[ExportExtension]` entry point |
| `src/dnSpy.MCP/DnSpyContext.cs` | Static service bridge |
| `src/dnSpy.MCP/MenuCommands.cs` | Menu items for Start/Status/Log |
| `src/dnSpy.MCP/Helpers/MethodResolver.cs` | Resolve methods/types by name/token |
| `src/dnSpy.MCP/Helpers/TextDecompilerOutput.cs` | `IDecompilerOutput` implementation |
| `src/dnSpy.MCP/Tools/*.cs` | 6 tool classes, 21 tools total |

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

### JSON-RPC Response (Critical)
`HandleToolCall` returns the **full JSON-RPC response object**. Do NOT wrap it again with `CreateResponse()` — that causes double-nested `result` keys:
```csharp
// CORRECT
var callResult = HandleToolCall(req);
results.Add(isNotification ? null : callResult);

// WRONG: results.Add(CreateResponse(id, callResult)); // double-wraps!
```