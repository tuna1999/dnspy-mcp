using System;
using System.Text.Json.Nodes;
using dnSpy.MCP.Mcp;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Unit tests for <see cref="ToolRegistry.ToolEntry.ConvertJsonValue"/> — the JSON→CLR coercion
/// that runs before every tool invocation. This is the boundary between untrusted MCP input and
/// reflection-based <see cref="System.Reflection.MethodInfo.Invoke"/>, so its coercion and
/// rejection behavior matters for safety (see the no-coercion-fallback comment in ToolRegistry).
/// </summary>
public class ConvertJsonValueTests {
    // Shortcut: the method under test lives on the nested ToolEntry type.
    static object? Convert(JsonNode? node, Type targetType, string paramName = "p") =>
        ToolRegistry.ToolEntry.ConvertJsonValue(node, targetType, paramName);

    // -------------------------------------------------------------------------
    // String targets
    // -------------------------------------------------------------------------

    [Fact]
    public void Converts_json_string_to_string() {
        Convert(JsonNode.Parse("\"hello\""), typeof(string)).Should().Be("hello");
    }

    [Fact]
    public void Converts_json_int_to_string() {
        Convert(JsonNode.Parse("42"), typeof(string)).Should().Be("42");
    }

    [Fact]
    public void Converts_json_long_to_string() {
        Convert(JsonNode.Parse("9007199254740993"), typeof(string)).Should().Be("9007199254740993");
    }

    [Fact]
    public void Converts_json_double_to_string() {
        Convert(JsonNode.Parse("3.14"), typeof(string)).Should().Be("3.14");
    }

    // -------------------------------------------------------------------------
    // Integer targets
    // -------------------------------------------------------------------------

    [Fact]
    public void Converts_json_int_to_int() {
        Convert(JsonNode.Parse("42"), typeof(int)).Should().Be(42);
    }

    [Fact]
    public void Converts_json_long_to_int_with_narrowing_cast() {
        // long -> int uses an explicit cast (matches the switch arm in ConvertJsonValue).
        Convert(JsonNode.Parse("7"), typeof(int)).Should().Be(7);
    }

    [Fact]
    public void Converts_json_double_to_int_truncates() {
        // double 3.9 -> int 3 (explicit cast semantics).
        Convert(JsonNode.Parse("3.9"), typeof(int)).Should().Be(3);
    }

    [Fact]
    public void Converts_json_int_to_long() {
        Convert(JsonNode.Parse("42"), typeof(long)).Should().Be(42L);
    }

    // -------------------------------------------------------------------------
    // Bool, double, float
    // -------------------------------------------------------------------------

    [Fact]
    public void Converts_json_bool_to_bool() {
        Convert(JsonNode.Parse("true"), typeof(bool)).Should().Be(true);
        Convert(JsonNode.Parse("false"), typeof(bool)).Should().Be(false);
    }

    [Fact]
    public void Converts_json_double_to_double() {
        Convert(JsonNode.Parse("3.14"), typeof(double)).Should().Be(3.14);
    }

    [Fact]
    public void Converts_json_int_to_double() {
        Convert(JsonNode.Parse("5"), typeof(double)).Should().Be(5.0);
    }

    [Fact]
    public void Converts_json_double_to_float_with_narrowing() {
        Convert(JsonNode.Parse("3.14"), typeof(float)).Should().Be((float)3.14);
    }

    // -------------------------------------------------------------------------
    // Null
    // -------------------------------------------------------------------------

    [Fact]
    public void Returns_null_for_null_node() {
        Convert(null, typeof(string)).Should().BeNull();
        Convert(null, typeof(int?)).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Nullable wrappers
    // -------------------------------------------------------------------------

    [Fact]
    public void Handles_nullable_int_target() {
        Convert(JsonNode.Parse("42"), typeof(int?)).Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Rejection (no-coercion fallback — caller error must fail loudly)
    // -------------------------------------------------------------------------

    [Fact]
    public void Throws_on_string_value_for_int_target() {
        var act = () => Convert(JsonNode.Parse("\"hello\""), typeof(int));
        act.Should().Throw<ArgumentException>()
            .WithMessage("*expects Int32*");
    }

    [Fact]
    public void Throws_on_bool_value_for_int_target() {
        var act = () => Convert(JsonNode.Parse("true"), typeof(int));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_on_object_for_string_target() {
        // Objects/arrays fall through to the default throw branch.
        var act = () => Convert(JsonNode.Parse("{}"), typeof(string));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Error_message_includes_param_name() {
        var act = () => Convert(JsonNode.Parse("\"hello\""), typeof(int), "methodFullNameOrToken");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*'methodFullNameOrToken'*");
    }
}
