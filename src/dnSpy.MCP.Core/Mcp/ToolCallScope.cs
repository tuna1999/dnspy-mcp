using System;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.MCP.Core.Mcp {
    /// <summary>
    /// Ambient per-call cancellation. McpServerHost opens a scope around tool.Invoke so the
    /// timeout actually CANCELS in-flight work (decompilers honor this via DecompilationContext).
    /// Tools/decompiler adapters read <see cref="Token"/>; when no host scope is open (unit
    /// tests, Headless stdio) it is CancellationToken.None — same behavior as before.
    /// AsyncLocal: Task.Run in the host captures the value set before Invoke.
    /// </summary>
    public static class ToolCallScope {
        private static readonly AsyncLocal<CancellationToken?> _current = new();

        public static CancellationToken Token => _current.Value ?? CancellationToken.None;

        /// <summary>Set the ambient token for this logical call flow. Reset to None after.</summary>
        public static void Set(CancellationToken ct) => _current.Value = ct;
    }

    /// <summary>
    /// Serializes destructive tool calls so parallel batch requests can't race on shared
    /// dnlib metadata. Mirrors McpServerHost._mutationLock for the MCP SDK stdio transport,
    /// which has no built-in awareness of mutation invariants. Read-only tools skip the
    /// lock entirely so they stay fully parallel.
    /// Detection delegates to <see cref="ToolRegistry.IsMutationTool"/> so both hosts
    /// stay in sync — change the prefix list once, both transports follow.
    /// </summary>
    public static class MutationGate {
        public static async ValueTask<T> RunAsync<T>(
            string toolName,
            Func<ValueTask<T>> work,
            SemaphoreSlim mutationLock,
            CancellationToken cancellationToken) {

            if (!ToolRegistry.IsMutationTool(toolName))
                return await work().ConfigureAwait(false);

            await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                return await work().ConfigureAwait(false);
            }
            finally {
                mutationLock.Release();
            }
        }
    }
}
