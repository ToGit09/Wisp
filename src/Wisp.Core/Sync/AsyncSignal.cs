namespace Wisp.Core.Sync;

/// <summary>An auto-reset wake-up signal for background loops. Multiple Set() calls before a wait collapse into one.</summary>
public sealed class AsyncSignal
{
    readonly object _gate = new();
    TaskCompletionSource _tcs = NewSource();
    bool _set;

    static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set()
    {
        lock (_gate)
        {
            _set = true;
            _tcs.TrySetResult();
        }
    }

    /// <summary>Waits until <see cref="Set"/> was called, the timeout elapses or the token is cancelled. Returns true if signalled.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task waiter;
        lock (_gate)
        {
            if (_set)
            {
                _set = false;
                _tcs = NewSource();
                return true;
            }
            waiter = _tcs.Task;
        }

        await Task.WhenAny(waiter, Task.Delay(timeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_set) return false;
            _set = false;
            _tcs = NewSource();
            return true;
        }
    }

    /// <summary>Clears a pending signal. Returns true if there was one.</summary>
    public bool TryConsume()
    {
        lock (_gate)
        {
            if (!_set) return false;
            _set = false;
            _tcs = NewSource();
            return true;
        }
    }

    /// <summary>A task that completes on the next <see cref="Set"/> (without consuming it).</summary>
    public Task WhenSet
    {
        get { lock (_gate) return _set ? Task.CompletedTask : _tcs.Task; }
    }
}
