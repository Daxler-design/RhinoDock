using rhDock.Core;

internal static class AutoRunPacerChecks
{
    public static void Run()
    {
        var pacer = new AutoRunPacer();
        Require(pacer.Due(1000) == 1000, "first change after selection runs at once");

        pacer.Record(2, 1002);
        Require(pacer.Due(1005) == 1002 + AutoRunPacer.FrameMs, "fast Script runs at most once per frame while edits continue");
        Require(pacer.Due(2000) == 2000, "fast Script runs at once after a pause");

        pacer.Reset();
        pacer.Record(40, 3040);
        Require(pacer.Due(3041) == 3080, "medium Script waits one Run time, keeping the host free half the time");

        pacer.Reset();
        pacer.Record(400, 5400);
        Require(pacer.Due(5401) == 5401 + AutoRunPacer.InteractiveMs, "slow Script waits until edits stop");
        Require(pacer.Due(9000) == 9000 + AutoRunPacer.InteractiveMs, "a later change postpones a slow Script again");

        pacer.Record(10, 9200);
        Require(pacer.Due(9201) == 9201 + AutoRunPacer.InteractiveMs, "one fast Run does not erase a slow history at once");
        pacer.Record(10, 9300);
        pacer.Record(10, 9400);
        Require(pacer.Due(9401) == 9459, "the smoothed duration (59 ms) recovers after several fast Runs");

        pacer.Reset();
        Require(pacer.AverageMs is null && pacer.Due(10000) == 10000, "reset forgets the previous Script's durations");
        Console.WriteLine("PASS: Auto Run pacing.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Auto Run pacing: " + message);
    }
}
