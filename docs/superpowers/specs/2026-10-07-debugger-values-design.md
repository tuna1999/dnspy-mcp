# Debugger Values Design — Phase 2

- Date: 2026-10-07
- Status: Proposed for review; planning only, not implemented.
- Baseline: `8952224` — Phase 1 control-flow tools and launch fixes committed locally; no push.

## Goal

At a method/IL breakpoint, inspect actual arguments, locals, `this`, instance fields, strings and array elements in an explicitly selected paused thread/frame. Use the same dnSpy session and evaluation engine as the GUI, without executing target methods or changing values.

## Decisions

1. **Use public dnSpy evaluation contracts.** `DbgLanguageService` and `DbgLanguage.LocalsProvider` already provide runtime values, naming, decompiler debug information and object expansion. The existing contract references suffice; no ClrMD, new package, raw-memory parser or second debugger.
2. **Explicit per-request selection.** Omitted process/thread selectors use dnSpy's current thread; explicit selectors resolve an exact process/thread pair. `frame_index=0` means the top frame, not the GUI's selected frame. Listing/reading never changes the GUI selection.
3. **One bounded snapshot.** A successful locals read replaces the previous snapshot and returns opaque value handles. Expansion keeps the selected frame/context alive only while paused. No persistent watch expressions or object IDs across resumes.
4. **Read-only means no target execution.** Always pass `NoFuncEval | RawView | NoHideRoots`; never enable formatter `FuncEval`, `ToString` or `FullString`. Set `NoDebuggerDisplay`. Reject static/results/dynamic group expansion before asking for its child count or children.

Alternatives rejected: ClrMD duplicates attachment/lifetime/runtime matching; arbitrary expression evaluation broadens the side-effect surface. Neither is needed for paused locals and object inspection.

## Verified source and binary evidence

Paths below are relative to `dnspy-source/` unless specified otherwise.

| Requirement | Evidence |
|---|---|
| Language and frame context | `dnSpy/dnSpy.Contracts.Debugger/Evaluation/DbgLanguageService.cs:47`; `DbgLanguage.cs:58,104-117` |
| Arguments and locals | `Evaluation/DbgValueNodeProvider.cs:70-133`; `DbgLocalsValueNodeInfo.Kind` distinguishes Parameter/Local/Error; `this` uses `PredefinedDbgValueNodeImageNames.This` |
| Bounded child requests | `Evaluation/DbgValueNode.cs:102-112` — `GetChildCount`, `GetChildren(evalInfo, ulong index, int count, options)` |
| No getters/proxies/display methods | `Evaluation/DbgValueNodeEvaluationOptions.cs:38,53,73`; `DbgValueFormatterOptions.cs:46-73`; `dnSpy/Roslyn/dnSpy.Roslyn/Debugger/ValueNodes/MembersValueNodeProvider.cs:193-196` |
| Instance fields and arrays | `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug/Impl/Evaluation/DbgCorDebugInternalRuntimeImpl.cs:307-331`; Roslyn `DbgDotNetValueNodeProviderFactory.cs:521-524,612-637` |
| Explicit object disposal | `dnSpy/dnSpy.Contracts.Debugger/DbgManager.cs:319-325`; `Evaluation/DbgEvaluationInfo.cs:66-69` |
| Stale context trap | `Extensions/dnSpy.Debugger/dnSpy.Debugger/Evaluation/DbgEvaluationContextImpl.cs:51-65` replaces `ContinueContext` on continue. Capture the original object; checking the current property's `IsClosed` is insufficient. |
| Static reads may execute code | CorDebug `DbgCorDebugInternalRuntimeImpl.cs:295,425-475` calls `InitializeStaticConstructor`; `DbgEngineImpl.Evaluation.cs:117-127` does not check `NoFuncEval`. |
| Safe refusal point | Roslyn `StaticMembersValueNodeProvider.cs:34,65`; factory `:628-629` creates a synthetic static group. Refuse by `ImageName` before count/expansion. |
| Existing frame-name risk | Project `src/dnSpy.MCP/Debugging/DnSpyDebuggerService.cs:523-533` matches filenames only. It can confuse same-named runtime modules; use `language.Formatter.FormatFrame` against the actual runtime frame. |

Reflection against the installed `bin/dnSpy.Contracts.Debugger.dll` confirmed the signatures of `GetCurrentLanguage`, `CreateContext`, locals `GetNodes`, node `GetChildren`/`GetChildCount`/`FormatValue`, and evaluation/formatter flags. The evaluation path itself has not been exercised through MCP yet; live proof is an implementation acceptance gate.

## MCP interface

Add exactly three Extension-only tools; total becomes 55 in the extension, remains 36 headless.

| Tool | Parameters | Result |
|---|---|---|
| `debug_list_threads` | `pid?` | Process ID, OS thread ID, managed ID, name, runtime and current-thread marker. |
| `debug_get_locals` | `pid?`, `thread_id?`, `frame_index=0`, `start_index=0`, `count=64` | Fresh snapshot metadata and a page of roots: arguments, locals and `this`; kind, name, type, display value, value ID, error and expandability. |
| `debug_get_value` | `value_id`, `start_index=0`, `count=64` | Selected value descriptor and one child page; new child handles, child count and next index. No expression parameter. |

