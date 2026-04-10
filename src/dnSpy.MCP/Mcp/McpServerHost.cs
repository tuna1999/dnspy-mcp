using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;

namespace dnSpy.MCP.Mcp {
    public sealed class McpServerHost : IDisposable {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly McpServerOptions _options;
        private readonly IDsDocumentService? _documentService;
        private readonly IDecompilerService? _decompilerService;
        private readonly ToolRegistry _registry;
        private bool _running;

        public bool IsRunning => _running;

        public McpServerHost(
            McpServerOptions options,
            IDsDocumentService? documentService,
            IDecompilerService? decompilerService) {
            _options = options;
            _documentService = documentService;
            _decompilerService = decompilerService;
            _registry = new ToolRegistry();
        }

        public async Task StartAsync() {
            if (_running) return;

            _cts = new CancellationTokenSource();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_options.Port}/");
            _listener.Start();
            _running = true;

            McpLogger.Info($"Server started on http://127.0.0.1:{_options.Port}/");
            McpLogger.Info($"Registered {(_registry.ListTools().Count)} tools");

            _ = Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening) {
                try {
                    var context = await _listener.GetContextAsync().WaitAsync(ct);
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (OperationCanceledException) {
                    break;
                }
                catch (HttpListenerException) {
                    break;
                }
                catch (ObjectDisposedException) {
                    break;
                }
            }
        }

        private async void HandleRequest(HttpListenerContext context) {
            var response = context.Response;
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            try {
                if (context.Request.HttpMethod == "OPTIONS") {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (context.Request.HttpMethod != "POST") {
                    response.StatusCode = 405;
                    WriteJson(response, JsonSerializer.Serialize(new { error = "Method not allowed" }));
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();

                // Handle JSON-RPC batch and single requests
                var results = new List<JsonNode?>();

                JsonNode? requestNode;
                try {
                    requestNode = JsonNode.Parse(body);
                }
                catch {
                    WriteError(response, -32700, "Parse error");
                    return;
                }

                JsonNode?[] requests;
                if (requestNode is JsonArray jsonArray) {
                    var list = new List<JsonNode?>();
                    for (int i = 0; i < jsonArray.Count; i++)
                        list.Add(jsonArray[i]);
                    requests = list.ToArray();
                }
                else {
                    requests = new[] { requestNode };
                }

                foreach (var req in requests) {
                    if (req == null) {
                        results.Add(MakeError(null, -32600, "Invalid Request"));
                        continue;
                    }

                    var method = req["method"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(method)) {
                        results.Add(MakeError(req["id"], -32600, "Invalid Request"));
                        continue;
                    }

                    var id = req["id"];
                    var isNotification = id == null;

                    if (method == "initialize") {
                        McpLogger.Info("Client initialized");
                        var result = CreateServerCapabilities();
                        results.Add(isNotification ? null : CreateResponse(id, result));
                    }
                    else if (method == "tools/list") {
                        McpLogger.Info("Client requested tool list");
                        var tools = _registry.ListTools();
                        var result = new JsonObject {
                            ["tools"] = JsonSerializer.SerializeToNode(tools)
                        };
                        results.Add(isNotification ? null : CreateResponse(id, result));
                    }
                    else if (method == "tools/call") {
                        var toolName = req["params"]?["name"]?.GetValue<string>() ?? "";
                        McpLogger.Info($"Tool call: {toolName}");
                        var callResult = HandleToolCall(req);
                        // HandleToolCall returns the full JSON-RPC response object
                        results.Add(isNotification ? null : callResult);
                    }
                    else if (method == "notifications/initialized" || method == "shutdown") {
                        results.Add(isNotification ? null : CreateResponse(id, new JsonObject()));
                    }
                    else {
                        McpLogger.Warn($"Unknown method: {method}");
                        results.Add(isNotification ? null : MakeError(id, -32601, $"Method not found: {method}"));
                    }
                }

                if (context.Request.HttpMethod == "POST") {
                    JsonNode? responseBody;
                    if (results.Count == 1) {
                        responseBody = results[0];
                    }
                    else {
                        var batch = new JsonArray();
                        foreach (var r in results)
                            batch.Add(r);
                        responseBody = batch;
                    }

                    if (responseBody == null) {
                        response.StatusCode = 204;
                        response.Close();
                    }
                    else {
                        WriteJson(response, responseBody.ToJsonString());
                    }
                }
            }
            catch (Exception ex) {
                McpLogger.Error(ex, "Request handler error");
                try { WriteError(response, -32603, $"Internal error: {ex.Message}"); } catch { }
            }
        }

        private JsonNode HandleToolCall(JsonNode request) {
            var toolName = request["params"]?["name"]?.GetValue<string>();
            var arguments = request["params"]?["arguments"] as JsonObject;

            if (string.IsNullOrEmpty(toolName)) {
                return MakeError(request["id"], -32602, "Missing tool name");
            }

            var tool = _registry.GetTool(toolName);
            if (tool == null) {
                return MakeError(request["id"], -32601, $"Unknown tool: {toolName}");
            }

            try {
                var result = tool.Invoke(arguments);
                var content = new JsonArray();
                content.Add(new JsonObject {
                    ["type"] = "text",
                    ["text"] = result
                });

                return CreateResponse(request["id"], new JsonObject {
                    ["content"] = content
                });
            }
            catch (Exception ex) {
                McpLogger.Error(ex, $"Tool '{toolName}'");
                return MakeError(request["id"], -32603, $"Tool execution failed: {ex.Message}");
            }
        }

        private static JsonObject CreateServerCapabilities() {
            return new JsonObject {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject {
                    ["name"] = "dnSpy-MCP",
                    ["version"] = "1.0.0"
                }
            };
        }

        private static JsonObject CreateResponse(JsonNode? id, JsonNode result) {
            var response = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };
            if (id != null)
                response["id"] = id.DeepClone();
            return response;
        }

        private static JsonObject MakeError(JsonNode? id, int code, string message) {
            var response = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["error"] = new JsonObject {
                    ["code"] = code,
                    ["message"] = message
                }
            };
            if (id != null)
                response["id"] = id.DeepClone();
            return response;
        }

        private static void WriteJson(HttpListenerResponse response, string json) {
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        private static void WriteError(HttpListenerResponse response, int code, string message) {
            response.StatusCode = 200;
            var err = MakeError(null, code, message);
            WriteJson(response, err.ToJsonString());
        }

        public void Stop() {
            if (!_running) return;

            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
            _running = false;
            McpLogger.Info("Server stopped");
        }

        public void Dispose() {
            Stop();
            _cts?.Dispose();
        }
    }
}
