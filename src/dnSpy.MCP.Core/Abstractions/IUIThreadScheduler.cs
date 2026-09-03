using System;

namespace dnSpy.MCP.Core.Abstractions;

/// <summary>
/// Marshals actions to the host's UI thread (Extension: WPF Dispatcher; Headless: inline).
/// </summary>
public interface IUIThreadScheduler {
    T Invoke<T>(Func<T> action);
    void Invoke(Action action);
}
