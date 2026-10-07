# Debugger Tools Design (Phase 1: control flow)

- **Date**: 2026-09-29
- **Status**: Approved (design presented in chat, Phase-1 scope selected by user)
- **Author**: tuna99
- **Target version**: dnSpy.MCP 2.1.0

## Summary

Add an Extension-only `debug_*` MCP tool group that drives dnSpy's built-in .NET
debugger (ICorDebug via `dnSpy.Debugger.DotNet.CorDebug`) from MCP: start/attach
a debuggee, set method breakpoints resolved from assemblies already loaded in
dnSpy (no PDB/source needed), break/continue/step, read the call stack, and stop
the session. Breakpoints and debug state are shared with the dnSpy GUI — the AI
and the human debug the same session.

## Goals

1. AI-assisted dynamic RE: breakpoint on `Ns.Type::Method` of an assembly loaded
   in dnSpy, catch it at runtime, walk the stack — the loop static analysis
   cannot do (runtime-decrypted strings, packers, virtualized dispatch).
2. Same-session interoperability with the dnSpy GUI debugger (BPs appear in the
   Breakpoints window).
3. Zero changes to Core/Headless: tools live in the Extension assembly only;
   headless never registers them (it cannot debug — no dnSpy host).

## Non-goals (Phase 1)

1. **No locals/arguments/evaluation** — `Contracts.Debugger.Evaluation` (38+
   types) is out of scope. Phase 2 will choose between dnSpy eval engine vs
   ClrMD read-only snapshot.
2. **No conditional breakpoints / trace / filters** — settings objects exist
   (`DbgCodeBreakpointCondition`, `HitCount`, `Filter`, `Trace`) but Phase 1
   sets plain enabled BPs only.
3. **No process memory read/write** (`DbgProcess.Read/WriteMemory` exists but is
   not exposed).
4. **No push notifications** — MCP tools are request/response; the client polls
   `debug_get_state` / `debug_wait_paused`.
5. **No headless-mode debugger** and **no attach to the dnSpy process itself**.

## Verified API mapping (dnSpyEx 6.6.0, inspected on the local install)

| Capability | Contract API (all public, impl exported via MEF in dnSpy.Debugger.x.dll) |
|---|---|
| Start with args | `DotNetStartDebuggingOptions` / `DotNetFrameworkStartDebuggingOptions` (`Filename`, `CommandLine`, `WorkingDirectory`, `DbgEnvironment`, `BreakKind`) → `DbgManager.Start(DebugProgramOptions)` |
| Break kinds | `PredefinedBreakKinds.DontBreak / CreateProcess / ModuleCctorOrEntryPoint / EntryPoint` |
| Attach by PID | `AttachableProcessesService.GetAttachableProcessesAsync(processIds:[])` → `AttachableProcess.Attach()` |
| Managed process list | `AttachableProcessesService.GetAttachableProcessesAsync(name, ct)` (`ProcessId`, `Name`, `Filename`, `RuntimeName`, `Architecture`) |
| Method breakpoint | `ModuleId.CreateFromFile(dnlib ModuleDef)` + method token → `DbgDotNetCodeLocationFactory.Create(ModuleId, token, ilOffset)` → `DbgCodeBreakpointsService.Add(DbgCodeBreakpointInfo)` / `Remove` / `Breakpoints` |
| Control flow | `DbgManager.IsDebugging`, `IsRunning` (bool?), `RunAll`, `BreakAll`, `StopDebuggingAll`, `DetachAll`, `TerminateAll`, `Processes`, `CurrentProcess/Runtime/Thread`, `Can*` guards |
| Stepping | `DbgManager.CurrentThread.Value.CreateStepper().Step(DbgStepKind, autoClose:false)` |
| Call stack | `DbgThread.GetFrames(count)` / `GetTopStackFrame()` → `DbgStackFrame.Module.Name`, `FunctionToken`, `FunctionOffset`, `HasFunctionToken` |
| State events | `DbgManager.MessageProgramBreak/ProcessPaused/StepComplete/MessageProcessExited/...` (handler must not touch UI) |

## Architecture

### Placement (mirrors `TreeViewTools` exactly)

```
src/dnSpy.MCP/
├── Debugging/DnSpyDebuggerService.cs   ← static service bridge + state snapshot
├── Tools/DebuggerTools.cs              ← 13 static MCP tool methods (thin)
└── TheExtension.cs                     ← Initialize() call at AppLoaded
```

- Services resolved once at `AppLoaded` via `ServiceLocator.TryResolve<T>()`
  (same mechanism as TreeViewTools — no new `[Import]` parts, no new MEF
  composition risk): `DbgManager`, `DbgCodeBreakpointsService`,
  `DbgDotNetCodeLocationFactory`, `AttachableProcessesService`.
