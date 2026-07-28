using System;
using dnSpy.MCP.Core.Abstractions;
using dnSpy.MCP.Core.Helpers;

namespace dnSpy.MCP.Core.Mcp;

/// <summary>
/// Typed composition root holding all 5 host-specific dependencies plus a derived
/// MethodResolver. Tools receive this via constructor injection.
/// Not a service locator: tools reference concrete properties, never GetService&lt;T&gt;().
/// </summary>
public sealed class McpContext {
    public IAssemblyLoader AssemblyLoader { get; }
    public ISourceDecompiler SourceDecompiler { get; }
    public IUIThreadScheduler UI { get; }
    public ILogSink Log { get; }
    public ITreeRefreshNotifier TreeRefresh { get; }
    public MethodResolver Resolver { get; }

    public McpContext(
        IAssemblyLoader assemblyLoader,
        ISourceDecompiler sourceDecompiler,
        IUIThreadScheduler ui,
        ILogSink log,
        ITreeRefreshNotifier treeRefresh) {
        AssemblyLoader = assemblyLoader ?? throw new ArgumentNullException(nameof(assemblyLoader));
        SourceDecompiler = sourceDecompiler ?? throw new ArgumentNullException(nameof(sourceDecompiler));
        UI = ui ?? throw new ArgumentNullException(nameof(ui));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        TreeRefresh = treeRefresh ?? throw new ArgumentNullException(nameof(treeRefresh));
        Resolver = new MethodResolver(assemblyLoader);
    }
}
