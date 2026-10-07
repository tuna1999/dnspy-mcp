using System;
using System.IO;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.MCP.Debugging;
using dnSpy.MCP.Tools;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Unit tests for the pure parts of the debugger tool group: runtime detection
/// heuristics, options-type selection, and graceful unavailability. Live
/// debugging behavior is covered by the in-dnSpy smoke script (spec:
/// docs/superpowers/specs/2026-09-29-debugger-tools-design.md).
/// </summary>
public class DebuggerToolsTests {
    [Fact]
    public void DetectRuntime_dotnet_only_when_runtimeconfig_exists() {
        var dir = Path.Combine(Path.GetTempPath(), "mcpdbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var exe = Path.Combine(dir, "Target.exe");
            File.WriteAllText(exe, "stub");
            Assert.Equal("netfx", DnSpyDebuggerService.DetectRuntime(exe));
            File.WriteAllText(Path.Combine(dir, "Target.runtimeconfig.json"), "{}");
            Assert.Equal("dotnet", DnSpyDebuggerService.DetectRuntime(exe));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("dotnet", "dotnet")]
    [InlineData("netcore", "dotnet")]
    [InlineData("net", "dotnet")]
    [InlineData("netfx", "netfx")]
    [InlineData("framework", "netfx")]
    [InlineData("auto", "netfx")]   // falls back to DetectRuntime: missing exe → no runtimeconfig → netfx
    [InlineData(null, "netfx")]
    public void NormalizeRuntime_maps_aliases_and_falls_back(string? input, string expected) {
        var exe = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.Equal(expected, DnSpyDebuggerService.NormalizeRuntime(input!, exe));
    }

    [Theory]
    [InlineData("terminate", null, true)]    // terminate defaults to dry-run
    [InlineData("terminate", false, false)]
    [InlineData("terminate", true, true)]
    [InlineData("stop", null, false)]        // other modes execute by default
    [InlineData("stop", true, true)]
    [InlineData("detach", null, false)]
    [InlineData("detach", true, true)]
    public void ResolveDryRun_matches_documented_defaults(string mode, bool? dryRun, bool expected) {
        Assert.Equal(expected, DebuggerTools.ResolveDryRun(mode, dryRun));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("UNKNOWN")]
    public void NormalizeRuntime_unknown_tokens_fall_back_to_detection(string input) {
        var exe = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.Equal("netfx", DnSpyDebuggerService.NormalizeRuntime(input, exe));
    }

    [Fact]
    public void Start_without_working_directory_uses_target_folder() {
        var options = (CorDebugStartDebuggingOptions)DnSpyDebuggerService.CreateStartOptions(
            @"C:\debug targets\StageFx.exe", "netfx");
        Assert.Equal(@"C:\debug targets", options.WorkingDirectory);
    }

    [Theory]
    [InlineData(null, @"C:\debug targets")]
    [InlineData("", @"C:\debug targets")]
    [InlineData(@"C:\custom working dir", @"C:\custom working dir")]
    public void Working_directory_treats_empty_as_unset_and_preserves_explicit_paths(string? input, string expected) {
        var options = (CorDebugStartDebuggingOptions)DnSpyDebuggerService.CreateStartOptions(
            @"C:\debug targets\StageFx.exe", "netfx", input);
        Assert.Equal(expected, options.WorkingDirectory);
    }

    [Fact]
    public void Relative_target_resolves_a_nonempty_absolute_working_directory() {
        var options = (CorDebugStartDebuggingOptions)DnSpyDebuggerService.CreateStartOptions("StageFx.exe", "netfx", "");
        Assert.Equal(Directory.GetCurrentDirectory(), options.WorkingDirectory);
    }

    [Fact]
    public void Native_exe_launches_directly_but_managed_exe_uses_dotnet_host() {
        var managedExe = Path.Combine(Path.GetTempPath(), "managed-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(typeof(DebuggerToolsTests).Assembly.Location, managedExe);
        try {
            var native = Assert.IsType<DotNetStartDebuggingOptions>(
                DnSpyDebuggerService.CreateStartOptions(Environment.ProcessPath!, "dotnet"));
            Assert.False(native.UseHost);
            var managed = Assert.IsType<DotNetStartDebuggingOptions>(
                DnSpyDebuggerService.CreateStartOptions(managedExe, "dotnet"));
            Assert.True(managed.UseHost);
        }
        finally {
            File.Delete(managedExe);
        }
    }

    [Fact]
    public void Tools_report_unavailable_without_dnSpy_host() {
        // The test host never calls Initialize (and has no WPF dispatcher): every tool
        // must return an explicit error string, never throw.
        Assert.StartsWith("Error:", DebuggerTools.DebugGetState());
        Assert.StartsWith("Error:", DebuggerTools.DebugListProcesses());
        Assert.StartsWith("Error:", DebuggerTools.DebugStart(@"C:\nope.exe"));
        Assert.StartsWith("Error:", DebuggerTools.DebugAttach(1234));
        Assert.StartsWith("Error:", DebuggerTools.DebugStop());
        Assert.StartsWith("Error:", DebuggerTools.DebugContinue());
        Assert.StartsWith("Error:", DebuggerTools.DebugBreakAll());
        Assert.StartsWith("Error:", DebuggerTools.DebugStep());
        Assert.StartsWith("Error:", DebuggerTools.DebugWaitPaused(1));
        Assert.StartsWith("Error:", DebuggerTools.DebugSetBreakpoint("T::M"));
        Assert.StartsWith("Error:", DebuggerTools.DebugDeleteBreakpoint(1));
        Assert.StartsWith("Error:", DebuggerTools.DebugListBreakpoints());
        Assert.StartsWith("Error:", DebuggerTools.DebugGetCallstack());
    }

    [Theory]
    [InlineData("engine: UserMessage: Could not start the debugger. Make sure you have access to the file 'x'")]
    [InlineData("Error: Could not execute 'x' @ 10:16:49")]
    [InlineData("msg[SomeKind]: boom")]
    [InlineData("Error: something failed")]
    public void LooksLikeStartFailure_matches_engine_errors(string lastBreak) {
        Assert.True(DnSpyDebuggerService.LooksLikeStartFailure(lastBreak));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("breakpoint hit @ 10:13:02")]
    [InlineData("entry point @ 10:16:11")]
    [InlineData("step complete @ 10:17:55")]
    [InlineData("process exited (code 0)")]
    [InlineData("engine: ThreadExited @ 10:35:39")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeStartFailure_ignores_normal_breaks(string? lastBreak) {
        Assert.False(DnSpyDebuggerService.LooksLikeStartFailure(lastBreak!));
    }

    [Theory]
    [InlineData(false, 779, 150, "HwndWrapper")]   // dnSpy WPF error box shape (observed live)
    [InlineData(false, 400, 160, "#32770")]        // Win32 message box
    public void IsDialogLike_matches_error_popups(bool isMain, int w, int h, string cls) {
        Assert.True(DialogCloser.IsDialogLike(isMain, w, h, cls));
    }

    [Theory]
    [InlineData(true, 779, 150, "HwndWrapper")]    // never the main window
    [InlineData(false, 1600, 900, "HwndWrapper")]  // full-size window is not a dialog
    [InlineData(false, 200, 0, "HwndWrapper")]     // degenerate rect
    public void IsDialogLike_ignores_main_and_full_windows(bool isMain, int w, int h, string cls) {
        Assert.False(DialogCloser.IsDialogLike(isMain, w, h, cls));
    }
}
