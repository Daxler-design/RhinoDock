namespace rhDock.Rhino;

/// <summary>UI-thread delivery: one browser request in flight, retaining only the latest pending snapshot.</summary>
internal sealed class SnapshotSender(Func<string, Task> execute, Action<Exception> reportError, Action<Action> dispatch)
{
    private bool sending;
    private string? pending;

    public void ClearPending() => pending = null;

    public Task Send(string script)
    {
        pending = script;
        if (sending) return Task.CompletedTask;
        sending = true;
        pending = null;
        return Deliver(script);
    }

    private async Task Deliver(string script)
    {
        Exception? error = null;
        try { await execute(script); }
        catch (Exception ex) { error = ex; }
        // Rhino callbacks need not install a SynchronizationContext. Always finish delivery on the UI thread.
        dispatch(() =>
        {
            sending = false;
            if (error is not null) reportError(error);
            if (pending is { } next) _ = Send(next);
        });
    }
}
