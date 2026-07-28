using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless tree refresh notifier — no-op. There is no TreeView in Headless mode,
/// so metadata mutations require no UI refresh notification.
/// </summary>
public sealed class NoOpTreeRefreshNotifier : ITreeRefreshNotifier {
    public void RefreshAll() { }

    public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
}
