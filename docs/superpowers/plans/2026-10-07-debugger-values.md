# Debugger Runtime Values Implementation Plan — Phase 2

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. The tasks share debugger lifetime state; execute sequentially, not with concurrent edits.

**Goal:** Read actual locals, arguments, `this`, instance fields and array elements at selected paused execution locations through dnspy-mcp.

**Architecture:** Reuse public dnSpy language/evaluation contracts and the existing Extension-only tool pattern. A single bounded `DnSpyValueService` snapshot owns frame/context/value nodes and issues handles valid only for that pause. Runtime frame formatting replaces filename-only token resolution.

**Tech Stack:** C#/.NET 10 Windows/WPF, existing dnSpy contracts, `System.Text.Json`, existing xUnit harness and build scripts; no new package.

**Spec:** `docs/superpowers/specs/2026-10-07-debugger-values-design.md`

## Global Constraints

- Planning baseline is local commit `8952224`; this document does not implement the feature or authorize a push/release.
- Three new tools, Extension-only: `debug_list_threads`, `debug_get_locals`, `debug_get_value`. Final count 55 extension / 36 headless.
- Always `NoFuncEval | RawView | NoHideRoots`; formatter has `NoDebuggerDisplay`, never `FuncEval`, `ToString` or `FullString`.
- Block StaticMembers/ResultsView/DynamicView before child count or expansion. Static-field values and arbitrary evaluation are excluded because the current CorDebug static path can run a cctor despite `NoFuncEval`.
- Require fully paused state. Explicit thread selection requires process ID. Never change GUI thread/frame selection.
- Frame index 0–255; page start nonnegative; count 1–256; default count 64; maximum retained nodes 4,096; formatted text capped at 4,096 UTF-16 units without splitting surrogate pairs.
- One snapshot; successful locals read replaces it. Capture the original `ContinueContext`; its getter returns a replacement after resume and cannot itself prove freshness.
- No value-content logging, new transport, engine replacement, global runtime installation, package-manager migration or version bump.
- Build must have zero warnings/errors; preserve existing behavior and tests. Documentation/tool-count changes travel with each newly registered tool.

## File Map

| File | Responsibility |
|---|---|
| Modify `src/dnSpy.MCP/Debugging/DnSpyDebuggerService.cs` | Inject language service; selected-thread call stack; runtime-aware formatting; remove obsolete filename resolver. |
| Create `src/dnSpy.MCP/Debugging/DnSpyValueService.cs` | Selection, bounded snapshot ownership, locals/children reads, handles and bounded text writer. Keep snapshot/helper types nested; no generic cache or new interface. |
| Modify `src/dnSpy.MCP/Tools/DebuggerTools.cs` | Existing `Safe` wrapper plus three described tool methods and optional callstack selectors. |
| Modify `src/dnSpy.MCP/TheExtension.cs` | Resolve `DbgLanguageService`, initialize value bridge using existing scheduler, dispose on AppExit. |
| Create `src/dnSpy.MCP.Tests/DebuggerValueTests.cs` | Consumer-visible page/text boundary checks. Live ownership/side-effect behavior is verified against the real engine, not mocked nodes. |
| Create `src/dnSpy.MCP.Tests/TestData/DebuggerValuesTarget/DebuggerValuesTarget.csproj` and `Program.cs` | Deterministic real debuggee, including hostile callbacks and worker-thread mode. |
| Modify `src/dnSpy.MCP.Tests/dnSpy.MCP.Tests.csproj` | Build the debuggee fixture using `ProjectReference ReferenceOutputAssembly="false"`, following existing TestData conventions. |
| Modify `README.md`, `CLAUDE.md`, Phase 2 spec | Tools, counts, handle lifetime, safe-expansion limits, verification evidence. |

No compile-reference changes are expected: the required public evaluation/text types are already in the referenced `dnSpy.Contracts.Debugger.dll`. If the installed contract differs, stop at a proven missing API; do not bypass through reflection/internal engine APIs.

## Shared Proposed Interfaces

These are interfaces to implement, not existing capabilities:

```csharp
// In DebuggerTools; [Description] on each method, Safe(() => ...) bodies.
public static string DebugListThreads(int? pid = null);
public static string DebugGetLocals(int? pid = null, ulong? threadId = null,
    int frameIndex = 0, long startIndex = 0, int count = 64);
public static string DebugGetValue(string valueId,
    long startIndex = 0, int count = 64);
public static string DebugGetCallstack(int maxFrames = 50,
    int? pid = null, ulong? threadId = null);

// DnSpyValueService: initialize once, use the existing IUIThreadScheduler.
internal static void Initialize(DbgManager? manager,
    DbgLanguageService? languages, IUIThreadScheduler scheduler);
internal static string ListThreads(int? pid);
internal static string GetLocals(int? pid, ulong? threadId,
    int frameIndex, long startIndex, int count);
internal static string GetValue(string valueId, long startIndex, int count);
internal static void Dispose();
internal static (ulong Start, int Count) GetPageRange(
    ulong total, long startIndex, int count);
```

