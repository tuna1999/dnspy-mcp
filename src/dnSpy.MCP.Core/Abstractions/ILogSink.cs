using System;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Tool-facing logger. Extension: file + dnSpy Output Pane. Headless: stderr only.
/// </summary>
public interface ILogSink {
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