- Method-name → token resolution reuses `MethodResolver` over a
  `DnSpyAssemblyLoader(DocumentService, WpfUIThreadScheduler)` built at init.
- Missing service ⇒ every `debug_*` tool returns
  `"Error: dnSpy debugger services are not available."` (graceful degradation,
  same as TreeViewTools when TreeView is null).

### Threading model

- All debugger API calls happen on the WPF UI thread via the existing
  `TreeViewTools.RunOnUIThread` helpers (dnSpy's own menu commands call these
  APIs from the UI thread).
- `DbgManager.Message*` events fire on debug threads; the handler only writes
  plain-string/int fields into a snapshot object under a lock. No UI, no
  dispatcher.
- `debug_wait_paused(timeoutMs)` polls from the MCP server thread: each probe
  dispatches an `IsRunning` read to the UI thread, sleeps 100 ms between
  probes, returns on paused/timeout. The UI thread is never held during the
  wait.
`DnSpyDebuggerService` keeps `Dictionary<int, DbgCodeBreakpoint>` (id assigned
incrementally). `debug_set_breakpoint` resolves `assembly`+`method` via
`MethodResolver.ResolveMethodFlexible`, builds
`DbgDotNetCodeLocationFactory.Create(ModuleId, token, offset)`, adds with
`DbgCodeBreakpointSettings { IsEnabled = true }`. BPs created before
`debug_start` bind when the module loads in the debuggee (ModuleId is
name-based, per dnSpy XML docs).

### Binding requirements discovered during the live smoke (2026-09-29)

1. **ModuleId source**: must come from `IModuleIdProvider.Create(module)` (the
   engine-side comparison never matches `ModuleId.CreateFromFile`).
2. **Path separators**: dnlib stores `ModuleDef.Name` exactly as passed to
   `load_assembly`. A forward-slash path (typical from AI clients) never equals
   the debuggee's backslash module name → the BP stays unbound silently. The
   service normalizes `/` → `\` in the ModuleId before creating the location.
3. **Dispatcher affinity**: location creation + `Add` must run on the WPF UI
   (debug dispatcher) thread.
4. **Dedupe**: `DbgCodeBreakpointsService.Add(info)` returns null when an
   identical-location BP already exists (dnSpy persists BPs across sessions);
   the service then reuses `TryGetBreakpoint(location)`.
5. **JIT inlining** (target-side): a function BP on a tiny method that the JIT
   inlines never fires — RE targets should use `[MethodImpl(NoInlining)]` or
   break on larger methods.
6. **Launch failure root cause verified (2026-10-07)**: the MCP tool defaulted
   `working_dir` to `""`, but the service used a null-only fallback. GDB captured
   a non-null empty `lpCurrentDirectory` at `KERNELBASE!CreateProcessW` and
   `RtlSetLastWin32Error(123)` (`ERROR_INVALID_NAME`). The earlier probes passed
   the target folder, so their arguments were not identical. This was not an
   OS/thread limitation. Missing/empty working directories now resolve to the
   absolute target folder. Framework EXE, .NET managed DLL, and native apphost
   EXE launch flows all hit a breakpoint, step out, produce `total=242`, and
   exit with code 0. The installed dnSpy engine DLL was not modified.

### Runtime auto-detection for `debug_start`

`runtime = "auto"|"netfx"|"dotnet"` (default `auto`):
- `dotnet` → `DotNetStartDebuggingOptions`
- `netfx` → `DotNetFrameworkStartDebuggingOptions` (DebuggeeVersion null = autodetect, same as the GUI dialog default)
- `auto` → `<exe>.runtimeconfig.json` exists next to the target → `dotnet`,
  else `netfx`.

For modern .NET, PE metadata determines host selection: managed images use
`dotnet.exe` (`UseHost=true`); native apphosts launch directly (`UseHost=false`).
Missing/empty `working_dir` uses the absolute target folder; an explicit path
is preserved.

`break` (default `true`) maps to `BreakKind = ModuleCctorOrEntryPoint`,
`false` → `DontBreak`.

## Tools (13) — Extension-only, snake_case, `dry_run` per convention

| Tool | Params | Behavior |
|---|---|---|
| `debug_get_state` | — | `IsDebugging`, `IsRunning`, processes (pid/name/filename), current thread, last break reason (from event snapshot) |
| `debug_list_processes` | `name_filter?` | Attachable (managed) processes: pid, name, filename, runtime, arch |
| `debug_start` | `exe_path, arguments?, working_dir?, break?=true, runtime?="auto"` | Build options → `DbgManager.Start(options)`; verify a Running/Paused process and surface asynchronous engine failures |
| `debug_attach` | `pid` | `GetAttachableProcessesAsync(processIds:[pid])` → `.Attach()`; empty result = not found/not managed |
| `debug_stop` | `mode?="stop"` (`stop|detach|terminate`), `dry_run?` (default **true when mode=terminate**, false otherwise) | `StopDebuggingAll` / `DetachAll` / `TerminateAll` |
| `debug_continue` | — | `RunAll` (guarded by `IsDebugging`) |
| `debug_break_all` | — | `BreakAll` |
| `debug_step` | `kind?="over"` (`into|over|out`) | `CurrentThread.CreateStepper().Step(...)`; error if running/paused-on-no-thread |
| `debug_wait_paused` | `timeout_ms?=10000` | Polls until `IsRunning == false`; returns state snapshot |
| `debug_set_breakpoint` | `assembly, method, il_offset?=0` | See Breakpoint identity; returns id + resolved location |
| `debug_delete_breakpoint` | `id` | `BreakpointsService.Remove` |
| `debug_list_breakpoints` | — | id, module, token, il offset, enabled, resolved method name (best-effort) |
| `debug_get_callstack` | `max_frames?=50` | Frames: module, token, il offset + method name resolved from dnSpy-loaded modules (best-effort; raw token+module if unresolved) |

`ToolRegistry.s_mutationPrefixes` gains `"debug_"` so the extension host
serializes all debug calls through `_mutationLock` (never two concurrent debug
commands). Headless never sees these tools (not in its scanned assembly set).

## Error handling

- No session: control tools return `"Error: not debugging"`.
- `debug_start` with a running session: returns error (one debug session at a
  time — dnSpy itself is single-session via DbgManager).
- Attach/start failures return the string `DbgManager.Start` reports, or the
  attach provider's empty result with pid.
- All tool bodies wrap in try/catch → `"Error: ..."` strings (existing tool
  convention) + `McpLogger.Error`.

## Testing

1. **Unit (dnSpy.MCP.Tests)**: pure helpers — `ParseBreakKind`,
   `PickStartOptions` runtime detection (fixture exe names), snake_case naming
   (already covered by registry tests), `IsMutationTool("debug_*")` in
   `MutationGateTests`.
2. **verify-tool-count.ps1**: auto-discovers the 13 new tools; update
   CLAUDE.md header `## Available MCP Tools (NN)` 38 → 51.