Use existing `dnSpy.MCP.Core.Abstractions.IUIThreadScheduler` / `Adapters.WpfUIThreadScheduler`; do not add a parallel scheduling abstraction. Snapshot DTOs contain plain data only. `DbgEvaluationInfo` owns only frames obtained for this service, never the GUI's active frame.

### Task 1: Exact thread/frame selection and trustworthy frame names

**Files:** service bridge, new value service, tools, extension initialization, fixture project/Program.cs, README/CLAUDE.

**Consumes:** existing `DbgManager`, current-thread selection and mutation serialization.
**Produces:** `debug_list_threads` and `debug_get_callstack(max_frames, pid?, thread_id?)`; exact selector helper shared by locals. Count becomes 53.

- [ ] Read the Phase 2 spec and these source contracts before editing: `DbgThread.cs:65-77,139-153`, `DbgLanguageService.cs:47`, `DbgLanguage.cs:104-117`, `DbgFormatter.cs:73`, `DbgStackFrameFormatterOptions.cs`, and existing `GetCallstack`/`ResolveTokenName`.
- [ ] Run LSP references for the exported `DebugGetCallstack` and service initialization before changing signatures. If no LSP exists, use explicit source references/caller search and record the limitation.
- [ ] Compile/run the fixture below. In normal mode set a breakpoint on `ValuesTarget::Checkpoint`; in worker mode attach after `ready:2`, break all, and record actual threads and stacks using existing tools. This establishes the live selection baseline; new selection APIs are expected to be absent.
- [ ] Resolve process/thread without changing `DbgManager.CurrentThread`. Omitted selectors use the actual current thread. Explicit `thread_id` without `pid` is an error. Enumerate threads from the specified process; unknown IDs never fall back. Locals will obtain `thread.GetFrames(frameIndex + 1)` and close unselected frames.
- [ ] Resolve `DbgLanguageService` through `ServiceLocator.TryResolve`, retaining null-tolerant initialization. Format each actual runtime frame using the following verified contract route:

```csharp
var language = languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
var context = language.CreateContext(frame,
    DbgEvaluationContextOptions.NoMethodBody,
    cancellationToken: ToolCallScope.Token);
var info = new DbgEvaluationInfo(context, frame, ToolCallScope.Token);
try {
    language.Formatter.FormatFrame(info, writer,
        DbgStackFrameFormatterOptions.ModuleNames |
        DbgStackFrameFormatterOptions.DeclaringTypes |
        DbgStackFrameFormatterOptions.Namespaces |
        DbgStackFrameFormatterOptions.ParameterTypes |
        DbgStackFrameFormatterOptions.IP,
        DbgValueFormatterOptions.NoDebuggerDisplay,
        System.Globalization.CultureInfo.InvariantCulture);
}
finally { info.Close(); }
```

`writer` is the existing public `DbgStringBuilderTextWriter` here, replaced with the bounded writer in Task 2. Each frame is newly obtained and owned by the tool. Do not set `ParameterValues`; do not resolve names through unrelated loaded modules. Formatting failure falls back to runtime module filename + token + IL offset. Remove the now-unused `ResolveTokenName` method.

- [ ] Register `debug_list_threads` through the existing static tool class. Update the two inventory headers to 53 and document optional stack selectors.
- [ ] Build/test and run the tool-count guard. Through directly mounted dnspy-mcp tools, list worker threads, get stacks for both exact threads and prove their reported IDs differ; reject an invalid PID/thread without changing GUI selection. Compare displayed runtime frame names with dnSpy's Call Stack window.
- [ ] Record evidence in the spec. Commit this working slice only when implementation commits are authorized; never push as a side effect.

### Task 2: Locals/arguments/this snapshot and bounded output

**Files:** value service, tools, extension exit, new tests and target fixture, README/CLAUDE/spec.

**Consumes:** Task 1's selected-thread helper and language service.
**Produces:** `debug_get_locals`, snapshot metadata and root handles; count becomes 54. Proposed internal helpers: `GetPageRange` above and nested `DnSpyValueService.BoundedTextWriter(int capacity)` implementing `IDbgTextWriter`, exposing `Text` and `Truncated`.

