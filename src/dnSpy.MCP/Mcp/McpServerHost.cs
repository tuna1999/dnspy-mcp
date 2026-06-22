using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.MCP.Settings;

namespace dnSpy.MCP.Mcp
{
    public sealed class McpServerHost : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly McpSettings _settings;
        private readonly ToolRegistry _registry;
        private volatile bool _running;
        private readonly SemaphoreSlim _concurrency;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private int _activeConnections;
        private readonly TaskCompletionSource _stoppedTcs = new();

        /// <summary>
        /// Auth config snapshot taken at StartAsync. Auth must stay stable while the server
        /// runs — reading the mutable <see cref="McpSettings.ApiToken"/> per-request would let
        /// an in-flight settings edit (debounced 500ms save) race the comparison.
        /// </summary>
        private bool _authRequired;
        private byte[]? _authExpectedToken;

        /// <summary>
        /// Exclusive lock held by destructive tools (update_method_body, rename_*).
        /// dnlib ModuleDef metadata is not safe for concurrent writers, and MCP batches are
        /// processed in parallel — mutations must serialize to avoid corrupted metadata.
        /// </summary>
        private static readonly SemaphoreSlim _mutationLock = new(1, 1);

        public bool IsRunning => _running;

        public McpServerHost(McpSettings settings)
        {
            _settings = settings;
            _concurrency = new SemaphoreSlim(settings.MaxConcurrency);
            _registry = new ToolRegistry();
        }

