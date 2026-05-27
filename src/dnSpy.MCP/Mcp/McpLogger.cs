using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using dnSpy.Contracts.Text;

namespace dnSpy.MCP.Mcp {
    /// <summary>
    /// Thread-safe logger that writes to file, debug output, and the dnSpy output pane.
    /// Supports automatic log rotation when the file exceeds <see cref="MaxFileSizeBytes"/>.
    /// </summary>
    public static class McpLogger {
        static readonly ConcurrentQueue<string> _recent = new();
        const int MaxRecent = 200;
        static readonly string _logPath;
        static readonly object _fileLock = new();

        /// <summary>
        /// Maximum log file size before rotation (5 MB).
        /// </summary>
        const long MaxFileSizeBytes = 5 * 1024 * 1024;

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
                    RotateLogIfNeeded();
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch (Exception ex) {
                // Fallback: at least write to debug output so nothing is silently lost
                System.Diagnostics.Debug.WriteLine($"MCP [LOG ERROR]: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"MCP: {line}");

            WriteToOutputWindow(color, line);
        }

        /// <summary>
        /// Rotates the log file if it exceeds <see cref="MaxFileSizeBytes"/>.
        /// Old file is renamed to mcp-server.log.1, .2, etc. (up to 3 backups).
        /// Must be called while holding <see cref="_fileLock"/>.
        /// </summary>
        static void RotateLogIfNeeded() {
            if (!File.Exists(_logPath)) return;

            var fi = new FileInfo(_logPath);
            if (fi.Length < MaxFileSizeBytes) return;

            // Delete oldest backup if exists
            var oldest = _logPath + ".3";
            if (File.Exists(oldest)) File.Delete(oldest);

            // Shift backups: .2 -> .3, .1 -> .2, current -> .1
            for (int i = 2; i >= 1; i--) {
                var src = _logPath + "." + i;
                var dst = _logPath + "." + (i + 1);
                if (File.Exists(src)) File.Move(src, dst);
            }

            File.Move(_logPath, _logPath + ".1");
            Info($"Log rotated (file exceeded {MaxFileSizeBytes / 1024 / 1024} MB)");
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
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"MCP [CLEAR ERROR]: {ex.Message}");
            }
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