3. **In-dnSpy smoke (manual, scripted)**: build+deploy → launch dnSpy →
   compile a tiny Framework console target (`csc`) → via MCP: load target,
   set BP on its loop method, `debug_start`, `debug_wait_paused`,
   `debug_get_callstack` contains the method, `debug_step`, `debug_continue`,
   `debug_stop(terminate)`. CI cannot run this (needs GUI host).

### Implementation verification (2026-10-07)

- Shipped inventory: 14 debugger tools (including `debug_dismiss_dialog`), 52
  extension tools total; the original 13-tool plan above is historical.
- Release build: 0 warnings/errors; full test suite: 111 passed.
- Direct MCP smoke: native `Stage.exe`, default arguments and working directory,
  paused at `Stage.Main`; stopping left no active debug session.
- TimelineExplorer 2.0.0.1: `Main_Load` breakpoint, step, continue and responsive
  application window verified with a temporary .NET 6.0.36 runtime. Runtime and
  capture artifacts removed afterward; CSV import was not tested.
- Both CI workflows now provision all three debugger contract DLLs. Their
  dependency lists were exercised locally by copying all 12 DLLs from the
  installed dnSpy into an isolated temporary directory; hosted CI was not run.

## Documentation updates

- README: new "Debugger tools (Extension only)" section + tool table rows.
- CLAUDE.md: tool count + debug tool list + "headless: not available" note.
- docs/superpowers/plans/2026-09-29-debugger-tools.md: implementation plan.

## Risks

1. **MEF availability of `DbgManager`** — verified indirectly
   (`DebuggerImpl` in dnSpy.Debugger.x.dll carries `[Export]`; the GUI consumes
   these services through the same composition our ServiceLocator uses).
   Mitigation: null-tolerant initialization, explicit "services unavailable"
   tool errors, smoke test proves it live.
2. **Engine routing by options type** — `DbgManager.Start` dispatches on the
   concrete `DebugProgramOptions` type; wrong runtime pick ⇒ start error
   string (visible, not silent). `auto` heuristic covers the common cases.
3. **Module identity for BPs** — `ModuleId.CreateFromFile` binds by module
   filename; a debuggee module loaded from a different path will not bind.
   Documented in tool description ("assembly must be the file the debuggee
   loads").
4. **dnSpy version drift** — contract DLLs are synced from the same pinned
   install as the rest of deps/ (CI pins DNSPY_TAG v6.6.0).
