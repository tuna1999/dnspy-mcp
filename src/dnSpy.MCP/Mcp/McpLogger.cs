using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Mcp {
    public static class McpLogger {
        static readonly ConcurrentQueue<string> _recent = new();
        const int MaxRecent = 200;
        static readonly string _logPath;
        static readonly object _fileLock = new();

        static McpLogger() {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            _logPath = Path.Combine(dir, "mcp-server.log");
        }

        public static void Info(string message) => Log("INFO", message, BoxedTextColor.DebugLogExtensionMessage);
        public static void Warn(string message) => Log("WARN", message, BoxedTextColor.DebugLogStepFiltering);
        public static void Error(string message) => Log("ERROR", message, BoxedTextColor.DebugLogExceptionUnhandled);
        public static void Error(Exception ex, string context = "") => Log("ERROR", $"{context}: {ex}", BoxedTextColor.DebugLogExceptionUnhandled);

        static void Log(string level, string message, object color) {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

            _recent.Enqueue(line);
            while (_recent.Count > MaxRecent)
                _recent.TryDequeue(out _);

            try {
                lock (_fileLock) {
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch { }

            System.Diagnostics.Debug.WriteLine($"MCP: {line}");

            WriteToOutputWindow(color, line);
        }

        static void WriteToOutputWindow(object color, string text) {
            try {
                var pane = DnSpyContext.OutputPane;
                if (pane == null) return;

                var app = System.Windows.Application.Current;
                if (app?.Dispatcher.CheckAccess() == false) {
                    app.Dispatcher.InvokeAsync(() => {
                        try { pane.WriteLine(color, text); } catch { }
                    });
                }
                else {
                    pane.WriteLine(color, text);
                }
            }
            catch { }
        }

        public static string GetRecentLogs(int count = 50) {
            var sb = new StringBuilder();
            sb.AppendLine($"Log file: {_logPath}");
            sb.AppendLine($"Server status: {(DnSpyContext.Extension?.IsServerRunning == true ? "Running" : "Stopped")}");
            sb.AppendLine($"Port: {DnSpyContext.Extension?.ServerPort ?? 0}");
            sb.AppendLine();

            var entries = _recent.ToArray();
            var start = Math.Max(0, entries.Length - count);
            for (int i = start; i < entries.Length; i++)
                sb.AppendLine(entries[i]);

            return sb.ToString();
        }

        public static void ClearLog() {
            try {
                lock (_fileLock) {
                    if (File.Exists(_logPath))
                        File.Delete(_logPath);
                }
            }
            catch { }
            while (_recent.TryDequeue(out _)) { }

            // Clear the Output Window pane too
            try {
                var pane = DnSpyContext.OutputPane;
                pane?.Clear();
            }
            catch { }
        }
    }
}
