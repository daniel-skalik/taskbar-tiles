using System.Windows.Threading;

namespace TaskbarTiles.Host.Runtime;

/// <summary>Runs work on the UI thread. Abstracted so runtime logic is testable without WPF.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}
