using System;
using System.Reflection;
using System.Text.Json.Nodes;
using dnSpy.MCP.Core.Mcp;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.MCP.Debugging;
using Xunit;

namespace dnSpy.MCP.Tests;

public class DebuggerValueTests {
    static readonly Func<JsonNode?, Type, string, object?> ConvertValue =
        typeof(ToolRegistry.ToolEntry).GetMethod("ConvertJsonValue", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<JsonNode?, Type, string, object?>>();
    [Fact]
    public void Thread_ids_preserve_unsigned_range_and_reject_negative_or_fractional_values() {
        Assert.Equal(ulong.MaxValue, ConvertValue(
            JsonNode.Parse("18446744073709551615"), typeof(ulong?), "threadId"));
        Assert.Equal(42UL, ConvertValue(
            JsonValue.Create(42), typeof(ulong?), "threadId"));
        Assert.Throws<ArgumentException>(() => ConvertValue(
            JsonNode.Parse("-1"), typeof(ulong?), "threadId"));
        Assert.Throws<ArgumentException>(() => ConvertValue(
            JsonNode.Parse("1.5"), typeof(ulong?), "threadId"));
    }

    [Theory]
    [InlineData("4294967297")]
    [InlineData("-4294967295")]
    [InlineData("1.5")]
    public void Frame_and_page_integers_cannot_wrap_or_drop_fractional_parts(string json) {
        Assert.Throws<ArgumentException>(() => ConvertValue(
            JsonNode.Parse(json), typeof(int), "frameIndex"));
    }

    [Fact]
    public void Page_boundaries_do_not_wrap_or_read_past_the_end() {
        Assert.Equal((8UL, 2), DnSpyValueService.GetPageRange(10, 8, 64));
        Assert.Equal((10UL, 0), DnSpyValueService.GetPageRange(10, 10, 64));
        Assert.Equal(((ulong)long.MaxValue, 64),
            DnSpyValueService.GetPageRange(ulong.MaxValue, long.MaxValue, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnSpyValueService.GetPageRange(10, -1, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnSpyValueService.GetPageRange(10, 11, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnSpyValueService.GetPageRange(10, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnSpyValueService.GetPageRange(10, 0, 257));
    }

    [Fact]
    public void Display_cap_preserves_unicode_and_reports_truncation() {
        var writer = new DnSpyValueService.BoundedTextWriter(4);
        writer.Write(DbgTextColor.Text, "a𝄞bc");
        writer.Write(DbgTextColor.Text, "more");
        Assert.Equal("a𝄞b", writer.Text);
        Assert.True(writer.Truncated);
        var split = new DnSpyValueService.BoundedTextWriter(2);
        split.Write(DbgTextColor.Text, "a𝄞bc");
        split.Write(DbgTextColor.Text, "z");
        Assert.Equal("a", split.Text);
        Assert.True(split.Truncated);
    }
}
