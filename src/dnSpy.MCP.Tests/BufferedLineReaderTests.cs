using System.IO;
using System.Text;
using System.Threading.Tasks;
using dnSpy.MCP.Mcp;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

public class BufferedLineReaderTests {
    static Stream StreamOf(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task ReadLineAsync_returns_null_at_eof() {
        var reader = new BufferedLineReader(StreamOf(string.Empty));

        var line = await reader.ReadLineAsync();

        line.Should().BeNull();
    }

    [Theory]
    [InlineData("hello\r\nworld", "hello")]
    [InlineData("hello\nworld", "hello")]
    [InlineData("hello\r", "hello")]
    [InlineData("just text", "just text")]
    public async Task ReadLineAsync_handles_each_terminator(string input, string expectedFirstLine) {
        var reader = new BufferedLineReader(StreamOf(input));

        var line = await reader.ReadLineAsync();

        line.Should().Be(expectedFirstLine);
    }

    /// <summary>
    /// Regression: a 256-byte (or any fixed-buffer) reader that discarded unconsumed bytes
    /// would lose the start of the body when it sat in the same read chunk as the headers.
    /// This test frames a header + partial body in a single buffer and verifies the reader
    /// does NOT consume the body bytes while reading header lines.
    /// </summary>
    [Fact]
    public async Task ReadLineAsync_does_not_consume_body_bytes_after_headers() {
        var reader = new BufferedLineReader(StreamOf("POST / HTTP/1.1\r\nContent-Length: 4\r\n\r\nABCD"));

        // Read three header lines
        (await reader.ReadLineAsync()).Should().Be("POST / HTTP/1.1");
        (await reader.ReadLineAsync()).Should().Be("Content-Length: 4");
        (await reader.ReadLineAsync()).Should().Be(string.Empty); // blank line separating headers and body

        // The body should still be readable via ReadExactlyAsync — if buffer state was discarded,
        // this would either throw or return fewer than 4 bytes.
        var dest = new byte[4];
        await reader.ReadExactlyAsync(dest, 0, 4);
        Encoding.UTF8.GetString(dest).Should().Be("ABCD");
    }

    [Fact]
    public async Task ReadExactlyAsync_drains_buffered_bytes_first_then_reads_stream() {
        // Reader reads line "X\r\n" — the '\n' read pulls in trailing body bytes.
        var reader = new BufferedLineReader(StreamOf("X\r\nPADDING"));
        await reader.ReadLineAsync();

        var dest = new byte[7];
        await reader.ReadExactlyAsync(dest, 0, 7);

        Encoding.UTF8.GetString(dest).Should().Be("PADDING");
    }

    [Fact]
    public async Task ReadExactlyAsync_preserves_order_when_body_spans_buffer_boundary() {
        // Larger than the 4096 internal buffer so it forces multiple stream reads mid-line.
        var body = new string('A', 5000);
        var reader = new BufferedLineReader(StreamOf($"HDR\r\n\r\n{body}"));
        await reader.ReadLineAsync();
        await reader.ReadLineAsync();

        var dest = new byte[5000];
        await reader.ReadExactlyAsync(dest, 0, 5000);

        dest.Should().AllBeEquivalentTo((byte)'A');
    }
}
