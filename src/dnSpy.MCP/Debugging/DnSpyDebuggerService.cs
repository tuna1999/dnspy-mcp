using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Metadata;
using dnlib.DotNet;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Debugging;

/// <summary>
/// Static bridge to dnSpy's built-in .NET debugger, populated once at <c>AppLoaded</c> via
/// <c>ServiceLocator.TryResolve</c> (same pattern as <see cref="dnSpy.MCP.Tools.TreeViewTools"/>).
///
/// Threading contract:
///  - Every debugger API call is marshaled to the WPF UI thread (<see cref="UI{T}"/>) — dnSpy's
///    own menu commands call these services from the UI thread.
///  - <see cref="DbgManager"/> events fire on debug threads; handlers only write plain-string
///    fields into the snapshot under a lock (no UI, no dispatcher).
///  - <see cref="WaitPaused"/> polls from the calling (MCP server) thread and dispatches one
///    short read per probe — the UI thread is never held while waiting.
///
/// Design doc: docs/superpowers/specs/2026-09-29-debugger-tools-design.md
/// </summary>
internal static class DnSpyDebuggerService {
    const string Unavailable = "Error: dnSpy debugger services are not available in this host.";

    static DbgManager? _manager;
    static DbgCodeBreakpointsService? _breakpoints;
    static DbgDotNetCodeLocationFactory? _locations;
    static AttachableProcessesService? _attachable;
    static dnSpy.Contracts.Metadata.IModuleIdProvider? _moduleIdProvider;
    static MethodResolver? _resolver;

    static readonly Dictionary<int, DbgCodeBreakpoint> _bpById = new();
    static readonly Dictionary<int, DbgDotNetCodeLocation> _bpLocationById = new();
    static readonly Dictionary<int, string> _bpLabelById = new();
    static int _nextBpId;

    static readonly object _breakLock = new();
    static string _lastBreak = "none";

    public static bool IsAvailable => _manager != null;

    internal static void Initialize(
        DbgManager? manager,
        DbgCodeBreakpointsService? breakpoints,
        DbgDotNetCodeLocationFactory? locations,
        AttachableProcessesService? attachable,
        dnSpy.Contracts.Metadata.IModuleIdProvider? moduleIdProvider,
        MethodResolver? resolver) {
        _manager = manager;
        _breakpoints = breakpoints;
        _locations = locations;
        _attachable = attachable;
        _moduleIdProvider = moduleIdProvider;
        _resolver = resolver;
        if (manager != null) {
            manager.MessageProgramBreak += OnProgramBreak;
            manager.MessageEntryPointBreak += OnEntryPointBreak;
            manager.MessageStepComplete += OnStepComplete;
            manager.MessageExceptionThrown += OnExceptionThrown;
            manager.MessageProcessExited += OnProcessExited;
            // Generic engine→manager message channel: DbgMessageConnected error reports
            // (e.g. "Couldn't start debugging: ...") arrive here, not via the typed events.
            manager.Message += OnDbgMessage;
            // Engine errors ("Couldn't start debugging: ...") surface here, not as
            // exceptions from DbgManager.Start — capture them so MCP clients see the cause.
            manager.DbgManagerMessage += OnDbgManagerMessage;
        }
        McpLogger.Info(
            $"Debugger services: manager={(manager != null ? "ok" : "null")}, " +
            $"breakpoints={(breakpoints != null ? "ok" : "null")}, " +
            $"locations={(locations != null ? "ok" : "null")}, " +
            $"attach={(attachable != null ? "ok" : "null")}, " +
            $"moduleIdProvider={(moduleIdProvider != null ? "ok" : "null")}");
    }

    static void RecordBreak(string reason) {
        lock (_breakLock)
            _lastBreak = $"{reason} @ {DateTime.Now:HH:mm:ss}";
    }

    static void OnProgramBreak(object? sender, DbgMessageProgramBreakEventArgs e) => RecordBreak("program break");
    static void OnEntryPointBreak(object? sender, DbgMessageEntryPointBreakEventArgs e) => RecordBreak("entry point");
    static void OnStepComplete(object? sender, DbgMessageStepCompleteEventArgs e) => RecordBreak("step complete");
    static void OnExceptionThrown(object? sender, DbgMessageExceptionThrownEventArgs e) => RecordBreak("exception");
    static void OnProcessExited(object? sender, DbgMessageProcessExitedEventArgs e) =>
        RecordBreak($"process exited (code {e.ExitCode})");
    static void OnDbgManagerMessage(object? sender, DbgManagerMessageEventArgs e) {
        RecordBreak($"msg[{e.MessageKind}]: {e.Message}");
        McpLogger.Info($"DbgManager: [{e.MessageKind}] {e.Message}");
    }