        public async Task StartAsync()
        {
            if (_running) return;

            _cts = new CancellationTokenSource();

            var ipAddress = _settings.Host switch {
                "0.0.0.0" or "*" => IPAddress.Any,
                "127.0.0.1" or "localhost" => IPAddress.Loopback,
                _ => IPAddress.Parse(_settings.Host)
            };

            _listener = new TcpListener(ipAddress, _settings.Port);
            _listener.Start();

            McpLogger.Info($"Server started on http://{_settings.Host}:{_settings.Port}/");
            McpLogger.Info($"Registered {(_registry.ListTools().Count)} tools");

            _running = true;

            // Snapshot auth config so it stays stable for the server's lifetime. Reload requires restart.
            _authRequired = _settings.RequireAuth;
            _authExpectedToken = _authRequired && !string.IsNullOrEmpty(_settings.ApiToken)
                ? Encoding.UTF8.GetBytes("Bearer " + _settings.ApiToken)
                : null;

            // Fail-closed: if auth is requested but no token is configured, we CANNOT serve
            // requests safely — refuse to start instead of silently disabling protection.
            if (_authRequired && _authExpectedToken == null)
                throw new InvalidOperationException(
                    "RequireAuth is enabled but ApiToken is empty. Set a token in MCP Settings or disable RequireAuth.");

            _ = Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _running)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync().WaitAsync(ct);
                    await _concurrency.WaitAsync(ct);
                    Interlocked.Increment(ref _activeConnections);
                    _ = Task.Run(async () => {
                        try { await HandleConnection(client, _cts.Token); }
                        finally { _concurrency.Release(); client.Dispose(); }
                    }).ContinueWith(_ => {
                        if (Interlocked.Decrement(ref _activeConnections) == 0 && !_running)
                            _stoppedTcs.TrySetResult();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private async Task HandleConnection(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 30_000;
                stream.WriteTimeout = 30_000;
                var reader = new BufferedLineReader(stream);

                // Read request line: "POST / HTTP/1.1\r\n"
                var requestLine = await reader.ReadLineAsync(ct);
                if (requestLine == null) return;

                var spaceIdx = requestLine.IndexOf(' ');
                if (spaceIdx < 0) return;
                var method = requestLine.Substring(0, spaceIdx);

                // Read headers
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while ((headerLine = await reader.ReadLineAsync(ct)) != null && headerLine.Length > 0)
                {
                    var colonIdx = headerLine.IndexOf(':');
                    if (colonIdx > 0)
                        headers[headerLine.Substring(0, colonIdx).Trim()] = headerLine.Substring(colonIdx + 1).Trim();
                }

                // Health check endpoint
                var requestPath = requestLine.Substring(spaceIdx + 1);
                var queryIdx = requestPath.IndexOf(' ');
                var path = queryIdx > 0 ? requestPath.Substring(0, queryIdx) : requestPath.Trim();

                if (method == "GET" && (path == "/health" || path == "/ping"))
                {
                    var health = new JsonObject
                    {
                        ["status"] = "healthy",
                        ["port"] = _settings.Port,
                        ["uptime_seconds"] = (long)_uptime.Elapsed.TotalSeconds,
                        ["tools_count"] = _registry.ListTools().Count,
                        ["active_connections"] = _activeConnections
                    };
                    await WriteJsonResponseAsync(stream, 200, health);
                    return;
                }

                // CORS preflight
                if (method == "OPTIONS")
                {
                    var preflightHeaders = new List<(string, string)> {
                        ("Access-Control-Allow-Methods", "POST, OPTIONS"),
                        ("Access-Control-Allow-Headers", "Content-Type"),
                    };
                    if (!string.IsNullOrWhiteSpace(_settings.AllowedOrigins))
                        preflightHeaders.Insert(0, ("Access-Control-Allow-Origin", _settings.AllowedOrigins));
                    await WriteResponseAsync(stream, 204, "No Content", null, preflightHeaders.ToArray());
                    return;
                }

                // Auth check (fail-closed: snapshot taken at StartAsync).
                // Constant-time compare to avoid leaking token length/prefix via timing.
                if (_authRequired)
                {
                    headers.TryGetValue("Authorization", out var auth);
                    var provided = auth is null ? null : Encoding.UTF8.GetBytes(auth);
                    var authorized = provided != null && _authExpectedToken != null
                        && provided.Length == _authExpectedToken.Length
                        && CryptographicOperations.FixedTimeEquals(provided, _authExpectedToken);
                    if (!authorized)
                    {
                        await WriteJsonResponseAsync(stream, 401, new { error = "Unauthorized" });
                        return;
                    }
                }

                // Size check
                var contentLength = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) ? cl : 0;
                var maxBytes = (long)_settings.MaxRequestSizeMB * 1024 * 1024;
                if (contentLength > maxBytes)
                {
                    await WriteJsonResponseAsync(stream, 413, new { error = $"Payload too large (max {_settings.MaxRequestSizeMB}MB)" });
                    return;
                }

                if (method != "POST")
                {
                    await WriteJsonResponseAsync(stream, 405, new { error = "Method not allowed" });
                    return;
                }

                // Read body
                string body;
                if (contentLength > 0)
                {
                    var buffer = new byte[contentLength];
                    await reader.ReadExactlyAsync(buffer, 0, contentLength, ct);
                    body = Encoding.UTF8.GetString(buffer);
                }
                else
                {
                    body = string.Empty;
                }

                // Process JSON-RPC
                var results = new List<JsonNode?>();

                JsonNode? requestNode;
                try
                {
                    requestNode = JsonNode.Parse(body);
                }
                catch
                {
                    await WriteJsonResponseAsync(stream, 200, MakeError(null, -32700, "Parse error"));
                    return;
                }

                JsonNode?[] requests;
                if (requestNode is JsonArray jsonArray)
                {
                    var list = new List<JsonNode?>();
                    for (int i = 0; i < jsonArray.Count; i++)
                        list.Add(jsonArray[i]);
                    requests = list.ToArray();
                }
                else
                {
                    requests = new[] { requestNode };
                }

                foreach (var req in requests)
                {
                    if (req == null)
                    {
                        results.Add(MakeError(null, -32600, "Invalid Request"));
                        continue;
                    }

                    var rpcMethod = req["method"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(rpcMethod))
                    {
                        results.Add(MakeError(req["id"], -32600, "Invalid Request"));
                        continue;
                    }

                    var id = req["id"];
                    var isNotification = id == null;

                    if (rpcMethod == "initialize")
                    {
                        McpLogger.Info("Client initialized");
                        results.Add(isNotification ? null : CreateResponse(id, CreateServerCapabilities()));
                    }
                    else if (rpcMethod == "tools/list")
                    {
                        McpLogger.Info("Client requested tool list");
                        var tools = _registry.ListTools();
                        var result = new JsonObject
                        {
                            ["tools"] = JsonSerializer.SerializeToNode(tools)
                        };
                        results.Add(isNotification ? null : CreateResponse(id, result));
                    }
                    else if (rpcMethod == "tools/call")
                    {
                        var toolName = req["params"]?["name"]?.GetValue<string>() ?? "";
                        McpLogger.Info($"Tool call: {toolName}");
                        var callResult = await HandleToolCallAsync(req);
                        results.Add(isNotification ? null : callResult);
                    }
                    else if (rpcMethod == "notifications/initialized" || rpcMethod == "shutdown")
                    {
                        results.Add(isNotification ? null : CreateResponse(id, new JsonObject()));
                    }
                    else
                    {
                        McpLogger.Warn($"Unknown method: {rpcMethod}");
                        results.Add(isNotification ? null : MakeError(id, -32601, $"Method not found: {rpcMethod}"));
                    }
                }

                JsonNode? responseBody;
                if (results.Count == 1)
                {
                    responseBody = results[0];
                }
                else
                {
                    var batch = new JsonArray();
                    foreach (var r in results)
                        batch.Add(r);
                    responseBody = batch;
                }

                if (responseBody == null)
                {
                    await WriteResponseAsync(stream, 204, "No Content", null);
                }
                else
                {
                    await WriteJsonResponseAsync(stream, 200, responseBody);
                }
            }
            catch (Exception ex)
            {
                McpLogger.Error(ex, "Connection handler error");
            }
        }

