using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Settings;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Regression guards for the localhost drive-by / DNS-rebinding gate in
/// <see cref="McpServerHost"/> (Host + Origin validation).
///
/// Threat model (default install: loopback-bound, no auth):
///   1. Drive-by POST — any webpage can fire a CORS "simple request" POST (string
///      body =&gt; text/plain, no preflight) at 127.0.0.1:5150. Mutations (load_assembly,
///      rename_*, update_method_body) execute even though the response is unreadable.
///   2. DNS rebinding — a public hostname resolving to 127.0.0.1 makes the browser
///      treat the server as same-origin, so responses become READABLE.
///
/// Browsers always send Host, and attach Origin to cross-origin requests. Non-browser
/// clients (MCP clients, curl) send neither — the gate must be invisible to them.
/// </summary>
[Collection("mcp-logger-serial")]
public class McpServerHostGateTests {
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

    private const string ToolsListBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    /// <summary>Starts a server on a free loopback port. Tests in one class run
    /// sequentially, so the grab-release-reuse port dance cannot race.</summary>
    private static (McpServerHost Host, int Port) StartServer(Action<McpSettings>? configure = null) {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var settings = new McpSettings { Host = "127.0.0.1", Port = port };
        configure?.Invoke(settings);
        var host = new McpServerHost(settings, new ToolRegistry(StubContext(), typeof(McpContext).Assembly));
        host.StartAsync().GetAwaiter().GetResult();
        return (host, port);
    }

    private static async Task<string> SendAsync(int port, string method, string path,
        IReadOnlyDictionary<string, string>? headers = null, string? body = null) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        using var stream = client.GetStream();

        var sb = new StringBuilder();
        sb.Append($"{method} {path} HTTP/1.1\r\n");
        if (headers != null)
            foreach (var h in headers)
                sb.Append($"{h.Key}: {h.Value}\r\n");
        if (body != null)
            sb.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        if (body != null)
            sb.Append(body);

        var request = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(request, cts.Token);

        // Read until the server closes the connection (handler returns -> client disposed).
        var buffer = new byte[8192];
        var response = new MemoryStream();
        while (true) {
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            await response.WriteAsync(buffer.AsMemory(0, read), cts.Token);
        }
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static string StatusLine(string response) =>
        response.Substring(0, response.IndexOf('\r'));

    // ---- Non-browser clients (MCP clients, curl) are unaffected ----

    [Fact]
    public async Task Post_without_origin_or_host_headers_passes() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "POST", "/", body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
            Assert.Contains("decompile_method", response);   // tools actually served
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Theory]
    [InlineData("127.0.0.1")]     // with port appended by the test
    [InlineData("localhost")]
    public async Task Post_with_loopback_host_passes(string hostName) {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> { ["Host"] = $"{hostName}:{port}" },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Post_with_portless_loopback_host_passes() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> { ["Host"] = "localhost" },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    // ---- Drive-by POST: browser Origin on a cross-origin request ----

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://evil.example:8443")]
    [InlineData("null")]                       // sandboxed iframe / file:// page
    [InlineData("https://sub.localhost")]      // suffix of localhost is NOT localhost
    public async Task Cross_origin_post_is_rejected_403(string origin) {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> {
                    ["Host"] = $"127.0.0.1:{port}",
                    ["Origin"] = origin,
                },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 403", StatusLine(response));
            Assert.DoesNotContain("decompile_method", response);  // no tool data leaked
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Allowlisted_origin_passes() {
        var (host, port) = StartServer(s => s.AllowedOrigins = "https://app.example, https://other.example");
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> {
                    ["Host"] = $"127.0.0.1:{port}",
                    ["Origin"] = "https://app.example",
                },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
            Assert.Contains("decompile_method", response);
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Wildcard_allowed_origins_accepts_any_origin() {
        var (host, port) = StartServer(s => s.AllowedOrigins = "*");
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> {
                    ["Host"] = $"127.0.0.1:{port}",
                    ["Origin"] = "https://evil.example",
                },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    // ---- DNS rebinding: public hostname resolving to 127.0.0.1 ----

    [Theory]
    [InlineData("evil.example")]
    [InlineData("10.0.0.5")]        // non-loopback IP — also not the loopback origin
    public async Task Rebound_host_post_is_rejected_403(string hostName) {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "POST", "/",
                headers: new Dictionary<string, string> { ["Host"] = $"{hostName}:{port}" },
                body: ToolsListBody);
            Assert.StartsWith("HTTP/1.1 403", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Rebind_read_path_get_health_with_foreign_host_is_rejected_403() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "GET", "/health",
                headers: new Dictionary<string, string> { ["Host"] = $"evil.example:{port}" });
            Assert.StartsWith("HTTP/1.1 403", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Health_with_loopback_host_is_served() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "GET", "/health",
                headers: new Dictionary<string, string> { ["Host"] = $"127.0.0.1:{port}" });
            Assert.StartsWith("HTTP/1.1 200", StatusLine(response));
            Assert.Contains("healthy", response);
        }
        finally { host.Stop(); host.Dispose(); }
    }

    // ---- Preflight follows the same gate ----

    [Fact]
    public async Task Preflight_from_foreign_origin_is_rejected_403() {
        var (host, port) = StartServer();
        try {
            var response = await SendAsync(port, "OPTIONS", "/",
                headers: new Dictionary<string, string> {
                    ["Host"] = $"127.0.0.1:{port}",
                    ["Origin"] = "https://evil.example",
                    ["Access-Control-Request-Method"] = "POST",
                });
            Assert.StartsWith("HTTP/1.1 403", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }

    [Fact]
    public async Task Preflight_from_allowlisted_origin_is_served_204() {
        var (host, port) = StartServer(s => s.AllowedOrigins = "https://app.example");
        try {
            var response = await SendAsync(port, "OPTIONS", "/",
                headers: new Dictionary<string, string> {
                    ["Host"] = $"127.0.0.1:{port}",
                    ["Origin"] = "https://app.example",
                    ["Access-Control-Request-Method"] = "POST",
                });
            Assert.StartsWith("HTTP/1.1 204", StatusLine(response));
        }
        finally { host.Stop(); host.Dispose(); }
    }
}

/// <summary>xUnit collection: runs sequentially alongside McpLoggerTests. McpLogger is
/// process-wide static state — server start/requests log entries, which would race
/// the logger's file/queue growth assertions if these classes ran in parallel.</summary>
[CollectionDefinition("mcp-logger-serial")]
public class McpLoggerSerialCollection { }
