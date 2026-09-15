using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using dnSpy.MCP.Core.Mcp;

// Stub tools discovered by ToolRegistry via the dnSpy.MCP.Core.Tools* namespace rule.
namespace dnSpy.MCP.Core.Tools {
    public sealed class StubTools {
        private readonly McpContext _ctx;
        public StubTools(McpContext ctx) => _ctx = ctx;

        public static readonly ConcurrentQueue<(string Event, long Ticks)> Journal = new();
        public static bool WasCancelled;

        [Description("Mutation-prefixed stub: sleeps, journaling enter/exit so tests can observe serialization.")]
        public string UpdateSlowThing(int ms) {
            Journal.Enqueue(("enter", Environment.TickCount64));
            Thread.Sleep(ms);
            Journal.Enqueue(("exit", Environment.TickCount64));
            return "done";
        }

        [Description("Polls the ambient ToolCallScope token until cancelled; flags cancellation for the test.")]
        public string SlowCancellable() {
            var deadline = Environment.TickCount64 + 15_000;
            while (Environment.TickCount64 < deadline) {
                if (ToolCallScope.Token.IsCancellationRequested) {
                    Volatile.Write(ref WasCancelled, true);
                    return "cancelled";
                }
                Thread.Sleep(20);
            }
            return "never-cancelled";
        }

        [Description("Throws with a distinctive message to prove TargetInvocationException is unwrapped.")]
        public string ThrowDiagnostic() =>
            throw new InvalidOperationException("boom-diagnostic-message");
    }
}
