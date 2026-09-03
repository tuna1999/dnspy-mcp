using System.Threading;
using dnSpy.MCP.Core.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// MCP SDK call-tool filter that serializes destructive tool calls — the stdio transport
/// has no built-in awareness of mutation invariants, so without this two parallel
/// <c>rename_*</c> / <c>update_*</c> calls in a single batch would race on shared
/// <c>ModuleDef</c> state and corrupt dnlib metadata.
///
/// Mirrors <c>McpServerHost._mutationLock</c> from the Extension HTTP transport. The
/// detection predicate (<see cref="ToolRegistry.IsMutationTool"/>) is shared, so adding
/// a new mutation prefix updates both transports in one place.
/// </summary>
public static class MutationLockFilter {
    public static SemaphoreSlim CreateLock() => new(1, 1);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Build(SemaphoreSlim mutationLock) {
        // Concise (non-async) lambda — body returns ValueTask<T> directly so it matches
        // McpRequestHandler<TParams,TResult> = (RequestContext<TParams>, CT) → ValueTask<TResult>.
        // An async lambda here would coerce to Task<T> and fail to convert.
        // `next` already returns ValueTask<CallToolResult>, so the inner delegate is just a
        // pass-through (no wrapping needed — ValueTask<T> has no (ValueTask<T>) ctor).
        return next => (request, cancellationToken) => MutationGate.RunAsync<CallToolResult>(
            request.Params?.Name ?? "",
            () => next(request, cancellationToken),
            mutationLock,
            cancellationToken);
    }
}
