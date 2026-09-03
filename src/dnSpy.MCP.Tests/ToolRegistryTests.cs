using dnSpy.MCP.Mcp;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

public class ToolRegistryTests {
    [Theory]
    [InlineData("DecompileMethod", "decompile_method")]
    [InlineData("GetXrefsTo", "get_xrefs_to")]
    [InlineData("UpdateMethodBody", "update_method_body")]
    [InlineData("A", "a")]
    [InlineData("ABC", "abc")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("RefreshUI", "refresh_ui")]
    [InlineData("GetHTTPResponse", "get_http_response")]
    [InlineData("GetIlOpcodesFormatted", "get_il_opcodes_formatted")]
    public void ToSnakeCase_breaks_words_at_boundaries_not_every_uppercase(string input, string expected) {
        ToolRegistry.ToSnakeCase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("update_method_body", true)]
    [InlineData("rename_class", true)]
    [InlineData("rename_namespace", true)]
    [InlineData("decompile_method", false)]
    [InlineData("search_types", false)]
    [InlineData("get_xrefs_to", false)]
    public void IsMutationTool_detects_mutation_prefixes(string toolName, bool expected) {
        ToolRegistry.IsMutationTool(toolName).Should().Be(expected);
    }
}
