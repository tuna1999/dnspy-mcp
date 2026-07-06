using System.Text.Json.Nodes;
using dnSpy.MCP.Mcp;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

public class JsonRpcTests {
    [Fact]
    public void CreateResponse_sets_jsonrpc_version_and_result() {
        var result = JsonNode.Parse("\"ok\"")!;

        var response = JsonRpc.CreateResponse(42, result);

        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        response["result"]!.GetValue<string>().Should().Be("ok");
        response["id"]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void CreateResponse_with_null_id_yields_notification() {
        var result = JsonNode.Parse("{}")!;

        var response = JsonRpc.CreateResponse(null, result);

        response.ContainsKey("id").Should().BeFalse("notifications omit the id field");
        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
    }

    [Fact]
    public void CreateResponse_deep_clones_id_so_mutations_do_not_leak() {
        var id = new JsonObject { ["seq"] = 1 };
        var response = JsonRpc.CreateResponse(id, JsonNode.Parse("\"ok\"")!);

        id["seq"] = 999; // mutate original after build

        response["id"]!["seq"]!.GetValue<int>().Should().Be(1, "the response id is a deep clone");
    }

    [Fact]
    public void MakeError_sets_error_code_and_message() {
        var response = JsonRpc.MakeError(null, -32700, "Parse error");

        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        var err = response["error"]!;
        err["code"]!.GetValue<int>().Should().Be(-32700);
        err["message"]!.GetValue<string>().Should().Be("Parse error");
    }

    [Fact]
    public void CreateServerCapabilities_reports_protocol_version_and_tool_capability() {
        var caps = JsonRpc.CreateServerCapabilities();

        caps["protocolVersion"]!.GetValue<string>().Should().Be("2024-11-05");
        caps["capabilities"]!["tools"]!.AsObject().Should().NotBeNull("tools capability must be advertised");
        var info = caps["serverInfo"]!;
        info["name"]!.GetValue<string>().Should().Be("dnSpy-MCP");
        info["version"]!.GetValue<string>().Should().NotBeNullOrEmpty("version comes from the executing assembly");
    }
}
