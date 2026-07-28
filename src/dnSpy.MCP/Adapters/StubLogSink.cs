using System;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class StubLogSink : ILogSink {
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
