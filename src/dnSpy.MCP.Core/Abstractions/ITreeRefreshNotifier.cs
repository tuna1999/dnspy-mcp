namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Notifies the host that metadata mutations occurred so it can refresh UI state.
/// Extension: delegates to TreeViewTools. Headless: no-op.
/// </summary>
public interface ITreeRefreshNotifier {
    void RefreshAll();
    void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace);
}
