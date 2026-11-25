using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sandman.Tray.Windows;

/// <summary>
/// The "UI thread" of the Windows head: a queue drained by the thread that calls <see cref="Run"/>. The tray icon
/// and its menu live on H.NotifyIcon's own message thread; menu clicks and timer callbacks post here, so the
/// controller's state is only touched from one thread.
/// </summary>
internal sealed class SerialContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("SerialContext only supports Post");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs posted callbacks until <see cref="Complete"/>.</summary>
    public void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try { callback(state); }
            catch (Exception e) { Debug.WriteLine($"Sandman: {e}"); }
        }
    }

    /// <summary>Ends <see cref="Run"/> after the callbacks already queued. Any thread.</summary>
    public void Complete() => _queue.CompleteAdding();
}
