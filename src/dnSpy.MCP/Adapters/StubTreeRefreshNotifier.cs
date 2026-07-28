using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class StubTreeRefreshNotifier : ITreeRefreshNotifier {
    public void RefreshAll() { }
    public void NotifyNamespaceRenamed(string assembly, string oldNamespace, string newNamespace) { }
}
