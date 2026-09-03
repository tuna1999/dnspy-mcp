# dnSpy MCP Server

MCP (Model Context Protocol) server extension for [dnSpy](https://github.com/dnSpyEx/dnSpy), enabling AI agents to decompile and analyze .NET assemblies directly through dnSpy.

## How It Works

Two hosts share one tool core (`dnSpy.MCP.Core`):

```
┌─ Extension (in dnSpy) ─────────────────┐   ┌─ Headless (standalone exe) ─────────┐
│ dnSpy loads extension → MCP Server     │   │ dotnet dnspy-mcp-headless.dll       │
│ menu → Start → HttpListener on :5150   │   │ --load foo.dll (repeatable, globs)  │
│              ↕                         │   │              ↕                      │
│   AI agent — HTTP POST (JSON-RPC 2.0)  │   │   AI agent — stdio (MCP SDK)        │
└────────────┬───────────────────────────┘   └──────────────┬──────────────────────┘
             └──────────► dnSpy.MCP.Core ◄──────────────────┘
                         (36 tools + decompiler bridge,
                          output identical to dnSpy.exe)
```

The extension runs inside dnSpy using `System.Net.HttpListener` (no ASP.NET Core
conflicts). The headless binary is a standalone stdio MCP server for batch
analysis — same tools, same decompiled output, no UI, no dnSpy install needed
at analysis time (only the vendored decompiler DLLs in `deps/`).

## Tools (38 total · 36 in headless)

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

### Assembly
| Tool | Description |
|------|-------------|
| `load_assembly` | Load a .NET DLL/EXE by absolute path (headless/extension) |
| `close_assembly` | Unload assemblies by simple name (case-insensitive) |
| `list_loaded_assemblies` | All loaded binaries: filename, assembly name, MVID, type count, path |
| `assembly_overview` | Module info, version, entry point, type count, references |
| `assembly_list_namespaces` | All namespaces in the loaded assembly |
| `assembly_list_types` | Type listing with optional regex filter |
| `assembly_get_references` | Assembly references (DLLs, NuGet packages) |

*`get_selected_node` and `refresh_u_i` (UI & Navigation below) exist only in the dnSpy extension — headless serves the other 36.*

### Resources & Metadata
| Tool | Description |
|------|-------------|
| `get_resources` | List embedded resources |
| `get_resource_data` | Raw bytes of a specific resource |
| `get_metadata` | PE headers, MVID, runtime version, sections |

### Prerequisites

- [dnSpy](https://github.com/dnSpyEx/dnSpy/releases) (.NET 10 build, v6.6.0+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `deps/` folder with these DLLs synced from a dnSpy install
  (run `pwsh scripts/sync-deps.ps1 -DnSpyBin <dnSpy>\bin`):
  - `dnSpy.Contracts.DnSpy.dll`, `dnSpy.Contracts.Logic.dll`
  - `ICSharpCode.Decompiler.dll`, `dnlib.dll`
  - Headless-only: `dnSpy.Decompiler.dll`, `dnSpy.Decompiler.ILSpy.Core.dll`,
    `ICSharpCode.NRefactory.dll`, `ICSharpCode.NRefactory.CSharp.dll`

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
# 1) Build the whole solution (Core + Extension + Headless + Tests)
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy"

# 2) Clean + build
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Clean

# 3) Build + deploy extension to dnSpy
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy

# 4) Build + publish headless binary to publish/headless/
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -PublishHeadless

# 5) Override sang Debug khi cần debug
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -Configuration Debug -Deploy
```

The script syncs `deps/` from the dnSpy install, builds the solution, and
deploys the extension. **dnSpy must be closed** before `-Deploy`.

Options:
```powershell
-DnSpyPath "<path>"    # dnSpy install folder (required; used for deps sync + deploy)
-Clean                 # Clean before build
-Deploy                # Deploy extension after build (default target: <dnSpy>\bin\Extensions)
-DeployDir "<path>"    # Custom deploy target (used with -Deploy)
-PublishHeadless       # Also publish headless to publish/headless/
-Configuration Debug   # Build Debug instead of Release
```

### Build output paths

- Extension (Release): `src/dnSpy.MCP/bin/Release/net10.0-windows/dnSpy.MCP.x.dll`
- Extension (Debug): `src/dnSpy.MCP/bin/Debug/net10.0-windows/dnSpy.MCP.x.dll`
- Headless: `src/dnSpy.MCP.Headless/bin/Release/net10.0-windows/dnspy-mcp-headless.dll`
  (published bundle: `publish/headless/`)
- Runtime deploy: `<dnSpy>\bin\Extensions\dnSpy.MCP.x.dll`

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
```
dnspy_mcp/
├── src/
│   ├── dnSpy.MCP.Core/        # Shared library: 36 tools, McpContext, decompiler bridge
│   │   ├── Abstractions/      # IAssemblyLoader, ISourceDecompiler, IUIThreadScheduler,
│   │   │                      # ILogSink, ITreeRefreshNotifier
│   │   ├── Adapters/          # DnSpyDecompilerSourceProvider (shared by both hosts)
│   │   ├── Mcp/               # McpContext, ToolRegistry, McpServerHost (HTTP), JsonRpc
│   │   ├── Helpers/           # MethodResolver
│   │   └── Tools/             # 13 instance tool classes ([Description] methods)
│   ├── dnSpy.MCP/             # Extension (net10.0-windows, WPF, MEF) — HTTP transport
│   │   ├── Adapters/          # dnSpy-backed adapter implementations
│   │   ├── Tools/TreeViewTools.cs  # 2 UI-only tools (get_selected_node, refresh_u_i)
│   │   └── TheExtension.cs    # MEF entry, composes McpContext
│   ├── dnSpy.MCP.Headless/    # Standalone exe (stdio MCP transport via MCP SDK)
│   │   ├── Program.cs         # Host + fail-fast startup + stdio server
│   │   ├── CliOptions.cs      # --load (globs) / --config / --help
│   │   └── Adapters/          # dnlib loader, reflection decompiler loader,
│   │                          # stderr log sink, mutation-lock filter
│   └── dnSpy.MCP.Tests/       # Unit + headless E2E tests (spawn real server process)
│       └── TestData/SampleLibrary/  # E2E fixture assembly
├── deps/                      # Vendored dnSpy DLLs (sync via scripts/sync-deps.ps1)
├── scripts/                   # build.ps1, sync-deps.ps1, verify-tool-count.ps1, mcp-probe.js
└── skills/                    # AI agent workflow guides
```

## Headless Mode

For batch analysis without dnSpy running: same 36 tools, decompiler output
byte-identical to dnSpy.exe, stdio transport (Claude Desktop / Cursor /
VS Code can auto-spawn it).

```powershell
# Build & publish
pwsh scripts/build.ps1 -DnSpyPath "D:\tools\dnSpy" -PublishHeadless

# Run directly from build output
dotnet src/dnSpy.MCP.Headless/bin/Release/net10.0-windows/dnspy-mcp-headless.dll --load path\to\target.dll

# Or from the published bundle
dotnet publish\headless\dnspy-mcp-headless.dll --load "path\to\*.dll"
```

- `--load, -l <path>` — pre-load assemblies (repeatable, supports `*`/`?` globs)
- `--config, -c <json>` — reserved, currently unused
- Logging goes to **stderr only** (stdout carries the MCP JSON-RPC frames)
- Parallel mutation calls (`rename_*`, `update_method_body`) are serialized via
  a shared lock, mirroring the Extension transport

Client config (stdio):

```json
{
  "mcpServers": {
    "dnspy-headless": {
      "command": "dotnet",
      "args": [
        "D:\\path\\to\\publish\\headless\\dnspy-mcp-headless.dll",
        "--load", "D:\\path\\to\\target.dll"
      ]
    }
  }
}
```

## Adding New Tools

Tools are discovered at runtime via reflection. To add a new tool:

1. Create a `public sealed` class in `src/dnSpy.MCP.Core/Tools/` under the
   `dnSpy.MCP.Core.Tools` namespace, with a constructor taking `McpContext`
2. Add instance methods `public string MyTool(...)` with a `[Description("...")]` attribute
3. Parameters use `[Description("...")]` for documentation

```csharp
using System.ComponentModel;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class MyTools(McpContext ctx) {
        [Description("Describe what this tool does")]
        public string MyTool(
            [Description("Parameter description")] string param1) {
            // Access loaded modules via the abstraction — works in both
            // Extension (dnSpy) and Headless (dnlib) hosts
            var docs = ctx.AssemblyLoader.GetDocuments();
            return $"Result: {param1} ({docs.Count} assemblies loaded)";
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
You should see 36 MCP tools available:
- decompile_method
- decompile_type
- search_types
- grep
- get_xrefs_to
- assembly_overview
- ...and more
```

If the agent does not auto-discover the tools, tell it: "Use the dnSpy MCP server at `http://127.0.0.1:5150/` to access decompilation and analysis tools."

## Skills

The `skills/` directory contains reusable workflow guides for AI agents working with this MCP server. These skills teach the AI **how to think** about common analysis tasks — it dynamically picks tools based on what it discovers, rather than following rigid steps.

### Available Skills

| Skill | Description |
|-------|-------------|
| `deobfuscate-dotnet` | Deobfuscate .NET binaries: string decryption, symbol renaming, control flow analysis, proxy call resolution, anti-tamper removal |

### Skill Structure

Each skill is a folder containing a `SKILL.md` file:

```
skills/
└── deobfuscate-dotnet/
    └── SKILL.md          # Skill definition (YAML frontmatter + instructions)
```

### Installing Skills for Claude Code

Claude Code auto-discovers skills from `.claude/skills/`. To install:

```bash
# Install a specific skill
cp -r skills/deobfuscate-dotnet .claude/skills/

# Or install all skills
cp -r skills/* .claude/skills/
```

After installing, the skill activates automatically when you describe a matching task — for example:

```
# These will trigger the deobfuscation skill:
"Giúp tôi decrypt các string trong binary này"
"Rename lại các class/method bị obfuscate"
"Binary này bị protect bằng gì? Phân tích giúp tôi"
```

No restart needed — Claude Code picks up new skills on the next message.

### For Other AI Editors

If your AI editor supports custom instructions or system prompts, paste the content of `SKILL.md` directly into your configuration. The skill content is self-contained and editor-agnostic.

## License

This project is licensed under [GPLv3](https://www.gnu.org/licenses/gpl-3.0.en.html), consistent with dnSpy's license.
