using System;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Headless.Adapters;

/// <summary>
/// Headless UI thread scheduler — executes inline. There is no WPF Dispatcher in
/// Headless mode, so all tool code runs on the caller's thread directly.
/// </summary>
public sealed class InlineUIThreadScheduler : IUIThreadScheduler {
    public T Invoke<T>(Func<T> action) => action();

    public void Invoke(Action action) => action();
}
