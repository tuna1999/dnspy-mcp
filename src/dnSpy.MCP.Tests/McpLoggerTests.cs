using System;
using System.IO;
using System.Linq;
using dnSpy.MCP.Core.Mcp;
using FluentAssertions;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Guards the McpLogger invariants that the Output Pane rendering path depends on
/// (PR #3 feedback-loop regression): reading/displaying logs must never create new
/// log events. McpLogger is process-wide static state — serialize all tests through
/// a single lock so parallel xunit collections can't interleave entries.
/// </summary>
[Collection("mcp-logger-serial")]   // shares McpLogger static state with McpServerHostGateTests
public class McpLoggerTests {
    static readonly object LoggerLock = new();

    static long LogSize() {
        lock (LoggerLock) {
            var fi = new FileInfo(McpLogger.LogPath);
            return fi.Exists ? fi.Length : 0;
        }
    }

    [Fact]
    public void GetRecent_is_pure_no_queue_or_file_growth() {
        lock (LoggerLock) {
            McpLogger.ClearLog();
            for (int i = 0; i < 10; i++) McpLogger.Info($"seed {i}");

            var first = McpLogger.GetRecent();
            var sizeAfterFirst = LogSize();

            // Rendering must not record: read repeatedly the way Show Log does.
            for (int i = 0; i < 5; i++) _ = McpLogger.GetRecent();

            var second = McpLogger.GetRecent();
            second.Should().Equal(first, "GetRecent must not enqueue entries");
            LogSize().Should().Be(sizeAfterFirst, "GetRecent must not append to the log file");
        }
    }

    [Fact]
    public void GetRecent_returns_most_recent_window_of_capped_queue() {
        const int maxRecent = 200; // McpLogger.MaxRecent (internal const)
        lock (LoggerLock) {
            McpLogger.ClearLog();
            // Seed past the cap: only the last maxRecent survive.
            for (int i = 1; i <= maxRecent + 25; i++) McpLogger.Info($"entry {i}");

            var window = McpLogger.GetRecent(maxRecent + 10); // ask for more than the cap holds
            window.Should().HaveCount(maxRecent, "queue is capped at MaxRecent");
            window.Last().Should().Contain($"entry {maxRecent + 25}");

            var smaller = McpLogger.GetRecent(5);
            smaller.Should().HaveCount(5);
            smaller.Last().Should().Be(window.Last());
        }
    }

    [Fact]
    public void LineLogged_fires_for_every_line_and_swallows_handler_exceptions() {
        lock (LoggerLock) {
            McpLogger.ClearLog();
            var seen = new System.Collections.Generic.List<string>();
            void Handler(McpLogger.Level _, string line) => seen.Add(line);
            // LineLogged is a static event: every subscriber added must be removed before
            // the test exits, or it leaks into the rest of the process (and throws "boom"
            // into unrelated assertions). Hence a named throwing handler, not an anonymous
            // lambda we can't unsubscribe from.
            void ThrowingHandler(McpLogger.Level _, string __)
                => throw new InvalidOperationException("boom");

            McpLogger.LineLogged += Handler;
            McpLogger.LineLogged += ThrowingHandler;
            try {
                McpLogger.Info("event line");
                seen.Should().HaveCount(1);

                // A throwing handler must never break logging
                McpLogger.Warn("still logged");
            }
            finally {
                McpLogger.LineLogged -= Handler;
                McpLogger.LineLogged -= ThrowingHandler;
            }
            seen.Should().HaveCount(2, "logging must survive a throwing subscriber");
            McpLogger.GetRecent(2).Should().HaveCount(2);
        }
    }

    [Fact]
    public void ClearLog_empties_queue_and_file() {
        lock (LoggerLock) {
            McpLogger.Info("before clear");
            McpLogger.ClearLog();

            McpLogger.GetRecent().Should().BeEmpty("queue must be drained");
            File.Exists(McpLogger.LogPath).Should().BeFalse("log file must be deleted");
        }
    }

    [Fact]
    public void TheExtension_imports_are_all_optional() {
        // MEF ignores NRT annotations: a nullable [Import] property is still a REQUIRED
        // import unless AllowDefault=true. If any of the five reverts to a strict import,
        // a dnSpy build lacking that export silently rejects the whole extension part
        // (issue #2). Guard the hardening at the reflection level.
        var props = typeof(dnSpy.MCP.TheExtension)
            .GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(System.ComponentModel.Composition.ImportAttribute), false).Any())
            .ToList();

        props.Should().NotBeEmpty("TheExtension must have [Import] properties");
        foreach (var p in props) {
            var attr = (System.ComponentModel.Composition.ImportAttribute)
                p.GetCustomAttributes(typeof(System.ComponentModel.Composition.ImportAttribute), false)[0];
            attr.AllowDefault.Should().BeTrue(
                $"[Import] on TheExtension.{p.Name} must set AllowDefault=true — strict imports let one missing export kill the entire extension");
        }
    }
}
