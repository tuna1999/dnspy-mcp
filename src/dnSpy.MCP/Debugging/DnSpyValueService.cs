using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Debugging;

internal static class DnSpyValueService {
    static DbgManager? _manager;
    static DbgLanguageService? _languages;
    static IUIThreadScheduler? _scheduler;
    // ponytail: one frame snapshot; add multiple only for a client needing simultaneous frames.
    static Snapshot? _snapshot;
    static long _generation;
    const int MaxNodes = 4096;
    const DbgValueNodeEvaluationOptions EvaluationOptions =
        DbgValueNodeEvaluationOptions.NoFuncEval | DbgValueNodeEvaluationOptions.RawView |
        DbgValueNodeEvaluationOptions.NoHideRoots;
    const DbgValueFormatterOptions ValueOptions =
        DbgValueFormatterOptions.Decimal | DbgValueFormatterOptions.Namespaces |
        DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.NoDebuggerDisplay;
    const DbgValueFormatterTypeOptions TypeOptions =
        DbgValueFormatterTypeOptions.Decimal | DbgValueFormatterTypeOptions.Namespaces |
        DbgValueFormatterTypeOptions.IntrinsicTypeKeywords;

    internal static void Initialize(DbgManager? manager, DbgLanguageService? languages,
        IUIThreadScheduler scheduler) {
        _manager = manager;
        _languages = languages;
        _scheduler = scheduler;
    }

    static T UI<T>(Func<T> action) => (_scheduler ??
        throw new InvalidOperationException("Debugger value services are unavailable.")).Invoke(action);

    static DbgManager PausedManager() {
        var manager = _manager ?? throw new InvalidOperationException("Debugger services are unavailable.");
        if (!manager.IsDebugging || manager.IsRunning != false)
            throw new InvalidOperationException("Debugger must be fully paused.");
        ToolCallScope.Token.ThrowIfCancellationRequested();
        return manager;
    }

    static DbgThread SelectThread(int? pid, ulong? threadId) {
        if (threadId.HasValue && !pid.HasValue)
            throw new ArgumentException("thread_id requires pid.");
        var manager = PausedManager();
        var current = manager.CurrentThread.Current;
        if (!pid.HasValue)
            return current ?? throw new InvalidOperationException("No current thread.");
        var process = manager.Processes.FirstOrDefault(p => p.Id == pid.Value)
            ?? throw new ArgumentException("Unknown debuggee pid.");
        if (threadId.HasValue)
            return process.Runtimes.SelectMany(r => r.Threads).FirstOrDefault(t => t.Id == threadId.Value)
                ?? throw new ArgumentException("Unknown thread_id in this process.");
        if (current is null || current.Process != process)
            throw new ArgumentException("Specify thread_id for a process without the current thread.");
        return current;
    }

    internal static string ListThreads(int? pid) => UI(() => {
        var manager = PausedManager();
        var processes = manager.Processes;
        if (pid.HasValue) {
            var process = processes.FirstOrDefault(p => p.Id == pid.Value)
                ?? throw new ArgumentException("Unknown debuggee pid.");
            processes = new[] { process };
        }
        var current = manager.CurrentThread.Current;
        return JsonSerializer.Serialize(new {
            threads = processes.SelectMany(p => p.Runtimes.SelectMany(r => r.Threads.Select(t => new {
                pid = p.Id, thread_id = t.Id, managed_id = t.ManagedId, name = t.UIName,
                runtime = r.Name, is_current = t == current,
            }))).ToArray(),
        });
    });

    internal static string GetCallstack(int maxFrames, int? pid, ulong? threadId) => UI(() => {
        if (maxFrames < 1 || maxFrames > 256)
            throw new ArgumentOutOfRangeException(nameof(maxFrames), "max_frames must be 1–256.");
        var thread = SelectThread(pid, threadId);
        var frames = thread.GetFrames(maxFrames);
        try {
            var text = new StringBuilder();
            text.AppendLine($"Process [{thread.Process.Id}] Thread [{thread.Id}]");
            for (int i = 0; i < frames.Length; i++) {
                ToolCallScope.Token.ThrowIfCancellationRequested();
                text.AppendLine($"#{i} {FormatFrame(frames[i])}");
            }
            PausedManager();
            if (frames.Any(f => f.IsClosed))
                throw new InvalidOperationException("Pause ended while reading the call stack.");
            return text.ToString();
        }
        finally { _manager!.Close(frames); }
    });

