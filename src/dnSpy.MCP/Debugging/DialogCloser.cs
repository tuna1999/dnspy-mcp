using System;
using System.Runtime.InteropServices;
using System.Text;

namespace dnSpy.MCP.Debugging;

/// <summary>
/// Closes modal error dialogs owned by the dnSpy process (e.g. the debugger engine's
/// "Could not start the debugger" box, which blocks the WPF UI thread until dismissed
/// and wedges every MCP tool that needs the dispatcher). Win32-only — no dispatcher.
/// </summary>
internal static class DialogCloser {
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

    const uint WM_CLOSE = 0x0010;

    /// <summary>Pure predicate: does this window look like a modal dialog and not the
    /// main window? Win32 message boxes are class #32770; dnSpy's WPF error boxes are
    /// short top-level windows (observed 779x150) while the main window is full-size.
    /// Internal for unit tests.</summary>
    internal static bool IsDialogLike(bool isMainWindow, int width, int height, string className) =>
        !isMainWindow && (className == "#32770" || (height > 0 && height < 300));

    /// <summary>Closes every dialog-like top-level window of the current process.
    /// Returns a human-readable report of what happened (list may be empty).</summary>
    internal static string CloseDialogs() {
        var sb = new StringBuilder();
        var currentPid = (uint)Environment.ProcessId;
        IntPtr main = IntPtr.Zero;
        try {
            // WPF main window handle; guard for headless hosts (no dispatcher).
            var app = System.Windows.Application.Current;
            main = app?.Dispatcher?.Invoke(
                () => app.MainWindow is { } mw ? new System.Windows.Interop.WindowInteropHelper(mw).Handle : IntPtr.Zero
            ) ?? IntPtr.Zero;
        }
        catch { /* no UI — treat all owned windows as closable candidates */ }

        var closed = 0;
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != currentPid || !IsWindowVisible(h))
                return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (!GetWindowRect(h, out var r))
                return true;
            var w = r.R - r.L;
            var ht = r.B - r.T;
            if (!IsDialogLike(h == main, w, ht, cls.ToString()))
                return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, 512);
            SendMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var gone = !IsWindow(h);
            if (gone) {
                closed++;
                sb.AppendLine($"closed 0x{h.ToInt64():X} ({cls} {w}x{ht}) \"{title}\"");
            }
            return true;
        }, IntPtr.Zero);

        if (closed == 0)
            sb.AppendLine("No dialogs found in the dnSpy process.");
        return sb.ToString();
    }
}
