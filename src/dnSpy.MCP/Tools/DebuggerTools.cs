using System;
using System.ComponentModel;
using dnSpy.MCP.Debugging;

namespace dnSpy.MCP.Tools;

/// <summary>
/// Extension-only MCP tools driving dnSpy's built-in .NET debugger: start/attach a debuggee,
/// set method breakpoints resolved from assemblies loaded in dnSpy (no PDB/source needed),
/// break/continue/step, read the call stack, and stop the session. Breakpoints and debug
/// state are shared with the dnSpy GUI — the AI and the human debug the same session.
///
/// Not available in headless mode: debugging requires the dnSpy host process.
/// Design doc: docs/superpowers/specs/2026-09-29-debugger-tools-design.md
/// </summary>
public static class DebuggerTools {
    const string NotAvailable = "Error: dnSpy debugger services are not available in this host.";

    /// <summary>Convention of the sibling Extension tool classes (TreeViewTools): tool bodies
    /// return error strings and never throw into the MCP host.</summary>
    static string Safe(Func<string> f) {
        try {
            return f();
        }
        catch (Exception ex) {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>Effective dry-run flag for debug_stop: terminate defaults to dry-run
    /// (it kills debuggee processes), every other mode executes. Internal for unit tests.</summary>
    internal static bool ResolveDryRun(string mode, bool? dryRun) =>
        dryRun ?? mode == "terminate";

    [Description("Gets the current debugger state: whether a session is active, whether the debuggee is running or paused, debuggee processes, current thread, and the last break reason. Extension-only (requires dnSpy).")]
    public static string DebugGetState() =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.GetState() : NotAvailable);

    [Description("Lists attachable .NET processes (pid, name, runtime, architecture, filename). Optional name_filter (substring). Use a pid with debug_attach.")]
    public static string DebugListProcesses(string nameFilter = "") =>
        Safe(() => DnSpyDebuggerService.IsAvailable
            ? DnSpyDebuggerService.ListProcesses(nameFilter)
            : NotAvailable);

    [Description("Starts an executable under the dnSpy debugger and verifies the engine actually started (engine failures arrive asynchronously and are reported as errors; dnSpy may still show a modal error box you must close). Load the target assembly in dnSpy first (load_assembly) so breakpoints resolve. runtime: auto|netfx|dotnet (default auto — a .runtimeconfig.json next to the exe selects dotnet). break (default true) pauses at module .cctor/entry point.")]
    public static string DebugStart(string exePath, string arguments = "", string workingDir = "", bool breakAtStart = true, string runtime = "auto") =>
        Safe(() => DnSpyDebuggerService.IsAvailable
            ? DnSpyDebuggerService.Start(exePath, arguments, workingDir, breakAtStart, runtime)
            : NotAvailable);

    [Description("Attaches the dnSpy debugger to a running .NET process by pid. See debug_list_processes for candidate pids.")]
    public static string DebugAttach(int pid) =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.Attach(pid) : NotAvailable);

    [Description("Stops the debug session. mode: stop (default) | detach | terminate. terminate kills the debuggee processes and defaults to dry_run=true — pass dry_run=false to actually terminate.")]
    public static string DebugStop(string mode = "stop", bool? dryRun = null) =>
        Safe(() => DnSpyDebuggerService.IsAvailable
            ? DnSpyDebuggerService.Stop(mode, ResolveDryRun(mode, dryRun))
            : NotAvailable);

    [Description("Continues (runs) all debuggee processes. Errors when not debugging or already running.")]
    public static string DebugContinue() =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.Continue() : NotAvailable);

    [Description("Requests a break in all debuggee processes, then returns. Poll debug_get_state or call debug_wait_paused to wait for the pause.")]
    public static string DebugBreakAll() =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.BreakAll() : NotAvailable);

    [Description("Steps the current thread while paused. kind: over (default) | into | out.")]
    public static string DebugStep(string kind = "over") =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.Step(kind) : NotAvailable);

    [Description("Waits until the debuggee is paused (or the timeout expires), then returns the state snapshot. Use after debug_start/debug_continue/debug_step. timeout_ms default 10000.")]
    public static string DebugWaitPaused(int timeoutMs = 10000) =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.WaitPaused(timeoutMs) : NotAvailable);

    [Description("Sets a method breakpoint that binds when the module loads in the debuggee — no PDB or source needed. method: 'Namespace.Type::Method' (or 0xToken like other tools). assembly: optional simple-name filter among assemblies loaded in dnSpy. il_offset default 0 (method entry). IMPORTANT: the assembly file loaded in dnSpy must be the same file the debuggee loads (ModuleId binds by filename).")]
    public static string DebugSetBreakpoint(string method, string assembly = "", int ilOffset = 0) =>
        Safe(() => DnSpyDebuggerService.IsAvailable
            ? DnSpyDebuggerService.SetBreakpoint(method, assembly, ilOffset)
            : NotAvailable);

    [Description("Deletes a breakpoint previously created by debug_set_breakpoint, by id.")]
    public static string DebugDeleteBreakpoint(int id) =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.DeleteBreakpoint(id) : NotAvailable);

    [Description("Lists breakpoints created via MCP (id, method, IL offset, bound count) and flags ones removed in the dnSpy UI.")]
    public static string DebugListBreakpoints() =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.ListBreakpoints() : NotAvailable);

    [Description("Returns the managed call stack of the current thread while paused: module!method + IL offset per frame. Method names are resolved from assemblies loaded in dnSpy (best-effort; raw token when unresolved). max_frames default 50.")]
    public static string DebugGetCallstack(int maxFrames = 50) =>
        Safe(() => DnSpyDebuggerService.IsAvailable ? DnSpyDebuggerService.GetCallstack(maxFrames) : NotAvailable);

    [Description("Dismisses modal dialogs in the dnSpy process (e.g. debugger engine error popups that block the UI). Call after a failed debug_start to unblock the UI. Extension-only (requires dnSpy).")]
    public static string DebugDismissDialog() => Safe(DialogCloser.CloseDialogs);
}