    static void OnDbgMessage(object? sender, DbgMessageEventArgs e) {
        var text = DescribeMessage(e);
        RecordBreak($"engine: {text}");
        McpLogger.Info($"DbgManager.Message: {text}");
    }

    /// <summary>Kind + all non-empty string properties (message text). The args
    /// classes carry the text under different member names per message kind; reading
    /// by reflection keeps this robust across dnSpy contract versions.</summary>
    static string DescribeMessage(DbgMessageEventArgs e) {
        string kind;
        try { kind = e.Kind.ToString(); }
        catch { kind = e.GetType().Name; }
        var parts = new List<string>();
        foreach (var p in e.GetType().GetProperties()) {
            if (p.PropertyType != typeof(string) || !p.CanRead || p.GetIndexParameters().Length != 0)
                continue;
            try {
                if (p.GetValue(e) is string v && !string.IsNullOrEmpty(v) && !parts.Contains(v))
                    parts.Add(v);
            }
            catch { /* skip unreadable */ }
        }
        return parts.Count == 0 ? kind : $"{kind}: {string.Join(" | ", parts)}";
    }

    static string LastBreak {
        get { lock (_breakLock) return _lastBreak; }
    }

    /// <summary>Marshals to the WPF UI thread. Throws when no dispatcher exists (unit-test
    /// host); tool methods translate that into an error string.</summary>
    static T UI<T>(Func<T> func) {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher unavailable (no dnSpy UI)");
        if (dispatcher.CheckAccess())
            return func();
        return dispatcher.Invoke(func, DispatcherPriority.Normal);
    }

    static string DescribeRunning(bool? running) =>
        running is null ? "mixed or starting (no process yet)" : running.Value ? "true" : "false (paused)";

    internal static string GetState() {
        var m = _manager;
        if (m is null) return Unavailable;
        return UI(() => {
            var sb = new StringBuilder();
            sb.AppendLine($"Debugging: {m.IsDebugging}");
            sb.AppendLine($"Running: {DescribeRunning(m.IsRunning)}");
            foreach (var p in m.Processes)
                sb.AppendLine($"Process [{p.Id}] {p.Name} | {p.Filename} | state={p.State}");
            var t = m.CurrentThread.Current;
            sb.AppendLine(t is null
                ? "Current thread: (none)"
                : $"Current thread: [{t.Id}] {(t.IsMain ? "main" : "worker")} managed={t.ManagedId?.ToString() ?? "?"} {t.UIName}");
            sb.AppendLine($"Last break: {LastBreak}");
            return sb.ToString();
        });
    }

