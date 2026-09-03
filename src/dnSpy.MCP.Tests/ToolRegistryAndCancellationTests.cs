using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Regression guards for the two bugs found while investigating the 30s decompile timeout:
///
/// 1. Tool discovery regression: tools moved to namespace dnSpy.MCP.Core.Tools but
///    IsToolClass only accepted dnSpy.MCP.Tools* — BOTH hosts silently registered 0 tools.
///    (verify-tool-count.ps1 missed it: it scans directories, not the runtime namespace filter.)
///
/// 2. Timeout leak: WaitAsync(timeout) abandons a still-running decompile. ToolCallScope +
///    DecompilationContext.CancellationToken make the timeout actually cancel the work.
/// </summary>
public class ToolRegistryAndCancellationTests {
    private sealed class StubLoader : IAssemblyLoader {
        public LoadResult Load(string path) => new(false, "stub", null);
        public int Close(string assemblyName) => 0;
        public IReadOnlyList<LoadedModule> GetDocuments() => Array.Empty<LoadedModule>();
    }

    private sealed class StubDecompiler : ISourceDecompiler {
        public string DecompileMethod(MethodDef method) => "";
        public string DecompileType(TypeDef type) => "";
        public string DecompileField(FieldDef field) => "";
        public string DecompileProperty(PropertyDef property) => "";
        public string DecompileEvent(EventDef ev) => "";
        public string DecompileModule(ModuleDef module) => "";
    }

    private sealed class StubUi : IUIThreadScheduler {
        public T Invoke<T>(Func<T> action) => action();
        public void Invoke(Action action) => action();
    }
    private sealed class StubLog : ILogSink {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
    private sealed class StubRefresh : ITreeRefreshNotifier {
        public void RefreshAll() { }
        public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
    }

    private static McpContext StubContext() => new(
        new StubLoader(), new StubDecompiler(), new StubUi(), new StubLog(), new StubRefresh());

    [Fact]
    public void ToolRegistry_discovers_Core_tools() {
        var registry = new ToolRegistry(StubContext(), typeof(McpContext).Assembly);

        var names = registry.ListTools()
            .Select(t => (string)t.GetType().GetProperty("name")!.GetValue(t)!)
            .ToList();

        Assert.Contains("decompile_method", names);
        Assert.Contains("load_assembly", names);
        Assert.True(names.Count >= 36, $"expected >=36 Core tools, got {names.Count}: {string.Join(",", names)}");
    }

    [Fact]
    public void ToolCallScope_token_flows_into_TaskRun() {
        using var cts = new CancellationTokenSource();
        ToolCallScope.Set(cts.Token);
        try {
            CancellationToken seen = CancellationToken.None;
            Task.Run(() => seen = ToolCallScope.Token).Wait(5000);
            Assert.Equal(cts.Token, seen);
        }
        finally {
            ToolCallScope.Set(CancellationToken.None);
        }
        Assert.Equal(CancellationToken.None, ToolCallScope.Token);
    }

    [Fact]
    public async Task ToolCallScope_cancellation_aborts_waiting_work() {
        // Same shape as McpServerHost: WaitAsync gives up at 100ms, ambient token cancels
        // the abandoned work at 200ms instead of letting it run to completion.
        using var cts = new CancellationTokenSource(200);
        ToolCallScope.Set(cts.Token);
        try {
            var t = Task.Run(async () => {
                while (!ToolCallScope.Token.IsCancellationRequested)
                    await Task.Delay(10);
                ToolCallScope.Token.ThrowIfCancellationRequested();
                return "finished";
            });
            await Assert.ThrowsAsync<TimeoutException>(() => t.WaitAsync(TimeSpan.FromMilliseconds(100)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        }
        finally {
            ToolCallScope.Set(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ToolCallScope_does_not_leak_across_concurrent_calls() {
        // Regression guard: AsyncLocal<T> values mutated inside Task.Run stay scoped to
        // that task's ExecutionContext — they don't bleed into sibling tasks or the
        // caller's context. This test catches a future regression where someone replaces
        // AsyncLocal with a plain static field (every call would see every other call's
        // token, breaking cancellation isolation in McpServerHost.HandleToolCallAsync).
        var tokens = new[] {
            new CancellationTokenSource().Token,
            new CancellationTokenSource().Token,
            new CancellationTokenSource().Token,
        };
        var observed = new CancellationToken?[tokens.Length];

        var tasks = new Task[tokens.Length];
        for (int i = 0; i < tokens.Length; i++) {
            int idx = i;
            tasks[idx] = Task.Run(async () => {
                ToolCallScope.Set(tokens[idx]);
                // Yield long enough that sibling tasks also reach Set() and read Token().
                await Task.Delay(30);
                observed[idx] = ToolCallScope.Token;
                ToolCallScope.Set(CancellationToken.None);
            });
        }
        await Task.WhenAll(tasks);

        // Each task observed its own token, not any other's — proves AsyncLocal isolation.
        for (int i = 0; i < tokens.Length; i++)
            Assert.Equal(tokens[i], observed[i]);

        // Caller's context (test method's ExecutionContext) was never Set, so still None.
        Assert.Equal(CancellationToken.None, ToolCallScope.Token);
    }
}
