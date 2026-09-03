using System;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Core.Mcp;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Regression guard for H1 (Headless race): destructive tools must serialize so concurrent
/// batch requests can't corrupt shared dnlib metadata. MutationGate is the single point of
/// enforcement — both Extension (McpServerHost._mutationLock) and Headless (MCP SDK filter)
/// route through <see cref="ToolRegistry.IsMutationTool"/>, so testing here covers both.
/// </summary>
public class MutationGateTests {
    [Theory]
    [InlineData("rename_class", true)]
    [InlineData("rename_namespace", true)]
    [InlineData("rename_method", true)]
    [InlineData("update_method_body", true)]
    [InlineData("patch_method", true)]
    [InlineData("decompile_method", false)]
    [InlineData("search_types", false)]
    [InlineData("load_assembly", false)]
    public void IsMutationTool_matches_predicate(string toolName, bool expected) {
        Assert.Equal(expected, ToolRegistry.IsMutationTool(toolName));
    }

    [Fact]
    public async Task MutationGate_serializes_concurrent_mutation_calls() {
        var lock_ = new SemaphoreSlim(1, 1);
        int concurrent = 0;
        int maxConcurrent = 0;
        var counterLock = new object();

        async ValueTask<string> Work() {
            lock (counterLock) {
                concurrent++;
                if (concurrent > maxConcurrent) maxConcurrent = concurrent;
            }
            await Task.Delay(20);
            lock (counterLock) { concurrent--; }
            return "done";
        }

        // Fire 8 concurrent mutations: with the gate, only one runs at a time.
        var tasks = new Task<string>[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = MutationGate.RunAsync("rename_class", Work, lock_, CancellationToken.None).AsTask();

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(0, concurrent); // counter restored — Release() ran in finally
    }

    [Fact]
    public async Task MutationGate_runs_readonly_tools_in_parallel() {
        var lock_ = new SemaphoreSlim(1, 1);
        int concurrent = 0;
        int maxConcurrent = 0;
        var counterLock = new object();

        async ValueTask<string> Work() {
            lock (counterLock) {
                concurrent++;
                if (concurrent > maxConcurrent) maxConcurrent = concurrent;
            }
            await Task.Delay(30);
            lock (counterLock) { concurrent--; }
            return "ok";
        }

        // 6 concurrent reads on a non-mutation tool name — no serialization expected.
        var tasks = new Task<string>[6];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = MutationGate.RunAsync("decompile_method", Work, lock_, CancellationToken.None).AsTask();

        await Task.WhenAll(tasks);

        // At least 2 must overlap; otherwise we serialized read-only work for no reason.
        Assert.True(maxConcurrent >= 2, $"expected parallel reads, got max={maxConcurrent}");
        Assert.Equal(0, concurrent);
    }

    [Fact]
    public async Task MutationGate_releases_lock_when_work_throws() {
        var lock_ = new SemaphoreSlim(1, 1);

        async ValueTask<string> Boom() {
            await Task.Yield();
            throw new InvalidOperationException("simulated");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MutationGate.RunAsync("rename_class", Boom, lock_, CancellationToken.None).AsTask());

        // After the throw the lock MUST be released — a second call must not deadlock.
        var completed = MutationGate.RunAsync("rename_class",
            () => new ValueTask<string>("ok"), lock_, CancellationToken.None);
        Assert.Equal("ok", await completed.AsTask());
    }
}