    /// <summary>Void variant of <see cref="UI{T}"/> for action-only calls.</summary>
    static void UIVoid(Action action) {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF dispatcher unavailable (no dnSpy UI)");
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action, DispatcherPriority.Normal);
    }

    internal static string ListProcesses(string? nameFilter) {
        if (_manager is null) return Unavailable;
        if (_attachable is null) return "Error: AttachableProcessesService not available.";
        var filter = nameFilter?.Trim();
        // Filter client-side: the service's (string, CancellationToken) overload
        // accepts wildcards but does not reliably narrow results in practice
        // (verified live against dnSpy 6.6.0) — a substring match on name and
        // path is deterministic and testable.
        var procs = _attachable.GetAttachableProcessesAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(filter))
            procs = procs.Where(p =>
                p.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.Filename?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        var sb = new StringBuilder();
        foreach (var p in procs.OrderBy(x => x.ProcessId))
            sb.AppendLine($"[{p.ProcessId}] {p.Name} | runtime={p.RuntimeName} | arch={p.Architecture} | {p.Filename}");
        return sb.ToString();
    }

    internal static string Start(string exePath, string? arguments, string? workingDir, bool breakAtStart, string runtime) {
        var m = _manager;
        if (m is null) return Unavailable;
        if (!File.Exists(exePath)) return $"Error: file not found: {exePath}";
        var baselineBreak = LastBreak;
        var result = UI(() => {
            if (m.IsDebugging)
                return "Error: already debugging — stop the current session first (debug_stop).";
            var options = CreateStartOptions(exePath, runtime, workingDir);
            ((CorDebugStartDebuggingOptions)options).Filename = exePath;
            ((CorDebugStartDebuggingOptions)options).CommandLine = arguments ?? string.Empty;
            if (options is StartDebuggingOptions sdo)
                sdo.BreakKind = breakAtStart
                    ? PredefinedBreakKinds.ModuleCctorOrEntryPoint
                    : PredefinedBreakKinds.DontBreak;
            var error = m.Start(options);
            return string.IsNullOrEmpty(error)
                ? string.Empty
                : $"Error: {error}";
        });
        if (!string.IsNullOrEmpty(result))
            return result;
        // m.Start returning empty only means the request was accepted. Engine failures
        // (e.g. "Could not execute ...") arrive asynchronously via the message events
        // and also pop a modal error box in dnSpy — poll off the UI thread so the tool
        // reports the real outcome instead of a false "Started".
        const int confirmTimeoutMs = 5000;
        var deadline = Environment.TickCount64 + confirmTimeoutMs;
        while (Environment.TickCount64 < deadline) {
            Thread.Sleep(100);
            // Starting-state processes also show up here right before an engine
            // failure — only Running/Paused proves the debuggee actually launched.
            var started = UI(() => m.IsDebugging && m.Processes.Any(p =>
                p.State == DbgProcessState.Running || p.State == DbgProcessState.Paused));
            if (started)
                return $"Started: {exePath}{(breakAtStart ? " (will break at entry)" : "")}";
            var brk = LastBreak;
            if (brk != baselineBreak && LooksLikeStartFailure(brk))
                return "Error: dnSpy failed to start the process — " + brk +
                       " (dnSpy may show a modal error box; close it to unblock the UI)";
        }
        return $"Started (unconfirmed after {confirmTimeoutMs / 1000}s): {exePath} — poll debug_get_state";
    }

    /// <summary>Engine start failures arrive as free-text messages ("Could not start
    /// the debugger…", "Error: Could not execute…"). Benign engine messages like
    /// "engine: ThreadExited" must NOT match. Pure predicate, unit-tested.</summary>
    internal static bool LooksLikeStartFailure(string lastBreak) {
        if (string.IsNullOrEmpty(lastBreak)) return false;
        return lastBreak.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lastBreak.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lastBreak.StartsWith("msg[", StringComparison.Ordinal);
    }

    internal static DebugProgramOptions CreateStartOptions(string exePath, string runtime, string? workingDir = null) {
        CorDebugStartDebuggingOptions options;
        if (NormalizeRuntime(runtime, exePath) == "dotnet") {
            using var stream = File.OpenRead(exePath);
            using var image = new PEReader(stream);
            // Managed images need dotnet.exe; native apphosts must launch themselves.
            options = new DotNetStartDebuggingOptions { UseHost = image.HasMetadata };
        }
        else {
            options = new DotNetFrameworkStartDebuggingOptions();
        }
        // An empty native lpCurrentDirectory is invalid (Win32 123), not "inherit".
        options.WorkingDirectory = string.IsNullOrEmpty(workingDir)
            ? Path.GetDirectoryName(Path.GetFullPath(exePath))
            : workingDir;
        return options;
    }

    internal static string NormalizeRuntime(string runtime, string exePath) {
        var r = (runtime ?? "").Trim().ToLowerInvariant();
        switch (r) {
            case "dotnet": case "core": case "netcore": case "net": case "net5": case "net6":
            case "net7": case "net8": case "net9": case "net10":
                return "dotnet";
            case "netfx": case "framework": case "net48": case "net472":
                return "netfx";
            default:
                return DetectRuntime(exePath);
        }
    }

    /// <summary>Pure heuristic: a &lt;name&gt;.runtimeconfig.json next to the exe means a
    /// modern .NET apphost; otherwise assume .NET Framework. Unit-tested.</summary>
    internal static string DetectRuntime(string exePath) {
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(exePath);
        return File.Exists(Path.Combine(dir, name + ".runtimeconfig.json")) ? "dotnet" : "netfx";
    }

    internal static string Attach(int pid) {
        if (_manager is null) return Unavailable;
        if (_attachable is null) return "Error: AttachableProcessesService not available.";
        var procs = _attachable.GetAttachableProcessesAsync(null, new[] { pid }, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        var target = procs.FirstOrDefault(p => p.ProcessId == pid);
        if (target is null)
            return $"Error: process {pid} not found or not an attachable .NET process (see debug_list_processes).";
        return UI(() => {
            target.Attach();
            return $"Attached to [{target.ProcessId}] {target.Name} (runtime {target.RuntimeName})";
        });
    }

    internal static string Stop(string mode, bool dryRun) {
        var m = _manager;
        if (m is null) return Unavailable;
        if (mode != "stop" && mode != "detach" && mode != "terminate")
            return $"Error: unknown mode '{mode}' — use stop | detach | terminate.";
        return UI(() => {
            if (!m.IsDebugging)
                return "Not debugging — nothing to stop.";
            if (dryRun)
                return $"[dry-run] Would {mode} the debug session{(mode == "terminate" ? " (kill debuggee processes)" : "")}.";
            switch (mode) {
                case "detach":
                    m.DetachAll();
                    return "Detached from all debuggee processes.";
                case "terminate":
                    m.TerminateAll();
                    return "Terminated all debuggee processes.";
                default:
                    m.StopDebuggingAll();
                    return "Stopped debugging.";
            }
        });
    }

    internal static string Continue() {
        var m = _manager;
        if (m is null) return Unavailable;
        return UI(() => {
            if (!m.IsDebugging) return "Error: not debugging.";
            if (m.IsRunning == true) return "Already running.";
            m.RunAll();
            return "Continued.";
        });
    }

    internal static string BreakAll() {
        var m = _manager;
        if (m is null) return Unavailable;
        return UI(() => {
            if (!m.IsDebugging) return "Error: not debugging.";
            if (m.IsRunning == false) return "Already paused.";
            m.BreakAll();
            return "Break requested — poll debug_get_state or call debug_wait_paused.";
        });
    }

    internal static string Step(string kind) {
        var m = _manager;
        if (m is null) return Unavailable;
        return UI(() => {
            if (!m.IsDebugging) return "Error: not debugging.";
            if (m.IsRunning == true) return "Error: process is running — break first (debug_break_all).";
            var thread = m.CurrentThread.Current;
            if (thread is null) return "Error: no current thread.";
            var kindNorm = (kind ?? "").Trim().ToLowerInvariant();
            if (kindNorm != "into" && kindNorm != "over" && kindNorm != "out")
                return $"Error: unknown kind '{kind}' — use into | over | out.";
            var stepKind = kindNorm switch {
                "into" => DbgStepKind.StepInto,
                "out" => DbgStepKind.StepOut,
                _ => DbgStepKind.StepOver,
            };
            // autoClose: the stepper disposes itself when the step completes — a fresh
            // stepper is created per debug_step call, so false would leak one each time.
            thread.CreateStepper().Step(stepKind, autoClose: true);
            return $"Stepping {kind}.";
        });
    }

    internal static string WaitPaused(int timeoutMs) {
        var m = _manager;
        if (m is null) return Unavailable;
        timeoutMs = Math.Max(0, timeoutMs);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) {
            // IsRunning: true = all running, false = all paused, null = mixed OR no process
            // registered yet (startup window). Only !IsDebugging means "no session".
            var (isDebugging, running) = UI(() => (m.IsDebugging, m.IsRunning));
            if (!isDebugging) return "Error: not debugging.";
            if (running == false) return "Paused.\n" + GetState();
            // debug_* tools are serialized via the host's mutation lock — stop waiting as
            // soon as the host cancels (tool timeout) so the lock isn't held abandoned.
            if (ToolCallScope.Token.IsCancellationRequested)
                return "Error: wait cancelled (host tool timeout).";
            Thread.Sleep(100);
        }
        return $"Timeout after {timeoutMs} ms — still running or mixed. Last break: {LastBreak}";
    }

    internal static string SetBreakpoint(string method, string? assembly, int ilOffset) {
        var bps = _breakpoints;
        if (_manager is null || bps is null || _locations is null || _resolver is null)
            return Unavailable;
        if (_moduleIdProvider is null)
            return "Error: IModuleIdProvider not available (cannot build the engine-side module id).";
        var asmHint = string.IsNullOrWhiteSpace(assembly) ? null : assembly;
        var methodDef = _resolver.ResolveMethodFlexible(method, asmHint);
        if (methodDef is null)
            return $"Error: method not found: {method}{(asmHint != null ? $" in {asmHint}" : "")} — load the debuggee assembly first (load_assembly).";
        // Same guards as dnSpy's own MethodBreakpointsService: no BP on bodiless methods.
        if (methodDef.IsAbstract || methodDef.Body is null)
            return $"Error: {methodDef.FullName} has no method body (abstract/extern) — nothing to break on.";
        if (ilOffset < 0)
            return $"Error: il_offset must be >= 0 (got {ilOffset}).";
        // Canonical construction (dnSpy MethodBreakpointsService.Add): ModuleId MUST come
        // from IModuleIdProvider — ModuleId.CreateFromFile never matches the engine-side
        // module comparison. Location + Add run on the UI (debug dispatcher) thread: these
        // objects carry dispatcher affinity; creating them off-thread breaks binding.
        var token = methodDef.MDToken.Raw;
        var module = methodDef.Module;
        var id = Interlocked.Increment(ref _nextBpId);
        UIVoid(() => {
            var moduleId = _moduleIdProvider.Create(module);
            // dnlib keeps ModuleDef.Name exactly as passed to load_assembly — a forward-slash
            // path (typical from AI clients) never equals the debuggee's backslash module
            // name, so the breakpoint would silently fail to bind. Normalize separators.
            if (moduleId.ModuleName?.Contains('/') == true) {
                moduleId = new ModuleId(
                    moduleId.AssemblyFullName,
                    moduleId.ModuleName.Replace('/', '\\'),
                    moduleId.IsDynamic, moduleId.IsInMemory, moduleId.ModuleNameOnly);
            }
            var location = _locations.Create(moduleId, token, (uint)ilOffset);
            var info = new DbgCodeBreakpointInfo(location, new DbgCodeBreakpointSettings { IsEnabled = true });
            // Add returns null when an identical-location breakpoint already exists
            // (dnSpy persists breakpoints across sessions) — reuse the live one and close
            // our unowned location (one owner per DbgCodeLocation instance).
            var added = bps.Add(info);
            if (added is null) {
                added = bps.TryGetBreakpoint(location);
                location.Close();
            }
            if (added != null) {
                added.Hit -= OnBreakpointHit;   // dedupe: never subscribe twice
                added.Hit += OnBreakpointHit;
                _bpById[id] = added;
                _bpLocationById[id] = (DbgDotNetCodeLocation)added.Location;
            }
            else {
                _bpById[id] = null!;
            }
            _bpLabelById[id] = $"{methodDef.FullName} @ IL_{ilOffset:X4}";
        });
        return $"Breakpoint {id}: {_bpLabelById[id]}";
    }

    static void OnBreakpointHit(object? sender, DbgBreakpointHitEventArgs e) {
        RecordBreak("breakpoint hit");
        McpLogger.Info("Debugger: breakpoint hit");
    }

    internal static string DeleteBreakpoint(int id) {
        var bps = _breakpoints;
        if (_manager is null || bps is null) return Unavailable;
        if (!_bpById.TryGetValue(id, out var bp) || bp is null) {
            _bpById.Remove(id);
            return $"Error: unknown breakpoint id {id} (see debug_list_breakpoints).";
        }
        UIVoid(() => bps.Remove(bp));
        _bpById.Remove(id);
        _bpLocationById.Remove(id);
        _bpLabelById.Remove(id);
        return $"Breakpoint {id} removed.";
    }

    internal static string ListBreakpoints() {
        var bps = _breakpoints;
        if (_manager is null || bps is null) return Unavailable;
        return UI(() => {
            // Presence by location equality, not reference: the service dedupes/wraps
            // breakpoints on the debug dispatcher, and Add is async on that dispatcher.
            var liveLocations = new HashSet<DbgDotNetCodeLocation>(
                bps.Breakpoints.Select(b => b.Location).OfType<DbgDotNetCodeLocation>());
            var sb = new StringBuilder();
            foreach (var kv in _bpById.OrderBy(k => k.Key)) {
                var stillThere = _bpLocationById.TryGetValue(kv.Key, out var loc) &&
                                 loc != null && liveLocations.Contains(loc);
                int bound;
                try { bound = kv.Value!.BoundBreakpoints?.Length ?? -1; }
                catch { bound = -2; }
                sb.AppendLine($"[{kv.Key}] {_bpLabelById[kv.Key]} | bound={bound}{(stillThere ? "" : " | removed in dnSpy UI")}");
            }
            if (sb.Length == 0)
                sb.AppendLine("No MCP breakpoints. (dnSpy GUI-created breakpoints are managed in the Breakpoints window.)");
            return sb.ToString();
        });
    }

    internal static string GetCallstack(int maxFrames, int? pid, ulong? threadId) =>
        DnSpyValueService.GetCallstack(maxFrames, pid, threadId);

}
