using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace dnSpy.MCP.Core.Mcp;

/// <summary>
/// File-logging logger. Used by both Extension and Headless. The dnSpy Output Pane
/// integration lives in dnSpy.MCP.Adapters.DnSpyLogSink (Extension-only).
/// </summary>
public static class McpLogger {
    static readonly ConcurrentQueue<string> _recent = new();
    internal const int MaxRecent = 200;
    static readonly string _logPath;
    static readonly object _fileLock = new();

    /// <summary>Maximum log file size before rotation (5 MB).</summary>
    const long MaxFileSizeBytes = 5 * 1024 * 1024;

    public enum Level { Info, Warn, Error }

    static McpLogger() {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(dir, "mcp-server.log");
    }

    public static void Log(Level level, string message) {
        var tag = level switch {
            Level.Info => "INFO",
            Level.Warn => "WARN",
            Level.Error => "ERROR",
            _ => level.ToString().ToUpperInvariant()
        };
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";

        _recent.Enqueue(line);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

        try {
            lock (_fileLock) {
                RotateLogIfNeeded();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"MCP [LOG ERROR]: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"MCP: {line}");
    }

    /// <summary>Convenience wrappers around <see cref="Log(Level, string)"/>. These match
    /// the historical Extension-only API so call sites (McpServerHost, Headless host) can
    /// use the same Info/Warn/Error shorthand regardless of host.</summary>
    public static void Info(string message) => Log(Level.Info, message);
    public static void Warn(string message) => Log(Level.Warn, message);
    public static void Error(string message) => Log(Level.Error, message);
    public static void Error(Exception ex, string message) => Log(Level.Error, $"{message}: {ex}");

    public static string LogPath => _logPath;

    /// <summary>Rotates the log file when it exceeds MaxFileSizeBytes. Caller must hold _fileLock.</summary>
    static void RotateLogIfNeeded() {
        if (!File.Exists(_logPath)) return;
        var fi = new FileInfo(_logPath);
        if (fi.Length < MaxFileSizeBytes) return;

        var oldest = _logPath + ".3";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = 2; i >= 1; i--) {
            var src = _logPath + "." + i;
            var dst = _logPath + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst);
        }
        File.Move(_logPath, _logPath + ".1");
    }

    public static string[] GetRecent(int count = 50) {
        var entries = _recent.ToArray();
        var start = Math.Max(0, entries.Length - count);
        var result = new string[entries.Length - start];
        for (int i = start; i < entries.Length; i++)
            result[i - start] = entries[i];
        return result;
    }

    public static void ClearLog() {
        try {
            lock (_fileLock) {
                if (File.Exists(_logPath)) File.Delete(_logPath);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"MCP [CLEAR ERROR]: {ex.Message}");
        }
        while (_recent.TryDequeue(out _)) { }
    }
}
