using System;
using System.Collections.Generic;
using System.IO;

namespace dnSpy.MCP.Headless;

public sealed class CliOptions {
    public List<string> PreLoads { get; } = new();
    public string? ConfigPath { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args) {
        var opts = new CliOptions();
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--load":
                case "-l":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--load requires a path argument");
                    opts.PreLoads.Add(args[++i]);
                    break;
                case "--config":
                case "-c":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--config requires a path argument");
                    opts.ConfigPath = args[++i];
                    break;
                case "--help":
                case "-h":
                    opts.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return opts;
    }

    /// <summary>Expand any glob patterns in PreLoads into concrete file paths.</summary>
    public IReadOnlyList<string> ExpandLoads() {
        var paths = new List<string>();
        foreach (var pattern in PreLoads) {
            var dir = Path.GetDirectoryName(pattern);
            var file = Path.GetFileName(pattern);
            // Path.GetDirectoryName("*.dll") returns "" (empty string), not null —
            // a null-only check would pass it through to Directory.GetFiles("", ...)
            // which throws ArgumentException. Catch both null and empty.
            if (string.IsNullOrEmpty(dir) || file.Length == 0) {
                if (File.Exists(pattern)) paths.Add(pattern);
                continue;
            }
            if (file.Contains('*') || file.Contains('?')) {
                foreach (var f in Directory.GetFiles(dir, file, SearchOption.TopDirectoryOnly))
                    paths.Add(f);
            }
            else if (File.Exists(pattern)) {
                paths.Add(pattern);
            }
        }
        return paths;
    }

    public static void PrintHelp() {
        Console.Error.WriteLine("dnspy-mcp-headless — standalone MCP server for batch .NET analysis");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: dnspy-mcp-headless [--load <path>]... [--config <json>] [--help]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --load, -l <path>   Pre-load .NET DLL/EXE (repeatable, supports * and ? globs)");
        Console.Error.WriteLine("  --config, -c <json> Configuration file (reserved, currently unused)");
        Console.Error.WriteLine("  --help, -h          Show this help and exit");
        Console.Error.WriteLine();
        Console.Error.WriteLine("MCP transport: stdio (stdin/stdout). Logging: stderr.");
    }
}
