using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Core.Tools;
using dnSpy.MCP.Settings;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Host-level execution-semantics tests that need REAL tools served over the real
/// TCP transport (stub tool classes below), covering paths McpServerHostGateTests
/// cannot reach with tools/list alone:
///   - Mutation serialization: two concurrent connections calling an update_-prefixed
///     tool must not interleave (the host's _mutationLock, whose release-on-completion
///     semantics differ from Headless's MutationGate).
///   - Tool timeout: WaitAsync(timeout) must return -32603 AND actually cancel the
///     in-flight work via ToolCallScope (regression: dropping ToolCallScope.Set
///     reintroduces the abandoned-decompile CPU burn with all tests green).
///   - TargetInvocationException unwrap: the client must see the tool's real error
///     message, not "Exception has been thrown by the target of an invocation."
/// </summary>
[Collection("mcp-logger-serial")]
public class McpServerHostToolExecutionTests {
    private sealed class StubLoader : IAssemblyLoader {
        public LoadResult Load(string path) => new(false, "stub", null);
        public int Close(string assemblyName) => 0;
        public IReadOnlyList<LoadedModule> GetDocuments() => Array.Empty<LoadedModule>();
    }
    private sealed class StubDecompiler : ISourceDecompiler {
        public string DecompileMethod(dnlib.DotNet.MethodDef method) => "";
        public string DecompileType(dnlib.DotNet.TypeDef type) => "";
        public string DecompileField(dnlib.DotNet.FieldDef field) => "";
        public string DecompileProperty(dnlib.DotNet.PropertyDef property) => "";
        public string DecompileEvent(dnlib.DotNet.EventDef ev) => "";
        public string DecompileModule(dnlib.DotNet.ModuleDef module) => "";
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

    private static (McpServerHost Host, int Port) StartServer(Action<McpSettings>? configure = null) {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var settings = new McpSettings { Host = "127.0.0.1", Port = port };
        configure?.Invoke(settings);
        // Registry from THIS assembly: picks up the stub tool class below instead of
        // the real Core tools, so journaling/timing stubs are actually invokable.
        var host = new McpServerHost(settings, new ToolRegistry(StubContext(), typeof(McpServerHostToolExecutionTests).Assembly));
        host.StartAsync().GetAwaiter().GetResult();
        return (host, port);
    }

    private static string ToolCallBody(string tool, string argsJson = "{}") =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argsJson}}}}}";

    private static async Task<string> SendAsync(int port, string body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        using var stream = client.GetStream();

        var request = Encoding.UTF8.GetBytes(
            $"POST / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(request, cts.Token);

        var buffer = new byte[8192];
        var response = new MemoryStream();
        while (true) {
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            await response.WriteAsync(buffer.AsMemory(0, read), cts.Token);
        }
        return Encoding.UTF8.GetString(response.ToArray());
    }

    // ---- Mutation serialization across concurrent connections ----

    [Fact]
    public async Task Concurrent_mutation_tools_serialize_via_host_lock() {
        StubTools.Journal.Clear();
        var (host, port) = StartServer();
        try {
            // Two connections calling the update_-prefixed stub concurrently. The host's
            // _mutationLock must serialize them: enter/exit pairs must not interleave.
            var t1 = Task.Run(() => SendAsync(port, ToolCallBody("update_slow_thing", "{\"ms\":400}")));
            var t2 = Task.Run(() => SendAsync(port, ToolCallBody("update_slow_thing", "{\"ms\":400}")));
            await Task.WhenAll(t1, t2);

            Assert.Contains("done", await t1);
            Assert.Contains("done", await t2);

            // Wait for the journal to settle (second exit happens after its response is
            // composed; give it a moment), then verify strict serialization.
            var sw = Stopwatch.StartNew();
            while (StubTools.Journal.Count < 4 && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);

            var events = StubTools.Journal.ToArray();
            Assert.Equal(4, events.Length);
            // Two (enter, exit) pairs — never enter, enter: that would mean the two
            // mutations ran concurrently and raced on shared metadata.
            Assert.Equal("enter", events[0].Event);
            Assert.Equal("exit", events[1].Event);
            Assert.Equal("enter", events[2].Event);
            Assert.Equal("exit", events[3].Event);
        }
        finally { host.Stop(); host.Dispose(); }
    }

    // ---- Tool timeout actually cancels in-flight work ----

    [Fact]
    public async Task Tool_timeout_returns_error_and_cancels_work() {
        StubTools.Journal.Clear();
        var (host, port) = StartServer(s => s.ToolTimeoutSeconds = 1);
        try {
            var response = await SendAsync(port, ToolCallBody("slow_cancellable"));
            Assert.Contains("-32603", response);
            Assert.Contains("timed out", response);

            // The response already went out with the timeout error; the abandoned tool
            // must still observe cancellation (ToolCallScope token) shortly after.
            var sw = Stopwatch.StartNew();
            while (!StubTools.WasCancelled && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);
            Assert.True(StubTools.WasCancelled,
                "tool kept running after the client got the timeout error — ToolCallScope cancellation not propagated");
        }
        finally { host.Stop(); host.Dispose(); }
    }

    // ---- TargetInvocationException is unwrapped for the client ----

    [Fact]
    public async Task Tool_exception_message_reaches_client_not_reflection_wrapper() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, ToolCallBody("throw_diagnostic"));
            Assert.Contains("boom-diagnostic-message", response);
            Assert.DoesNotContain("target of an invocation", response);
        }
        finally { host.Stop(); host.Dispose(); }
    }
}