- [ ] Add these deterministic boundary tests before implementing the helpers; run the filtered suite and observe failure from the missing behavior:

```csharp
[Fact]
public void Page_boundaries_do_not_wrap_or_read_past_the_end() {
    Assert.Equal((8UL, 2), DnSpyValueService.GetPageRange(10, 8, 64));
    Assert.Equal((10UL, 0), DnSpyValueService.GetPageRange(10, 10, 64));
    Assert.Equal(((ulong)long.MaxValue, 64),
        DnSpyValueService.GetPageRange(ulong.MaxValue, long.MaxValue, 64));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        DnSpyValueService.GetPageRange(10, -1, 64));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        DnSpyValueService.GetPageRange(10, 11, 64));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        DnSpyValueService.GetPageRange(10, 0, 0));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        DnSpyValueService.GetPageRange(10, 0, 257));
}

[Fact]
public void Display_cap_preserves_unicode_and_reports_truncation() {
    var writer = new DnSpyValueService.BoundedTextWriter(4);
    writer.Write(DbgTextColor.Text, "a𝄞bc");
    writer.Write(DbgTextColor.Text, "more");
    Assert.Equal("a𝄞b", writer.Text);
    Assert.True(writer.Truncated);
    var split = new DnSpyValueService.BoundedTextWriter(2);
    split.Write(DbgTextColor.Text, "a𝄞bc");
    split.Write(DbgTextColor.Text, "z");
    Assert.Equal("a", split.Text);
    Assert.True(split.Truncated);
}
```

Add normal `using` directives and follow the existing test namespace/access conventions. These tests target range overflow and actual output truncation, not flag values, tool discovery or forwarding.

- [ ] Implement unsigned-safe page math: validate start/count, compare start to total before subtraction, then take the smaller of remaining items and requested count. Implement the bounded writer without allowing later writes to refill text after truncation.
- [ ] Validate a fully-paused managed frame and construct locals context with `DbgEvaluationContextOptions.None`, **not** `NoMethodBody` (locals need method/decompiler debug information):

```csharp
var language = languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
var context = language.CreateContext(frame,
    DbgEvaluationContextOptions.None, cancellationToken: ToolCallScope.Token);
var info = new DbgEvaluationInfo(context, frame, ToolCallScope.Token);
var capturedContinueContext = context.ContinueContext;
var roots = language.LocalsProvider.GetNodes(info,
    DbgValueNodeEvaluationOptions.NoFuncEval |
    DbgValueNodeEvaluationOptions.RawView |
    DbgValueNodeEvaluationOptions.NoHideRoots,
    DbgLocalsValueNodeEvaluationOptions.ShowRawLocals |
    DbgLocalsValueNodeEvaluationOptions.ShowCompilerGeneratedVariables |
    DbgLocalsValueNodeEvaluationOptions.ShowDecompilerGeneratedVariables);
```

- [ ] Format descriptors with `Decimal | Namespaces | IntrinsicTypeKeywords | NoDebuggerDisplay`. Format name, actual/expected type and display value into separate bounded writers; propagate provider error text. Classify `this` by `ImageName`, parameters/locals by provider `Kind`. Do not access `RawValue` strings solely to slice them after a potentially huge materialization.
- [ ] Build a candidate snapshot, then publish it only if the captured continuation/frame are still open and the process is fully paused. A successful publication increments snapshot generation and closes the previous snapshot. Close the candidate and all frames on error/cancellation; invalid input must not evict a valid previous snapshot.
- [ ] Store the captured continuation object and selected frame, not merely the context's current property. Validate them before and after every read. Subscribe to their closure for invalidation/queued cleanup; do not synchronously dispatch to WPF from an engine callback. On AppExit dispose and unsubscribe. Repeated reads must keep only one live snapshot, with at most 4,096 roots/children.
- [ ] Add `debug_get_locals`, update docs/count to 54, and run `dotnet test src/dnSpy.MCP.Tests -c Release --nologo --filter FullyQualifiedName~DebuggerValueTests`, then the full suite and guard.
- [ ] Deploy with dnSpy closed and run directly mounted MCP: breakpoint `Checkpoint` in normal mode → frame 0 arguments `argument=7`, `total=49`, decoded string `decoded-7` → frame 1 `this`, `input=7` and its initialized locals. Compare with GUI Locals at the same frame. Root types and values must be concrete and exact; optimized/unavailable values retain errors, not fake nulls.
- [ ] Exercise worker mode: inspect the `Inspect` frame for the two workers (inputs 11 and 13; totals 53 and 55). Use recorded thread IDs and the actual stack index, not an assumed top frame. Rebuild without PDB and with optimization; report availability honestly.
- [ ] Record evidence, then commit the working root-inspection slice only if authorized.

