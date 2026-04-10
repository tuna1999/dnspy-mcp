# dnSpy MCP Server

MCP (Model Context Protocol) server extension for [dnSpy](https://github.com/dnSpyEx/dnSpy), enabling AI agents to decompile and analyze .NET assemblies directly through dnSpy.

## How It Works

```
dnSpy loads extension → MCP Server menu → Start → HttpListener on :5150
                                                        ↕
                                              AI agent (Claude, etc.)
                                              HTTP POST (JSON-RPC 2.0)
```

The server runs as a dnSpy extension using `System.Net.HttpListener` — no ASP.NET Core or external dependencies required. AI agents connect via standard MCP protocol over HTTP.

## Tools (25)

### Decompiler
| Tool | Description |
|------|-------------|
| `decompile_method` | Decompile a method to C#. Accepts full name (`Namespace.Class::Method`), metadata token (`0x06000001`), or partial name |
| `decompile_type` | Decompile an entire type (all members) to C# |
| `decompile_assembly` | Decompile all types in the assembly (limited to 10 for brevity) |

### Search
| Tool | Description |
|------|-------------|
| `search_types` | Search types by name pattern. Use `regex:` prefix for regex matching |
| `search_methods` | Search methods by name, optionally scoped to a specific type |
| `search_strings` | Search string literals in method bodies |
| `grep` | Multi-scope search across types, methods, and strings |

### Analysis
| Tool | Description |
|------|-------------|
| `get_method_il` | Raw IL instructions with exception handlers |
| `get_method_signatures` | Method metadata: parameters, return type, flags, generic params |
| `get_type_hierarchy` | Inheritance chain, interfaces, member counts |
| `get_method_body` | IL bytes with MaxStack/InitLocals info |
| `get_il_opcodes_formatted` | Formatted IL opcodes with offsets and line indices |
| `update_method_body` | Patch a method body using C# statements (dry-run supported) |

### UI & Navigation
| Tool | Description |
|------|-------------|
| `get_selected_node` | Get the currently selected node in dnSpy tree view |
| `refresh_u_i` | Refresh tree view UI after metadata changes |

### Rename
| Tool | Description |
|------|-------------|
| `rename_namespace` | Rename a namespace across matching types (dry-run supported) |
| `rename_class` | Rename one class in an assembly+namespace (dry-run supported) |
| `rename_method` | Rename methods by exact or partial match (dry-run supported) |

### Namespace
| Tool | Description |
|------|-------------|
| `get_global_namespaces` | List all types in the global namespace |

### Cross-References
| Tool | Description |
|------|-------------|
| `get_xrefs_to` | Find all references to a method or field |
| `get_callees` | Methods and fields called by a method |
| `get_callers` | Alias for `get_xrefs_to` |

### Assembly
| Tool | Description |
|------|-------------|
| `assembly_overview` | Module info, version, entry point, type count, references |
| `assembly_list_namespaces` | All namespaces in the loaded assembly |
| `assembly_list_types` | Type listing with optional regex filter |
| `assembly_get_references` | Assembly references (DLLs, NuGet packages) |

### Resources & Metadata
| Tool | Description |
|------|-------------|
| `get_resources` | List embedded resources |
| `get_resource_data` | Raw bytes of a specific resource |
| `get_metadata` | PE headers, MVID, runtime version, sections |

## Quick Start

### Prerequisites

- [dnSpy](https://github.com/dnSpyEx/dnSpy/releases) (.NET 8.0 build)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- `deps/` folder with these DLLs copied from dnSpy:
  - `dnSpy.Contracts.DnSpy.dll`
  - `dnSpy.Contracts.Logic.dll`
  - `ICSharpCode.Decompiler.dll`
  - `dnlib.dll`

### Configure DnSpyBin path

The project resolves dnSpy contract DLLs via the `<DnSpyBin>` MSBuild property in [`dnSpy.MCP.csproj`](src/dnSpy.MCP/dnSpy.MCP.csproj):

```xml
<DnSpyBin>..\..\deps</DnSpyBin>
```

Default path resolves to `<repo>/deps/`. To change it, edit this property or pass it at the command line:

```powershell
dotnet build -p:DnSpyBin="D:\path\to\dnSpy\bin"
```

### Build & Deploy

```powershell
# 1) Build only (Release mặc định)
pwsh scripts/build.ps1

# 2) Clean + build
pwsh scripts/build.ps1 -Clean

# 3) Build + deploy to staging (build/Extensions/)
pwsh scripts/build.ps1 -Deploy

# 4) Build + deploy directly to dnSpy runtime
pwsh scripts/build.ps1 -Deploy -DeployDir "D:\tools\dnSpy\Extensions"

# 5) Override sang Debug khi cần debug
pwsh scripts/build.ps1 -Configuration Debug -Deploy -DeployDir "D:\tools\dnSpy\Extensions"
```

This builds the extension DLL and deploys it. **dnSpy must be closed** before running.

Options:
```powershell
pwsh scripts/build.ps1 -Clean                        # Clean before build
pwsh scripts/build.ps1 -Deploy                       # Deploy after build
pwsh scripts/build.ps1 -DeployDir "<path>"          # Custom deploy target (used with -Deploy)
pwsh scripts/build.ps1 -Configuration Debug          # Build Debug instead of Release
pwsh scripts/build.ps1 -Configuration Release        # Build Release (default)
```

### Build output paths

- Build output (Release default): `src/dnSpy.MCP/bin/Release/net8.0-windows/dnSpy.MCP.x.dll`
- Build output (Debug override): `src/dnSpy.MCP/bin/Debug/net8.0-windows/dnSpy.MCP.x.dll`
- Staging deploy: `build/Extensions/dnSpy.MCP.x.dll`
- Runtime deploy: `<dnSpy-folder>/Extensions/dnSpy.MCP.x.dll`

Only these files should be copied to dnSpy's `Extensions` folder:
- `dnSpy.MCP.x.dll`
- `dnSpy.MCP.x.deps.json`
- `dnSpy.MCP.x.pdb` (optional for debugging)

Do not copy the whole `build/Extensions` folder recursively into dnSpy (avoid nested `Extensions/Extensions/` and stale dependency files).

### Usage

1. Start `dnSpy.exe`
2. Open a .NET assembly (.exe/.dll)
3. Menu → **MCP Server** → **Start**
4. Open **View → Output** (Alt+2) → select **MCP Server** to see logs
5. Connect from an AI agent via `http://127.0.0.1:5150/`

### Menu Options

| Menu Item | Action |
|-----------|--------|
| **Start** | Start the MCP HTTP server |
| **Status** | Show running/stopped state and port |
| **Show Log** | Display recent log entries |
| **Clear Log** | Clear log file and output window |

## Project Structure

```
dnspy_mcp/
├── src/
│   └── dnSpy.MCP/             # Standalone extension project (recommended)
│       ├── Mcp/
│       │   ├── McpServerHost.cs     # HTTP transport + JSON-RPC 2.0 dispatch
│       │   ├── ToolRegistry.cs      # Reflection-based tool discovery
│       │   ├── McpLogger.cs         # Logging: file + Output Window
│       │   └── McpServerOptions.cs  # Port/host configuration
│       ├── Tools/                   # 6 tool classes, 21 tools total
│       ├── Helpers/
│       │   ├── MethodResolver.cs    # Resolve methods/types by name/token
│       │   └── TextDecompilerOutput.cs
│       ├── TheExtension.cs          # MEF entry point
│       ├── DnSpyContext.cs          # Static service bridge
│       └── MenuCommands.cs          # dnSpy menu items
├── deps/                        # dnSpy contract DLLs (for standalone build)
├── build/Extensions/           # Deployed extension DLLs
└── scripts/
    └── build.ps1           # Build & deploy script
```

## Adding New Tools

Tools are discovered at runtime via reflection. To add a new tool:

1. Create a `public static` class in `src/dnSpy.MCP/Tools/` under the `dnSpy.MCP.Tools` namespace
2. Add `public static` methods with a `[Description("...")]` attribute
3. Parameters use `[Description("...")]` for documentation

```csharp
using System.ComponentModel;

namespace dnSpy.MCP.Tools {
    public static class MyTools {
        [Description("Describe what this tool does")]
        public static string MyTool(
            [Description("Parameter description")] string param1) {
            // Access dnSpy services via DnSpyContext
            var module = DnSpyContext.DocumentService?
                .GetAssemblies().FirstOrDefault()?.ModuleDef;
            return $"Result: {param1}";
        }
    }
}
```

Method names are automatically converted to `snake_case` for the MCP protocol (e.g., `MyTool` → `my_tool`).

## Configuration

Default configuration in `McpServerOptions.cs`:
- **Host**: `127.0.0.1`
- **Port**: `5150`

## Logging

Logs are written to three destinations:
- **File**: `build/Extensions/mcp-server.log`
- **In-memory**: Viewable via MCP Server → Show Log
- **Output Window**: View → Output → MCP Server (in dnSpy)

## Architecture Notes

### Why HttpListener instead of MCP SDK?

The official MCP SDK (`ModelContextProtocol` 1.2.0) pulls `Microsoft.Extensions.*` 10.x dependencies, but dnSpy runs on .NET 8.0 with `Microsoft.Extensions.*` 8.x. This is a hard version conflict that cannot be resolved with binding redirects. The solution is a custom HTTP transport using `System.Net.HttpListener`.

### Standalone Build

The project references pre-built DLLs from `deps/`, enabling fast iteration without cloning the full dnSpy source. For integrated builds as part of dnSpy.sln, clone [dnSpyEx](https://github.com/dnSpyEx/dnSpy) and copy `src/dnSpy.MCP/` into `Extensions/`.

## Connecting AI Agents

This MCP server exposes dnSpy's decompilation and analysis tools via the standard MCP protocol over HTTP at `http://127.0.0.1:5150/`. Most modern AI agents support HTTP MCP servers natively — no bridge package needed.

### Claude Code (recommended)

Use the `claude mcp add` command to add the server. Choose a scope:

```bash
# Local scope (default) — only this project, stored in ~/.claude.json
claude mcp add --transport http dnspy http://127.0.0.1:5150

# Project scope — shared with team via .mcp.json (check into git)
claude mcp add --transport http dnspy --scope project http://127.0.0.1:5150

# User scope — all your projects
claude mcp add --transport http dnspy --scope user http://127.0.0.1:5150
```

**Project scope** generates a `.mcp.json` at the project root:

```json
{
  "mcpServers": {
    "dnspy": {
      "type": "http",
      "url": "http://127.0.0.1:5150"
    }
  }
}
```

**Local/User scope** writes to `~/.claude.json` under the project path:

```json
{
  "projects": {
    "/path/to/your/project": {
      "mcpServers": {
        "dnspy": {
          "type": "http",
          "url": "http://127.0.0.1:5150"
        }
      }
    }
  }
}
```

Other useful commands:
```bash
claude mcp list          # list all configured servers
claude mcp get dnspy     # show config for a server
claude mcp remove dnspy  # remove a server
```

### Other AI Editors

| Editor | Config file | Format |
|--------|------------|--------|
| **Cursor** | `~/.cursor/mcp.json` | `{ "mcpServers": { "dnspy": { "url": "http://127.0.0.1:5150/" } } }` |
| **VS Code** (Cline/Roo) | `.vscode/mcp.json` | Same as above |

### Verification

1. Start dnSpy and open an assembly
2. Menu → **MCP Server** → **Start**
3. In your AI agent, verify the connection:

```
You should see 25 MCP tools available:
- decompile_method
- decompile_type
- search_types
- grep
- get_xrefs_to
- assembly_overview
- ...and more
```

If the agent does not auto-discover the tools, tell it: "Use the dnSpy MCP server at `http://127.0.0.1:5150/` to access decompilation and analysis tools."

## License

This project is licensed under [GPLv3](https://www.gnu.org/licenses/gpl-3.0.en.html), consistent with dnSpy's license.
