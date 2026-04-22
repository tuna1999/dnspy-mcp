using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
                    _ = HandleConnection(client).ContinueWith(_ => {
                        _concurrency.Release();
                        client.Dispose();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private async Task HandleConnection(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 30_000;
                stream.WriteTimeout = 30_000;

                // Read request line: "POST / HTTP/1.1\r\n"
                var requestLine = await ReadLineAsync(stream);
                if (requestLine == null) return;

                var spaceIdx = requestLine.IndexOf(' ');
                if (spaceIdx < 0) return;
                var method = requestLine.Substring(0, spaceIdx);

                // Read headers
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while ((headerLine = await ReadLineAsync(stream)) != null && headerLine.Length > 0)
                {
                    var colonIdx = headerLine.IndexOf(':');
                    if (colonIdx > 0)
                        headers[headerLine.Substring(0, colonIdx).Trim()] = headerLine.Substring(colonIdx + 1).Trim();
                }

                // CORS preflight
                if (method == "OPTIONS")
                {
                    await WriteResponseAsync(stream, 204, "No Content", null,
                        ("Access-Control-Allow-Origin", _settings.AllowedOrigins),
                        ("Access-Control-Allow-Methods", "POST, OPTIONS"),
                        ("Access-Control-Allow-Headers", "Content-Type"));
                    return;
                }

                // Auth check
                if (_settings.RequireAuth && !string.IsNullOrEmpty(_settings.ApiToken))
                {
                    headers.TryGetValue("Authorization", out var auth);
                    if (auth != $"Bearer {_settings.ApiToken}")
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
                    await stream.ReadExactlyAsync(buffer, 0, contentLength);
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
                        var callResult = HandleToolCall(req);
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

        private static async Task<string?> ReadLineAsync(Stream stream)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = stream.ReadByte();
                if (b == -1) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\r')
                {
                    var next = stream.ReadByte();
                    // consume \n after \r
                    _ = next;
                    break;
                }
                if (b == '\n') break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private async Task WriteJsonResponseAsync(Stream stream, int statusCode, object data)
        {
            var json = data is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(data);
            var body = Encoding.UTF8.GetBytes(json);

            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n");
            sb.Append("Content-Type: application/json\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
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

        private JsonNode HandleToolCall(JsonNode request)
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
                var result = tool.Invoke(arguments);
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
            catch (Exception ex)
            {
                McpLogger.Error(ex, $"Tool '{toolName}'");
                return MakeError(request["id"], -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        private static JsonObject CreateServerCapabilities()
        {
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
                    ["version"] = "1.2.0"
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

            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            _running = false;
            McpLogger.Info("Server stopped");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