Extend existing `debug_get_callstack` with optional `pid` and `thread_id` selectors, preserving `max_frames=50`. Frame indexes must match `debug_get_locals`. Replace the filename-only name resolver with the runtime language formatter; when formatting is unavailable, report the runtime module path plus raw token rather than a guessed method.

New tools return JSON serialized into the existing string/text response. New successful responses contain `snapshot_id`, `pid`, `thread_id`, `frame_index`, module/location, total count, page indexes and descriptors. IDs are opaque strings such as `17:3`, not expressions, addresses or labels. JSON values must preserve null versus unavailable; a null reference is not an error. Numeric values are display text with exact declared/runtime types, avoiding JSON integer precision loss.

## Safety and limits

- Require an active debugger with `DbgManager.IsRunning == false`; reject running, mixed/starting and missing-thread states. Require a managed frame with a valid location.
- Explicit thread selection requires `pid`; otherwise reject ambiguity. Resolve within that process, then use the selected frame's runtime to obtain the language. Invalid process/thread/frame is an explicit error, not fallback to the current thread.
- `frame_index`: 0–255. `start_index`: nonnegative signed 64-bit input. `count`: 1–256. Validate before conversion to unsigned indexes; calculate `min(count, total - start)` only after comparing start to total. Start equal to total returns an empty page; start beyond total is an error.
- Maximum 4,096 retained nodes per snapshot. Repeated expansion of the same parent page reuses cached child handles, not new copies. If another page would exceed the ceiling, return a resource-limit error without leaking partial nodes. Cycles are expanded only on explicit client requests; never recursively dump a graph.
- Maximum 4,096 UTF-16 units per formatted name/type/value, without splitting surrogate pairs. Report writer truncation. Keep the engine's bounded string preview; do not claim a complete long-string dump. No arbitrary `ToString` calls, type proxies, enumerable iteration, property getter calls, assignments or process-memory access.
- Preserve provider errors for optimized-out/unavailable/out-of-scope values. Do not fabricate zero/null values or promise original local names when PDB/decompiler information is absent. Synthetic/decompiler-generated variables may be unavailable.
- Static, Results View and Dynamic View group descriptors carry a blocked reason and no expandable handle. Do not call `GetChildCount` or `GetChildren` on them; metadata `HasChildren` alone is insufficient authorization to expand.
- Reuse the existing `debug_` mutation serialization and UI scheduling pattern. Engine providers marshal to their own dispatcher internally. Never block debugger event handlers on synchronous WPF dispatch.
- Do not add value contents to `McpLogger`; normal debugger output may contain application secrets. Logs record operation/status only.

## Snapshot ownership and invalidation

The snapshot owns its `DbgEvaluationInfo`, the frame obtained from `thread.GetFrames`, all root/child `DbgValueNode`s, and a captured `DbgObject ContinueContext`. Store that original continuation object once; the context later replaces its property with a new object.

Every value request validates snapshot generation, captured continuation/frame/node `IsClosed`, and the fully-paused state before and after engine work. A GUI or MCP continue/step/stop/detach invalidates handles even if execution returns to another pause before the next request. A successful new locals read invalidates all previous handles; a malformed request must not destroy a still-valid snapshot.

Close roots/children via `DbgManager.Close`, then close owned evaluation context/frame through `DbgEvaluationInfo.Close`. Close unselected frames immediately. On continuation/frame closure, atomically invalidate the snapshot and enqueue cleanup without blocking the event thread. On extension exit, unsubscribe and release everything. Do not close GUI-owned frames or expose debugger objects outside the service.

`ponytail:` one active snapshot means reading another frame discards the previous frame's handles. Add multiple snapshots only when a real client needs simultaneous frame inspection; not in this phase.

## Out of scope

- **Static-field values:** intentionally excluded until a read path that cannot trigger a static constructor is verified. Existing static metadata tools remain available but do not substitute for runtime values.
- Arbitrary expression evaluation/watch, getters, calls, assignments and cross-resume handles.
- Conditional breakpoints, tracing, headless debugging and memory read/write.
- Full long-string extraction and eager deep object dumps.

## Acceptance

1. A deterministic .NET 10 target paused after initialization returns actual argument/local/`this` values and exact instance-field/array values, not metadata or mocked data.
2. Explicit frame 1 and another paused worker thread return their own variables; the GUI's selection is unchanged.
3. No-PDB and optimized builds report honest availability; compare with dnSpy's Locals window at the same IL location.
4. Hostile getter/`ToString`/debugger-display/proxy/enumerable/static-constructor counters remain unchanged across reads and expansions.
5. Stale IDs are rejected after MCP resume/step and GUI resume/re-pause; fresh IDs work. Replacing a snapshot releases its values without growing retained-node count.
6. Array/root paging, end-of-page, invalid bounds, null and cyclic references are deterministic and bounded.
7. Native apphost, managed DLL and .NET Framework launch/control-flow still work. Real TimelineExplorer `Main_Load` arguments and safe `this` fields are inspected using a temporary matching .NET 6 runtime; no CSV-import claim without exercising it.
8. Build has zero warnings/errors, the existing suite passes, the tool-count guard reports 55, and headless still advertises 36 tools. No engine replacement, global runtime installation, version bump or push is part of this plan.