    static string FormatFrame(DbgStackFrame frame) {
        var fallback = $"{frame.Module?.Name ?? "?"}!token:0x{frame.FunctionToken:X8} + IL_0x{frame.FunctionOffset:X}";
        if (_languages is null || frame.Location is null) return fallback;
        DbgEvaluationContext? context = null;
        try {
            var language = _languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
            context = language.CreateContext(frame, DbgEvaluationContextOptions.NoMethodBody,
                cancellationToken: ToolCallScope.Token);
            var info = new DbgEvaluationInfo(context, frame, ToolCallScope.Token);
            var writer = new BoundedTextWriter(4096);
            language.Formatter.FormatFrame(info, writer,
                DbgStackFrameFormatterOptions.ModuleNames | DbgStackFrameFormatterOptions.DeclaringTypes |
                DbgStackFrameFormatterOptions.Namespaces | DbgStackFrameFormatterOptions.ParameterTypes |
                DbgStackFrameFormatterOptions.IP, DbgValueFormatterOptions.NoDebuggerDisplay,
                CultureInfo.InvariantCulture);
            return writer.Text + (writer.Truncated ? " … [truncated]" : "");
        }
        catch (OperationCanceledException) { throw; }
        catch { return fallback; }
        finally { context?.Close(); }
    }

