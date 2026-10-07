# Debugger Tools Implementation Plan (Phase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 13 Extension-only `debug_*` MCP tools that drive dnSpy's built-in .NET debugger (start/attach, method breakpoints, break/continue/step, callstack, stop).

**Architecture:** Static `DnSpyDebuggerService` bridge (services resolved via `ServiceLocator.TryResolve` at `AppLoaded`) + static `DebuggerTools` MCP class in `dnSpy.MCP.Tools`, mirroring `TreeViewTools`. All debugger calls marshaled to the WPF UI thread; event handler records a plain-data snapshot only.

**Tech Stack:** dnSpy.Contracts.Debugger(.DotNet/.DotNet.CorDebug) contracts, dnlib (token/ModuleId), existing Core `MethodResolver`.

**Spec:** `docs/superpowers/specs/2026-09-29-debugger-tools-design.md`

## Global Constraints

- No changes to Core/Headless behavior; tools live only in `src/dnSpy.MCP`.
- All new dnSpy DLL references: `deps/` copies via `sync-deps.ps1`, `<Private>false</Private>`.
- Every tool method: `public static string` + `[Description]`; returns error strings, never throws.
- `debug_stop(mode:terminate)` defaults `dry_run=true`; other modes default `false`.
- Tool count after change: 51 (38 + 13) — CLAUDE.md header must match.
- Build must stay 0 warnings 0 errors; all 67 existing tests keep passing.

---

### Task 1: deps + references

**Files:**
- Modify: `scripts/sync-deps.ps1` ($requiredDlls list)
- Modify: `src/dnSpy.MCP/dnSpy.MCP.csproj` (References)
- Modify: `src/dnSpy.MCP.Tests/dnSpy.MCP.Tests.csproj` (References, Private=true)

**Steps:**
- [ ] Add `dnSpy.Contracts.Debugger.dll`, `dnSpy.Contracts.Debugger.DotNet.dll`, `dnSpy.Contracts.Debugger.DotNet.CorDebug.dll` to `$requiredDlls`; run `pwsh scripts/sync-deps.ps1`; confirm 3 files land in `deps/`.
- [ ] Add the 3 `<Reference>` items (HintPath `$(DnSpyBin)\…`, `Private=false`) to the extension csproj; same with `Private=true` in the test csproj.
- [ ] `dotnet build` the solution — 0 warnings 0 errors.

### Task 2: DnSpyDebuggerService bridge

**Files:**
- Create: `src/dnSpy.MCP/Debugging/DnSpyDebuggerService.cs`
- Modify: `src/dnSpy.MCP/TheExtension.cs` (AppLoaded: `DnSpyDebuggerService.Initialize(...)` after `TreeViewTools.Initialize`)

**Interfaces (produced for Task 3):**
```csharp
internal static class DnSpyDebuggerService {
    public static bool IsAvailable { get; }
    internal static void Initialize(DbgManager? dbgManager, DbgCodeBreakpointsService? breakpoints,
        DbgDotNetCodeLocationFactory? locationFactory, AttachableProcessesService? attachable,
        MethodResolver resolver);
    // + internal helpers: Snapshot GetState(), TrySetBreakpoint(assembly, method, ilOffset, out id, out error),
    //   DeleteBreakpoint(id), ListBreakpoints(), GetCallstack(maxFrames), Start(...), Attach(pid),
    //   Stop(mode), Continue(), BreakAll(), Step(kind), WaitPaused(timeoutMs), ListProcesses(nameFilter)
}
```
- Event snapshot: subscribe `MessageProgramBreak`, `ProcessPaused`, `MessageStepComplete`, `MessageProcessExited`, `MessageExceptionThrown` → record last-break info (strings) under `lock`.
- UI marshaling via `TreeViewTools.RunOnUIThread` (internal, same assembly).
- BP registry: `Dictionary<int, DbgCodeBreakpoint>` + `DbgCodeLocation`→method-name map for listing.
- `WaitPaused`: poll loop on calling thread, 100 ms interval, single UI dispatch per probe.

### Task 3: DebuggerTools (13 MCP methods)

**Files:**
- Create: `src/dnSpy.MCP/Tools/DebuggerTools.cs` (ns `dnSpy.MCP.Tools`, static class)
- Modify: `src/dnSpy.MCP.Core/Mcp/ToolRegistry.cs` — `s_mutationPrefixes` += `"debug_"`

**Steps:**
- [ ] Implement the 13 tools per spec table; every body = availability check → service call → string result.
- [ ] `verify-tool-count.ps1` lists 51 tools (after CLAUDE.md update in Task 6).

### Task 4: unit tests

**Files:**
- Create: `src/dnSpy.MCP.Tests/DebuggerToolsTests.cs`
- Modify: `src/dnSpy.MCP.Tests/MutationGateTests.cs` (debug_ prefix cases)

**Steps:**
- [ ] `ParseBreakKind`/`PickRuntime` mapping tests + `IsMutationTool("debug_terminate…")`/`debug_get_state` cases.
- [ ] `dotnet test` — all pass (67 + new).

### Task 5: build + in-dnSpy smoke

- [ ] `pwsh scripts/build.ps1 -Configuration Release -Deploy` (dnSpy closed).
- [ ] Compile smoke target: `csc /target:exe` tiny loop program to `%TEMP%`.
- [ ] Launch dnSpy, MCP via curl on the settings port: sequence `load_assembly` → `debug_set_breakpoint` → `debug_start` → `debug_wait_paused` → `debug_get_callstack` (assert method name present) → `debug_step` → `debug_continue` → `debug_stop(terminate)`; assert each response.
- [ ] Close dnSpy; kill stray target process.

### Task 6: docs

- Modify: `CLAUDE.md` (count 38→51 + tool list), `README.md` (debugger section + rows).
- [ ] `verify-tool-count.ps1` → OK (51).
