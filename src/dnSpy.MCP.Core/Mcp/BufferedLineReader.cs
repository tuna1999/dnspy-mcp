using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.MCP.Core.Mcp {
    /// <summary>
    /// Buffered reader that preserves unconsumed bytes across calls.
    /// Fixes the bug where a 256-byte buffer consumed data beyond headers
    /// into the body, then that buffered body data was discarded.
    /// </summary>
    public sealed class BufferedLineReader {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[4096];
        private int _bufPos, _bufLen;

        public BufferedLineReader(Stream stream) => _stream = stream;

        public async Task<string?> ReadLineAsync(CancellationToken ct = default) {
            var sb = new StringBuilder(256);

            while (true) {
                if (_bufPos >= _bufLen) {
                    _bufLen = await _stream.ReadAsync(_buf, 0, _buf.Length, ct);
                    if (_bufLen == 0) return sb.Length > 0 ? sb.ToString() : null;
                    _bufPos = 0;
                }

                var b = _buf[_bufPos++];
                if (b == '\r') {
                    // consume \n after \r
                    if (_bufPos >= _bufLen) {
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
        public async Task ReadExactlyAsync(byte[] dest, int offset, int count, CancellationToken ct = default) {
            // Drain buffered bytes first
            var buffered = Math.Min(count, _bufLen - _bufPos);
            if (buffered > 0) {
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
}
