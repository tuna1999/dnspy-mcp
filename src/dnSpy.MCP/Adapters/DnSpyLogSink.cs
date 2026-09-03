using System;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Text;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Adapters;

/// <summary>
/// Extension log sink: writes to file via shared Core McpLogger, and to dnSpy Output Pane
/// when available (marshaled to UI thread via <see cref="IUIThreadScheduler"/>).
/// </summary>
internal sealed class DnSpyLogSink : ILogSink {
    private readonly IUIThreadScheduler _ui;
    private readonly IOutputTextPane? _pane;

    public DnSpyLogSink(IUIThreadScheduler ui, IOutputTextPane? pane) {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _pane = pane;
    }

    public void Info(string message) =>
        Log(McpLogger.Level.Info, message, BoxedTextColor.DebugLogExtensionMessage);

    public void Warn(string message) =>
        Log(McpLogger.Level.Warn, message, BoxedTextColor.DebugLogStepFiltering);

    public void Error(string message, Exception? ex = null) {
        var text = ex is null ? message : $"{message}: {ex}";
        Log(McpLogger.Level.Error, text, BoxedTextColor.DebugLogExceptionUnhandled);
    }

    private void Log(McpLogger.Level level, string message, object color) {
        // Core logger writes file + Debug output. Extension-only enrichment below.
        McpLogger.Log(level, message);

        if (_pane is null) return;

        try {
            _ui.Invoke(() => _pane.WriteLine(color, message));
        }
        catch (Exception ex) {
            // Never let Output Pane failures propagate; the file log already captured the line.
            System.Diagnostics.Debug.WriteLine($"MCP [OUTPUT ERROR]: {ex.Message}");
        }
    }
}
