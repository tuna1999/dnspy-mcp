using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// End-to-end tests that spawn the headless stdio MCP server as a real child
/// process — the same way Claude Desktop / Cursor launch it — and drive the
/// JSON-RPC protocol over stdin/stdout. Covers initialize handshake, tool
/// discovery, and a real decompile round-trip on the SampleLibrary fixture.
/// </summary>
public class HeadlessE2ETests {
    private const int ResponseTimeoutMs = 60_000;

    /// <summary>
    /// Child-process wrapper around the headless server, spawned the same way
    /// an MCP client (Claude Desktop / Cursor) would spawn it. The server is
    /// run with `dotnet` from its own build output directory, where its
    /// runtimeconfig.json and deps.json live (running the bare DLL from the
    /// test output directory fails — those files are not copied by
    /// ProjectReference). stderr is captured to a ring buffer for failure
    /// diagnostics and drained continuously so the pipe never blocks the child.
    /// </summary>
    private sealed class HeadlessProcess : IDisposable {
        public Process Process { get; }
        private readonly Task _stderrDrain;
        private readonly System.Text.StringBuilder _stderrTail = new();

        public string StderrTail {
            get { lock (_stderrTail) return _stderrTail.ToString(); }
        }

        /// <summary>Locates the headless build output (same config as the test run).</summary>
        private static string FindHeadlessDir() {
            // <repo>/src/dnSpy.MCP.Tests/bin/<cfg>/net10.0-windows/ → 4 levels up = <repo>/src
            var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", ".."));
            foreach (var cfg in new[] { "Release", "Debug" }) {
                var dir = Path.Combine(srcDir, "dnSpy.MCP.Headless", "bin", cfg, "net10.0-windows");
                if (File.Exists(Path.Combine(dir, "dnspy-mcp-headless.dll")))
                    return dir;
            }
            throw new InvalidOperationException(
                "headless build output not found under src/dnSpy.MCP.Headless/bin — build the solution first");
        }

        public HeadlessProcess(params string[] extraArgs) {
            var headlessDir = FindHeadlessDir();
            var psi = new ProcessStartInfo("dotnet") {
                WorkingDirectory = headlessDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("dnspy-mcp-headless.dll");
            foreach (var arg in extraArgs)
                psi.ArgumentList.Add(arg);

            Process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to spawn dotnet");
            _stderrDrain = Task.Run(async () => {
                try {
                    var buf = new char[4096];
                    while (true) {
                        var n = await Process.StandardError.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) break;
                        lock (_stderrTail) {
                            _stderrTail.Append(buf, 0, n);
                            if (_stderrTail.Length > 4000)
                                _stderrTail.Remove(0, _stderrTail.Length - 4000);
                        }
                    }
                }
                catch { /* process killed */ }
            });
        }

        /// <summary>Sends a request and returns the first response with matching id.</summary>
        public async Task<JsonNode> RequestAsync(int id, string method, string paramsJson, int timeoutMs = ResponseTimeoutMs) {
            await Process.StandardInput.WriteLineAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}");
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true) {
                var line = await Process.StandardOutput.ReadLineAsync().WaitAsync(cts.Token);
                line.Should().NotBeNull(
                    "server exited or timed out before responding; stderr tail:{0}",
                    Environment.NewLine + StderrTail);
                var msg = JsonNode.Parse(line!)!;
                if (msg["id"] is { } msgId && msgId.GetValue<int>() == id)
                    return msg;
                // skip notifications / interleaved responses
            }
        }

        public Task NotifyAsync(string method) =>
            Process.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\"}}");

        public void Dispose() {
            try { Process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (!Process.WaitForExit(5000))
                Process.Dispose();
            _stderrDrain.Wait(5000);
        }
    }

    private static Task<JsonNode> InitializeAsync(HeadlessProcess hp) =>
        hp.RequestAsync(1, "initialize",
            "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}," +
            "\"clientInfo\":{\"name\":\"e2e-test\",\"version\":\"1.0\"}}");

    [Fact]
    public async Task Initialize_returns_server_info() {
        using var hp = new HeadlessProcess();
        var resp = await InitializeAsync(hp);
        resp["error"].Should().BeNull("initialize must not fail");
        resp["result"]!["serverInfo"]!["name"]!.GetValue<string>()
            .Should().Be("dnspy-mcp-headless");
    }

    [Fact]
    public async Task Tools_list_exposes_all_core_tools() {
        using var hp = new HeadlessProcess();
        await InitializeAsync(hp);
        await hp.NotifyAsync("notifications/initialized");

        var resp = await hp.RequestAsync(2, "tools/list", "{}");
        resp["error"].Should().BeNull();

        var names = resp["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToArray();

        // Core contributes exactly 36 tools; the 2 Extension-only UI tools
        // (get_selected_node, refresh_u_i) are intentionally NOT in headless.
        names.Should().HaveCount(36);
        names.Should().NotContain("get_selected_node", "UI tool must not exist in headless");
        foreach (var expected in new[] {
            "load_assembly", "close_assembly", "list_loaded_assemblies",
            "decompile_method", "decompile_type", "decompile_assembly",
            "search_types", "search_methods", "grep",
            "get_method_il", "get_type_hierarchy", "get_xrefs_to",
            "rename_class", "rename_method", "rename_namespace", "update_method_body"
        })
            names.Should().Contain(expected);
    }

    [Fact]
    public async Task Decompile_method_on_preloaded_sample_library() {
        using var hp = new HeadlessProcess("--load",
            Path.Combine(AppContext.BaseDirectory, "SampleLibrary.dll"));
        await InitializeAsync(hp);
        await hp.NotifyAsync("notifications/initialized");

        var resp = await hp.RequestAsync(2, "tools/call",
            "{\"name\":\"decompile_method\"," +
            "\"arguments\":{\"methodFullNameOrToken\":\"TestNS.TestClass::TestMethod\"}}");

        resp["error"].Should().BeNull("tools/call must not return a protocol error");
        resp["result"]!["isError"]?.GetValue<bool>().Should().NotBe(true);

        var text = string.Join("\n", resp["result"]!["content"]!.AsArray()
            .Select(c => c!["text"]?.GetValue<string>() ?? ""));
        text.Should().Contain("42", "TestMethod returns the constant 42");
    }
}