### Task 3: Safe object/array expansion and pause-lifetime proof

**Files:** value service, tools, boundary tests if a real edge needs coverage, README/CLAUDE/spec.

**Consumes:** Task 2's snapshot handles and captured continuation/frame.
**Produces:** `debug_get_value` and bounded child handles; final count 55. All acceptance criteria in the spec must be exercised before claiming Phase 2 complete.

- [ ] Before implementing, preserve root IDs from the live target, then confirm no child-inspection tool exists. Use the fixture's hostile callbacks as the failing capability check; no fake debugger/value-node implementation.
- [ ] Reject unknown/stale/malformed handles without reconstructing a value from a name or evaluating an expression. Check snapshot generation, captured continuation/frame and selected node `IsClosed`, plus fully-paused state.
- [ ] Inspect `ImageName` first. For StaticMembers, ResultsView or DynamicView return a blocked descriptor and never call `GetChildCount`, `GetChildren` or a display callback that could initialize that provider. For ordinary nodes use:

```csharp
const DbgValueNodeEvaluationOptions options =
    DbgValueNodeEvaluationOptions.NoFuncEval |
    DbgValueNodeEvaluationOptions.RawView |
    DbgValueNodeEvaluationOptions.NoHideRoots;
var total = node.GetChildCount(info);
var page = GetPageRange(total, startIndex, count);
var children = page.Count == 0 ? Array.Empty<DbgValueNode>() :
    node.GetChildren(info, page.Start, page.Count, options);
```

- [ ] Cache each returned child by parent handle and absolute child index so overlapping pages reuse handles. Close unused or partially-produced children on exception, cancellation, stale continuation or resource-limit failure. No recursive walk; cycles consume nodes only when explicitly expanded.
- [ ] Recheck snapshot freshness before publishing the child page. Preserve null/error distinctions, array index labels and truncation markers. Format only with the safe writer/flags from Task 2; property nodes should retain the engine's function-evaluation-disabled error.
- [ ] Add `debug_get_value`; inventory 55 and headless 36; document that successful `debug_get_locals` invalidates old handles, and resume/step invalidates them even after another pause.
- [ ] Verify exact fixture values: `this.Number=42`, text `fixture-value`, `Numbers=[3,5,8]`, and `Next` cycle. Page Numbers at start 1/count 2 → 5 and 8; end index 3 → empty; index 4/negative → error. Expand parent pages twice and check handles/retained count do not grow for the same indexes.
- [ ] Prove callbacks are not invoked: read hostile property; expand payload and its null `Dormant` field; reject static/results/dynamic groups. Resume normal mode and assert real stdout `result=49;side_effects=0`. Any nonzero counter or target-side exception fails acceptance.
- [ ] Hold a value ID, step and re-pause: old ID must fail; a fresh locals read works. Repeat with **GUI** Continue/re-pause, then stop/detach; explicit target termination is not a natural exit-code claim. Replace snapshots between threads/frames and confirm old handles fail without leaked values.
- [ ] Re-run native apphost, managed DLL and .NET Framework control-flow smokes. Verify matching-runtime names instead of filename guesses. For real TimelineExplorer 2.0.0.1, provision a temporary official .NET 6.0.36 Core + WindowsDesktop runtime, verify download hashes, start only dnSpy with process-local runtime environment, and use directly mounted MCP tools to hit `TimelineExplorer.Forms.Main::Main_Load`. Read `sender`, `e`, `this` and safe fields; compare with GUI. Dispose snapshots, remove only created BPs/fixtures/runtime, then restore dnSpy's normal environment. No global install or replacement of the installed engine.
- [ ] Run final commands below, verify headless `tools/list` still contains 36 tools and no `debug_*`, and record actual outcomes. Mark the spec implemented only after every acceptance scenario passes. Commit the complete slice only if authorized; do not push/release.

## Deterministic Debuggee Fixture

Create the project with the existing .NET 10 SDK, no package dependencies:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Optimize>false</Optimize>
    <DebugType>portable</DebugType>
  </PropertyGroup>
</Project>
```

`Program.cs` (the `--workers` mode is for attach/break/thread selection; stop it intentionally):

```csharp
using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

public static class ValuesTarget {
    public static int SideEffects;
    static bool workers;
    static int ready;

    public sealed class DormantType {
        static DormantType() { Interlocked.Increment(ref SideEffects); }
        public static int Value = 9;
    }

    public sealed class PayloadProxy {
        public PayloadProxy(Payload value) { Interlocked.Increment(ref SideEffects); }
    }

