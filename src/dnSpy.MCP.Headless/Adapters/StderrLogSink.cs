using System;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless log sink writing to Console.Error. Stderr is used (not stdout) because
/// MCP stdio transport reserves stdout for JSON-RPC framing — any stray bytes there
/// corrupt the protocol.
/// </summary>
public sealed class StderrLogSink : ILogSink {
    private readonly object _gate = new();

    public void Info(string message) {
        lock (_gate) { Console.Error.WriteLine($"[INFO ] {message}"); }
    }

    public void Warn(string message) {
        lock (_gate) { Console.Error.WriteLine($"[WARN ] {message}"); }
    }

    public void Error(string message, Exception? ex = null) {
        lock (_gate) {
            Console.Error.WriteLine($"[ERROR] {message}");
            if (ex is not null)
                Console.Error.WriteLine(ex.ToString());
        }
    }
}