        /// <summary>
        /// Buffered reader that preserves unconsumed bytes across calls.
        /// Fixes the bug where a 256-byte buffer consumed data beyond headers
        /// into the body, then that buffered body data was discarded.
        /// </summary>
        private sealed class BufferedLineReader
        {
            private readonly Stream _stream;
            private readonly byte[] _buf = new byte[4096];
            private int _bufPos, _bufLen;

            public BufferedLineReader(Stream stream) => _stream = stream;

            public async Task<string?> ReadLineAsync(CancellationToken ct = default)
            {
                var sb = new StringBuilder(256);

                while (true)
                {
                    if (_bufPos >= _bufLen)
                    {
                        _bufLen = await _stream.ReadAsync(_buf, 0, _buf.Length, ct);
                        if (_bufLen == 0) return sb.Length > 0 ? sb.ToString() : null;
                        _bufPos = 0;
                    }

                    var b = _buf[_bufPos++];
                    if (b == '\r')
                    {
                        // consume \n after \r
                        if (_bufPos >= _bufLen)
                        {
                            _bufLen = await _stream.ReadAsync(_buf, 0, _buf.Length, ct);
                            _bufPos = 0;
                        }
                        if (_bufLen > 0 && _buf[_bufPos] == '\n') _bufPos++;
                        break;
                    }
                    if (b == '\n') break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            /// <summary>
            /// Reads exactly <paramref name="count"/> bytes, draining the internal
            /// buffer first before reading from the underlying stream.
            /// </summary>
            public async Task ReadExactlyAsync(byte[] dest, int offset, int count, CancellationToken ct = default)
            {
                // Drain buffered bytes first
                var buffered = Math.Min(count, _bufLen - _bufPos);
                if (buffered > 0)
                {
                    Array.Copy(_buf, _bufPos, dest, offset, buffered);
                    _bufPos += buffered;
                    offset += buffered;
                    count -= buffered;
                }

                // Read remaining directly from stream
                if (count > 0)
                    await _stream.ReadAtLeastAsync(new Memory<byte>(dest, offset, count), count, cancellationToken: ct, throwOnEndOfStream: true);
            }
        }

        private async Task WriteJsonResponseAsync(Stream stream, int statusCode, object data)
        {
            var json = data is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(data);
            var body = Encoding.UTF8.GetBytes(json);

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
            // Omit CORS header entirely when no origins are configured — emitting an empty
            // ACAO would be equivalent to allowing nobody, but a missing header is unambiguous.
            if (!string.IsNullOrWhiteSpace(_settings.AllowedOrigins))
                sb.Append($"Access-Control-Allow-Origin: {_settings.AllowedOrigins}\r\n");
            sb.Append("\r\n");

            var header = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }

        private static async Task WriteResponseAsync(Stream stream, int statusCode, string reason, byte[]? body, params (string name, string value)[] headers)
        {
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
            if (body != null)
                sb.Append($"Content-Length: {body.Length}\r\n");
            foreach (var (name, value) in headers)
                sb.Append($"{name}: {value}\r\n");
            sb.Append("\r\n");

            var header = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(header, 0, header.Length);
            if (body != null)
                await stream.WriteAsync(body, 0, body.Length);
        }

        private static string GetReasonPhrase(int code) => code switch
        {
            200 => "OK",
            204 => "No Content",
            401 => "Unauthorized",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            _ => "Unknown"
        };

        private async Task<JsonNode> HandleToolCallAsync(JsonNode request)
        {
            var toolName = request["params"]?["name"]?.GetValue<string>();
            var arguments = request["params"]?["arguments"] as JsonObject;

            if (string.IsNullOrEmpty(toolName))
            {
                return MakeError(request["id"], -32602, "Missing tool name");
            }

            var tool = _registry.GetTool(toolName);
            if (tool == null)
            {
                return MakeError(request["id"], -32601, $"Unknown tool: {toolName}");
            }

            try
            {
                var timeout = TimeSpan.FromSeconds(_settings.ToolTimeoutSeconds);

                // Destructive tools (patch/rename) must run under the mutation lock so concurrent
                // batch requests can't race on dnlib metadata. Read-only tools stay fully parallel.
                SemaphoreSlim? heldLock = null;
                if (tool.IsMutation)
                {
                    await _mutationLock.WaitAsync(timeout);
                    heldLock = _mutationLock; // assigned only after successful acquire
                }
                try
                {
                    var result = await Task.Run(() => tool.Invoke(arguments)).WaitAsync(timeout);

                    var content = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = result
                        }
                    };

                    return CreateResponse(request["id"], new JsonObject
                    {
                        ["content"] = content
                    });
                }
                finally
                {
                    heldLock?.Release();
                }
            }
            catch (TimeoutException)
            {
                McpLogger.Warn($"Tool '{toolName}' timed out after {_settings.ToolTimeoutSeconds}s");
                return MakeError(request["id"], -32603, $"Tool execution timed out after {_settings.ToolTimeoutSeconds} seconds");
            }
            catch (Exception ex)
            {
                McpLogger.Error(ex, $"Tool '{toolName}'");
                return MakeError(request["id"], -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        private static JsonObject CreateServerCapabilities()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            return new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "dnSpy-MCP",
                    ["version"] = version
                }
            };
        }

        private static JsonObject CreateResponse(JsonNode? id, JsonNode result)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };
            if (id != null)
                response["id"] = id.DeepClone();
            return response;
        }

        private static JsonObject MakeError(JsonNode? id, int code, string message)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
            if (id != null)
                response["id"] = id.DeepClone();
            return response;
        }

        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;

            // Best-effort graceful drain. Stop() may be called on the UI thread (menu command),
            // so we must NOT sync-block on it — that would freeze dnSpy. Fire-and-forget a short
            // grace window; in-flight connections self-close via their own finally/Dispose.
            if (_activeConnections > 0)
            {
                _ = Task.Run(async () => {
                    try
                    {
                        await _stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        McpLogger.Info("Server stopped (all connections drained)");
                    }
                    catch (TimeoutException)
                    {
                        McpLogger.Warn("Shutdown grace (3s) elapsed: some connections were not closed gracefully");
                    }
                    catch (OperationCanceledException) { }
                });
            }

            McpLogger.Info("Server stopped");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