    [DebuggerDisplay("{Dangerous}")]
    [DebuggerTypeProxy(typeof(PayloadProxy))]
    public sealed class Payload : IEnumerable {
        public int Number = 42;
        public string Text = "fixture-value";
        public int[] Numbers = { 3, 5, 8 };
        public Payload Next;
        public DormantType Dormant;
        public int Dangerous {
            get { Interlocked.Increment(ref SideEffects); return -1; }
        }
        public override string ToString() {
            Interlocked.Increment(ref SideEffects); return "must-not-call";
        }
        public IEnumerator GetEnumerator() {
            Interlocked.Increment(ref SideEffects); return Numbers.GetEnumerator();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Inspect(int input) {
            int total = input + Number;
            string decoded = "decoded-" + input;
            int[] values = Numbers;
            Checkpoint(this, input, total, decoded, values);
            GC.KeepAlive(decoded);
            GC.KeepAlive(values);
            return total;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Checkpoint(Payload payload, int argument,
        int total, string decoded, int[] values) {
        if (workers) {
            if (Interlocked.Increment(ref ready) == 2) Console.WriteLine("ready:2");
            Thread.Sleep(Timeout.Infinite);
        }
        GC.KeepAlive(payload);
        GC.KeepAlive(argument);
        GC.KeepAlive(total);
        GC.KeepAlive(decoded);
        GC.KeepAlive(values);
    }

    public static void Main(string[] args) {
        workers = args.Length != 0 && args[0] == "--workers";
        if (workers) {
            new Thread(() => new Payload().Inspect(11)).Start();
            new Thread(() => new Payload().Inspect(13)).Start();
            Thread.Sleep(Timeout.Infinite);
        }
        var value = new Payload();
        value.Next = value;
        int result = value.Inspect(7);
        Console.WriteLine("result=" + result + ";side_effects=" + SideEffects);
    }
}
```

Use named parameters from metadata when available; raw local names may differ without PDB. The values remain consumer-visible through arguments/fields/output. Debuggee metadata names for nested methods include `ValuesTarget+Payload`; resolve the exact name/token from dnspy-mcp instead of guessing syntax. Use `[MethodImpl(NoInlining)]` and a checkpoint after initialization; breakpoint-at-method-entry cannot prove uninitialized locals exist.

The fixture is permanent regression scaffolding for this real consumer journey, not product fallback code. Temporary protocol scripts/screenshots/runtime downloads are removed after observed proof. Do not create a second framework or golden snapshot of incidental UI wording.

## Verification Commands

Run from repository root, respecting existing tooling:

```powershell
dotnet build dnspy_mcp.sln -c Release --nologo
dotnet test src/dnSpy.MCP.Tests -c Release --nologo --no-build
pwsh -NoProfile -File scripts/verify-tool-count.ps1
# Close the exact dnSpy host before deployment; preserve user debug sessions.
pwsh -NoProfile -File scripts/build.ps1 -Configuration Release `
  -DnSpyPath 'D:\ProgramFiles\StandaloneTools\RETools\dnSpy\win64' -Deploy
```

Unit tests are not proof of engine integration. Record the actual MCP sequence, selected PID/thread/frame, expected and observed values, target stdout, side-effect counter, handle invalidation and GUI comparison. Hosted CI cannot exercise this GUI debugger; local live smoke is mandatory. Do not assert TimelineExplorer CSV processing or full long-string extraction from a simple window/locals check.

## Plan Review and Execution Gate

This plan chooses safe paused-value inspection, not arbitrary evaluation. Static-field inspection is explicitly blocked pending a proven no-cctor path. API signatures and the static/lifetime hazards were checked against source; installed contract signatures were checked with reflection. Phase 2 runtime behavior remains unverified until implementation.

Review the proposed spec and tool/lifetime contract before execution. Do not start implementing merely because the baseline was committed. The implementation tasks are tightly coupled; inline execution with checkpoints is the conservative default when authorized.

## Planning Validation (2026-10-07)

- Baseline release build: zero warnings/errors; 111/111 existing tests passed;
  tool-count guard: 52. Direct dnspy-mcp smoke launched the native `Stage.exe`
  with omitted cwd, paused at `Stage.Main`, and stopped the session.
- The fixture project and `Program.cs` above were extracted into a unique
  temporary directory, built with zero warnings/errors and executed normally:
  `result=49;side_effects=0`. The directory was removed after verification.
- Plan/spec structure, code fences, baseline/spec link and safety/lifetime
  constraints were checked. This validates the plan and target, not the future
  MCP value-inspection feature; no Phase 2 tool is implemented yet.