    internal static string GetLocals(int? pid, ulong? threadId, int frameIndex,
        long startIndex, int count) => UI(() => {
        if (frameIndex < 0 || frameIndex > 255)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), "frame_index must be 0–255.");
        GetPageRange(ulong.MaxValue, startIndex, count);
        var thread = SelectThread(pid, threadId);
        var languages = _languages ?? throw new InvalidOperationException("Runtime language services are unavailable.");
        var frames = thread.GetFrames(frameIndex + 1);
        if (frames.Length <= frameIndex) {
            _manager!.Close(frames);
            throw new ArgumentException("frame_index is outside the selected thread's stack.");
        }
        var frame = frames[frameIndex];
        for (int i = 0; i < frames.Length; i++)
            if (i != frameIndex) frames[i].Close();
        DbgEvaluationContext? context = null;
        Snapshot? candidate = null;
        try {
            if (frame.Location is null || !frame.HasFunctionToken)
                throw new InvalidOperationException("Selected frame has no managed evaluation location.");
            var language = languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
            context = language.CreateContext(frame, DbgEvaluationContextOptions.None,
                cancellationToken: ToolCallScope.Token);
            candidate = new Snapshot(_manager!, context, frame, frameIndex,
                Interlocked.Increment(ref _generation));
            var roots = language.LocalsProvider.GetNodes(candidate.EvaluationInfo(), EvaluationOptions,
                DbgLocalsValueNodeEvaluationOptions.ShowRawLocals |
                DbgLocalsValueNodeEvaluationOptions.ShowCompilerGeneratedVariables |
                DbgLocalsValueNodeEvaluationOptions.ShowDecompilerGeneratedVariables);
            if (roots.Length > MaxNodes) {
                _manager!.Close(roots.Select(r => r.ValueNode));
                throw new InvalidOperationException("Snapshot exceeds 4096 retained values.");
            }
            candidate.Roots = new Entry[roots.Length];
            for (int i = 0; i < roots.Length; i++)
                candidate.Roots[i] = candidate.Add(roots[i].ValueNode, roots[i].Kind.ToString());
            candidate.Verify();
            var page = GetPageRange((ulong)roots.Length, startIndex, count);
            var values = new object[page.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = Describe(candidate, candidate.Roots[(int)page.Start + i]);
            var response = JsonSerializer.Serialize(new {
                snapshot_id = candidate.Id, pid = thread.Process.Id, thread_id = thread.Id,
                frame_index = frameIndex, module = frame.Module?.Name,
                function_token = $"0x{frame.FunctionToken:X8}", il_offset = frame.FunctionOffset,
                total_count = roots.Length, start_index = page.Start,
                next_index = page.Start + (ulong)values.Length, values,
                retained_count = candidate.Nodes.Count,
            });
            candidate.Verify();
            var previous = _snapshot;
            _snapshot = candidate;
            candidate = null;
            previous?.Close();
            return response;
        }
        finally {
            if (candidate is not null) candidate.Close();
            else if (_snapshot?.Frame != frame) {
                context?.Close();
                frame.Close();
            }
        }
    });

    internal static string GetValue(string valueId, long startIndex, int count) => UI(() => {
        GetPageRange(ulong.MaxValue, startIndex, count);
        var separator = valueId?.IndexOf(':') ?? -1;
        if (separator < 1 ||
            !long.TryParse(valueId!.AsSpan(0, separator), NumberStyles.None,
                CultureInfo.InvariantCulture, out var generation) ||
            !int.TryParse(valueId.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id))
            throw new ArgumentException("Malformed value_id.");
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Id != generation ||
            !snapshot.Nodes.TryGetValue(id, out var parent))
            throw new ArgumentException("Unknown or stale value_id; read locals again.");
        snapshot.Verify(parent.Node);
        // Refuse synthetic groups before any provider initialization, including child counts.
        if (IsBlocked(parent.Node))
            throw new InvalidOperationException("This group may execute target code and is blocked.");
        var info = snapshot.EvaluationInfo();
        var total = parent.Node.HasError || parent.Node.HasChildren == false
            ? 0UL : parent.Node.GetChildCount(info);
        var page = GetPageRange(total, startIndex, count);
        int missing = 0;
        for (int i = 0; i < page.Count; i++)
            if (!snapshot.Children.ContainsKey((id, page.Start + (ulong)i))) missing++;
        if (missing > MaxNodes - snapshot.Nodes.Count)
            throw new InvalidOperationException("Snapshot exceeds 4096 retained values; read locals again.");
        var pending = new List<(ulong Index, Entry Entry)>(missing);
        var entries = new Entry[page.Count];
        bool committed = false;
        try {
            for (int i = 0; i < entries.Length;) {
                var index = page.Start + (ulong)i;
                if (snapshot.Children.TryGetValue((id, index), out var cached)) {
                    entries[i++] = cached;
                    continue;
                }
                // ponytail: one child avoids dnSpy AggregateValueNodeProvider's boundary
                // underflow; batch only when the engine exposes/fixes that boundary.
                var children = parent.Node.GetChildren(info, index, 1, EvaluationOptions);
                // Own every returned object before validating the provider's page.
                for (int j = 0; j < children.Length; j++)
                    pending.Add((index + (ulong)j,
                        new Entry(snapshot.Nodes.Count + pending.Count + 1, children[j], "Member")));
                if (children.Length != 1)
                    throw new InvalidOperationException("Runtime provider returned an incomplete child page.");
                entries[i++] = pending[pending.Count - 1].Entry;
            }
            var values = new object[entries.Length];
            for (int i = 0; i < values.Length; i++) values[i] = Describe(snapshot, entries[i]);
            var response = JsonSerializer.Serialize(new {
                snapshot_id = snapshot.Id, pid = snapshot.Frame.Process.Id,
                thread_id = snapshot.Frame.Thread.Id, frame_index = snapshot.FrameIndex,
                module = snapshot.Frame.Module?.Name,
                function_token = $"0x{snapshot.Frame.FunctionToken:X8}",
                il_offset = snapshot.Frame.FunctionOffset,
                value = Describe(snapshot, parent), total_count = total,
                start_index = page.Start, next_index = page.Start + (ulong)values.Length, values,
                retained_count = snapshot.Nodes.Count + pending.Count,
            });
            snapshot.Verify(parent.Node);
            foreach (var child in pending) {
                snapshot.Nodes.Add(child.Entry.Id, child.Entry);
                snapshot.Children.Add((id, child.Index), child.Entry);
            }
            committed = true;
            return response;
        }
        finally {
            if (!committed) _manager!.Close(pending.Select(c => c.Entry.Node));
        }
    });


    static bool IsBlocked(DbgValueNode node) => node.ImageName is
        PredefinedDbgValueNodeImageNames.StaticMembers or
        PredefinedDbgValueNodeImageNames.ResultsView or
        PredefinedDbgValueNodeImageNames.DynamicView;

    static object Describe(Snapshot snapshot, Entry entry) {
        snapshot.Verify(entry.Node);
        var node = entry.Node;
        if (IsBlocked(node))
            return new { value_id = (string?)null, kind = entry.Kind, name = node.ImageName,
                blocked_reason = "Expansion may execute target code; unavailable in read-only mode.",
                expandable = false };
        var info = snapshot.EvaluationInfo();
        var name = new BoundedTextWriter(4096);
        var value = new BoundedTextWriter(4096);
        var actual = new BoundedTextWriter(4096);
        var expected = new BoundedTextWriter(4096);
        var error = new BoundedTextWriter(4096);
        node.FormatName(info, name, ValueOptions, CultureInfo.InvariantCulture);
        node.FormatExpectedType(info, expected, TypeOptions, ValueOptions, CultureInfo.InvariantCulture);
        node.FormatActualType(info, actual, TypeOptions, ValueOptions, CultureInfo.InvariantCulture);
        node.FormatValue(info, value, ValueOptions, CultureInfo.InvariantCulture);
        error.Write(DbgTextColor.Text, node.ErrorMessage);
        snapshot.Verify(node);
        return new {
            value_id = $"{snapshot.Id}:{entry.Id}",
            kind = node.ImageName == PredefinedDbgValueNodeImageNames.This ? "This" : entry.Kind,
            name = name.Text, expected_type = expected.Text, actual_type = actual.Text,
            display_value = node.HasError ? null : value.Text,
            error = node.HasError ? error.Text : null,
            expandable = !node.HasError && node.HasChildren != false,
            truncated = name.Truncated || value.Truncated || actual.Truncated ||
                expected.Truncated || error.Truncated,
        };
    }

    internal static void Dispose() {
        if (_scheduler is null) return;
        UI(() => {
            var snapshot = _snapshot;
            _snapshot = null;
            snapshot?.Close();
            return 0;
        });
    }

    sealed class Entry {
        internal readonly int Id;
        internal readonly DbgValueNode Node;
        internal readonly string Kind;
        internal Entry(int id, DbgValueNode node, string kind) {
            Id = id; Node = node; Kind = kind;
        }
    }

    sealed class Snapshot {
        internal readonly long Id;
        internal readonly DbgStackFrame Frame;
        internal readonly int FrameIndex;
        internal readonly Dictionary<int, Entry> Nodes = new();
        internal readonly Dictionary<(int Parent, ulong Index), Entry> Children = new();
        internal Entry[] Roots = Array.Empty<Entry>();
        readonly DbgManager _owner;
        readonly DbgEvaluationContext _context;
        readonly DbgObject _continuation;
        int _invalid;
        bool _closed;

        internal Snapshot(DbgManager owner, DbgEvaluationContext context,
            DbgStackFrame frame, int frameIndex, long id) {
            _owner = owner; _context = context; Frame = frame; FrameIndex = frameIndex; Id = id;
            // The getter swaps to a new continuation after resume. Capture this pause's object.
            _continuation = context.ContinueContext;
            _continuation.Closed += OnClosed;
            Frame.Closed += OnClosed;
        }

        internal DbgEvaluationInfo EvaluationInfo() => new(_context, Frame, ToolCallScope.Token);

        internal Entry Add(DbgValueNode node, string kind) {
            var entry = new Entry(Nodes.Count + 1, node, kind);
            Nodes.Add(entry.Id, entry);
            return entry;
        }

        internal void Verify(DbgValueNode? node = null) {
            PausedManager();
            if (Volatile.Read(ref _invalid) != 0 || _continuation.IsClosed ||
                _context.IsClosed || Frame.IsClosed || node?.IsClosed == true)
                throw new InvalidOperationException("Stale value snapshot: pause ended; read locals again.");
        }

        void OnClosed(object? sender, EventArgs args) {
            if (Interlocked.Exchange(ref _invalid, 1) != 0) return;
            // Engine events must never block on WPF. Ownership cleanup runs after this callback.
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    _scheduler?.Invoke(() => {
                        if (ReferenceEquals(_snapshot, this)) _snapshot = null;
                        Close();
                    });
                }
                catch { /* host dispatcher may already have shut down */ }
            });
        }

        internal void Close() {
            if (_closed) return;
            _closed = true;
            Interlocked.Exchange(ref _invalid, 1);
            _continuation.Closed -= OnClosed;
            Frame.Closed -= OnClosed;
            _owner.Close(Nodes.Values.Select(e => e.Node));
            Nodes.Clear();
            Children.Clear();
            Roots = Array.Empty<Entry>();
            EvaluationInfo().Close();
        }
    }

    internal static (ulong Start, int Count) GetPageRange(ulong total, long startIndex, int count) {
        if (startIndex < 0 || (ulong)startIndex > total)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (count < 1 || count > 256)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be 1–256.");
        var start = (ulong)startIndex;
        return (start, (int)Math.Min(total - start, (ulong)count));
    }

    internal sealed class BoundedTextWriter : IDbgTextWriter {
        readonly int _capacity;
        readonly StringBuilder _text = new();
        internal BoundedTextWriter(int capacity) {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }
        internal string Text => _text.Length != 0 && char.IsHighSurrogate(_text[_text.Length - 1])
            ? _text.ToString(0, _text.Length - 1) : _text.ToString();
        internal bool Truncated { get; private set; }
        public void Write(DbgTextColor color, string? text) {
            if (Truncated || string.IsNullOrEmpty(text)) return;
            int length = Math.Min(text.Length, _capacity - _text.Length);
            _text.Append(text, 0, length);
            if (length < text.Length) {
                Truncated = true;
                if (_text.Length != 0 && char.IsHighSurrogate(_text[_text.Length - 1]))
                    _text.Length--;
            }
        }
    }
}
