using System;
using System.Windows;
using System.Windows.Threading;
using dnSpy.MCP.Core.Abstractions;

namespace dnSpy.MCP.Adapters;

internal sealed class WpfUIThreadScheduler : IUIThreadScheduler {
    public T Invoke<T>(Func<T> action) {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return action();
        if (dispatcher.CheckAccess()) return action();
        return dispatcher.Invoke(action, DispatcherPriority.Normal);
    }

    public void Invoke(Action action) {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action, DispatcherPriority.Normal);
    }
}
