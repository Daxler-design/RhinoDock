using rhDock.Core;
using System.Diagnostics;

internal static class ScriptLayoutChecks
{
    public static void Run()
    {
        var repository = Path.Combine(Path.GetTempPath(), "rhdock-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(repository);
            Require(ScriptLayoutStore.Load(repository, "A.cs") is null, "no layout file means no saved layout");

            var inputs = new SectionLayout([new("Growth", ["steps", "radius"])], ["curves"]);
            var outputs = new SectionLayout([], ["Polylines"]);
            Require(ScriptLayoutStore.TryReplace(repository, "dir\\A.cs", 0, inputs, outputs, out var first) && first!.Rev == 1, "first save is revision 1");
            Require(ScriptLayoutStore.Load(repository, "dir/A.cs")?.Inputs.Groups[0].Name == "Growth", "Script keys use portable separators");
            Require(ScriptLayoutStore.Load(repository, "B.cs") is null, "layouts are per Script");

            var other = new SectionLayout([new("Other", ["curves"])], ["steps", "radius"]);
            Require(!ScriptLayoutStore.TryReplace(repository, "dir/A.cs", 0, other, outputs, out var current) && current!.Rev == 1,
                "a writer based on an older revision is refused, and gets the newer layout back");
            Require(ScriptLayoutStore.Load(repository, "dir/A.cs")!.Inputs.Groups[0].Name == "Growth", "the refused write did not overwrite the newer layout");
            Require(ScriptLayoutStore.TryReplace(repository, "dir/A.cs", 1, other, outputs, out var second) && second!.Rev == 2, "a writer based on the current revision succeeds");

            var hidden = new SectionLayout([new("Geometry", ["Polylines"]) { PreviewOff = true }], []);
            Require(ScriptLayoutStore.TryReplace(repository, "dir/A.cs", 2, other, hidden, out var third) && third!.Rev == 3, "a group Preview switch saves with the layout");
            Require(ScriptLayoutStore.Load(repository, "dir/A.cs")!.Outputs.Groups[0].PreviewOff == true, "a group Preview switch round-trips");
            Require(ScriptLayoutStore.Load(repository, "dir/A.cs")!.Inputs.Groups[0].PreviewOff is null, "a group Preview switch defaults to on");
            Throws(() => ScriptLayoutStore.TryReplace(repository, "A.cs", 0, new([new("G", ["x"]), new("g", [])], []), outputs, out _), "group names are unique ignoring case");
            Throws(() => ScriptLayoutStore.TryReplace(repository, "A.cs", 0, new([new("G", ["x"])], ["x"]), outputs, out _), "a port appears once");

            File.WriteAllText(ScriptLayoutStore.PathFor(repository), "{ not json");
            Require(ScriptLayoutStore.Load(repository, "dir/A.cs") is null, "an unreadable layout file never blocks the panel");
            Require(ScriptLayoutStore.TryReplace(repository, "dir/A.cs", 0, inputs, outputs, out _), "an unreadable layout file is replaced on the next save");
            ConcurrentWriters(repository);
        }
        finally { if (Directory.Exists(repository)) Directory.Delete(repository, true); }
        Console.WriteLine("PASS: Script layout checks.");
    }

    public static void Writer(string repository)
    {
        Console.WriteLine("ready");
        while (Console.ReadLine() is { } script)
        {
            var section = new SectionLayout([], [Environment.ProcessId.ToString()]);
            Console.WriteLine(ScriptLayoutStore.TryReplace(repository, script, 0, section, section, out _) ? "saved" : "stale");
        }
    }

    private static void ConcurrentWriters(string repository)
    {
        Process Start()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(ScriptLayoutChecks).Assembly.Location);
            start.ArgumentList.Add("--layout-writer");
            start.ArgumentList.Add(repository);
            return Process.Start(start)!;
        }
        using var a = Start();
        using var b = Start();
        try
        {
            Require(a.StandardOutput.ReadLine() == "ready" && b.StandardOutput.ReadLine() == "ready", "both layout writers started");
            for (var round = 0; round < 20; round++)
            {
                var script = $"race-{round}.cs";
                a.StandardInput.WriteLine(script); a.StandardInput.Flush();
                var otherScript = round % 2 == 0 ? script : $"other-{round}.cs";
                b.StandardInput.WriteLine(otherScript); b.StandardInput.Flush();
                var results = new[] { a.StandardOutput.ReadLine(), b.StandardOutput.ReadLine() };
                Require(otherScript != script ? results.All(r => r == "saved") : results.Count(r => r == "saved") == 1 && results.Count(r => r == "stale") == 1,
                    $"two processes saving revision 0 must produce one winner and one stale refusal (round {round}: {string.Join(", ", results)})");
                Require(ScriptLayoutStore.Load(repository, script)?.Rev == 1, "the winner's layout is readable");
                Require(ScriptLayoutStore.Load(repository, otherScript)?.Rev == 1, "concurrent writes to different Scripts are both retained");
            }
        }
        finally
        {
            foreach (var peer in new[] { a, b })
            {
                peer.StandardInput.Close();
                if (!peer.WaitForExit(5000)) peer.Kill(true);
            }
        }
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Layout check failed: " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Layout check failed: " + message);
    }
}
