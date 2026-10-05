using rhDock.Rhino;

static class SnapshotSenderChecks
{
    public static async Task Run()
    {
        var stalled = new TaskCompletionSource();
        var sent = new List<string>();
        var errors = new List<Exception>();
        var callbacks = new Queue<Action>();
        void DispatchCompletions() { while (callbacks.TryDequeue(out var action)) action(); }
        var sender = new SnapshotSender(script =>
        {
            sent.Add(script);
            return script == "first" ? stalled.Task : script == "fail" ? Task.FromException(new Exception("browser closed")) : Task.CompletedTask;
        }, errors.Add, callbacks.Enqueue);
        var delivery = sender.Send("first");
        for (var i = 0; i < 1000; i++) await sender.Send("pending " + i);
        if (delivery.IsCompleted || sent.Count != 1) throw new Exception("A stalled browser must not block callers or accumulate browser requests.");
        stalled.SetResult();
        await delivery;
        if (sent.Count != 1 || callbacks.Count != 1) throw new Exception("Browser completion must marshal back before touching pending delivery.");
        DispatchCompletions();
        if (!sent.SequenceEqual(new[] { "first", "pending 999" })) throw new Exception("Only the latest pending snapshot should be delivered.");
        await sender.Send("fail");
        DispatchCompletions();
        await sender.Send("recovered");
        DispatchCompletions();
        if (errors.Count != 1 || sent[^1] != "recovered") throw new Exception("Browser errors must be contained and allow later delivery.");
        stalled = new TaskCompletionSource();
        delivery = sender.Send("first");
        await sender.Send("hidden");
        sender.ClearPending();
        stalled.SetResult();
        await delivery;
        DispatchCompletions();
        if (sent[^1] != "first") throw new Exception("Closing or hiding the panel must discard its pending snapshot.");
        Console.WriteLine("PASS: nonblocking, bounded WebView snapshot delivery and error recovery.");
    }
}
