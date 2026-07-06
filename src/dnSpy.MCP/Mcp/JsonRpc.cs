using System.Reflection;
using System.Text.Json.Nodes;

namespace dnSpy.MCP.Mcp {
    /// <summary>
    /// JSON-RPC 2.0 protocol helpers — pure builders for responses, errors,
    /// and server capabilities. Stateless so they can be unit-tested in isolation.
    /// </summary>
    internal static class JsonRpc {
        /// <summary>
        /// Builds the server's <c>initialize</c> response describing protocol
        /// version and tool capabilities. Version is read from the executing
        /// assembly (the MCP extension DLL), not the dnSpy host.
        /// </summary>
        public static JsonObject CreateServerCapabilities() {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            return new JsonObject {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject {
                    ["name"] = "dnSpy-MCP",
                    ["version"] = version
                }
            };
        }

        /// <summary>Builds a JSON-RPC 2.0 success response. Null id yields a notification.</summary>
        public static JsonObject CreateResponse(JsonNode? id, JsonNode result) {
            var response = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };
            if (id != null)
                response["id"] = id.DeepClone();
            return response;
        }

        /// <summary>Builds a JSON-RPC 2.0 error response.</summary>
        public static JsonObject MakeError(JsonNode? id, int code, string message) {
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
    }
}
