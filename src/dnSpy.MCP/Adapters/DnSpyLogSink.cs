using System;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Adapters;

/// <summary>
/// Extension log sink: forwards to the shared Core <see cref="McpLogger"/> (file + queue).
/// Output Pane mirroring is centralized in <see cref="McpLogger.LineLogged"/> — subscribed
/// once by <c>TheExtension</c> — so every log line (server lifecycle, tool calls, errors)
/// reaches the pane exactly once, regardless of which host layer recorded it.
/// </summary>
internal sealed class DnSpyLogSink : ILogSink {
    public void Info(string message) => McpLogger.Info(message);
    public void Warn(string message) => McpLogger.Warn(message);

    public void Error(string message, Exception? ex = null) {
        if (ex is null) McpLogger.Error(message);
        else McpLogger.Error(ex, message);
    }
}
