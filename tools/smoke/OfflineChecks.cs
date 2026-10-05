using rhDock.Agents;

static class OfflineChecks
{
    // Offline: executable lookup, the agent registry, and repository checkpoints.
    public static void Run()
    {
        if (Harnesses.Get(null).Id != Harnesses.DefaultId || Harnesses.Get("claude").Name != "Claude Code" || Harnesses.All.Select(h => h.Id).Distinct().Count() != Harnesses.All.Count)
            throw new Exception("The registry must have unique ids and fall back to its default agent.");
        Console.WriteLine($"PASS: registry of {Harnesses.All.Count} agents.");
        if (new TurnResult("error", "400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Out of credits.\"}}").Message != "400 Out of credits."
            || new TurnResult("error", "plain").Message != "plain" || new TurnResult("error", "bad {json").Message != "bad {json")
            throw new Exception("A provider's JSON error must read as its message, and other errors unchanged.");
        Console.WriteLine("PASS: readable turn errors.");
        if (ConversationTitle.Clean("Title: \"Fix Attractor Falloff.\"\nextra") != "Fix Attractor Falloff" || ConversationTitle.Clean("  \n") is not null
            || ConversationTitle.Clean(new string('a', 80))!.Length != 50 || !ConversationTitle.Prompt("hello").EndsWith("hello", StringComparison.Ordinal))
            throw new Exception("Generated titles must be one clean line of at most 50 characters, built from the user's message.");
        Console.WriteLine("PASS: generated title cleanup.");

        var root = Path.Combine(Path.GetTempPath(), "rhdock-find-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Make(string dir, string file) { Directory.CreateDirectory(Path.Combine(root, dir)); var f = Path.Combine(root, dir, file); File.WriteAllText(f, ""); return f; }
            var shim = Make("npm", OperatingSystem.IsWindows() ? "agent.cmd" : "other");
            var native = Make("bin", OperatingSystem.IsWindows() ? "agent.exe" : "agent");
            var path = string.Join(Path.PathSeparator, Path.Combine(root, "npm"), Path.Combine(root, "bin"));
            if (AgentProcess.Find("agent", null, path) != native || AgentProcess.Find("agent", shim, path) != shim || AgentProcess.Find("agent", Path.Combine(root, "missing"), path) is not null)
                throw new Exception("Agent lookup must prefer a user path, then a native executable over an npm shim, and never fall back from a missing user path.");
            Console.WriteLine("PASS: agent lookup order.");
        }
        finally { Directory.Delete(root, true); }

        var repo = Path.Combine(Path.GetTempPath(), "rhdock-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string relative, string text) { var f = Path.Combine(repo, relative); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text); }
            Write("A.cs", "one\ntwo\nthree\n");
            Write("Sub/C.cs", "gone\n");
            Write("bin/Ignored.cs", "build output\n");
            var checkpoint = RepositoryCheckpoint.Capture(repo);
            Write("A.cs", "one\n2\nthree\n");
            Write("B.cs", "new\n");
            Write("bin/Ignored.cs", "changed build output\n");
            File.Delete(Path.Combine(repo, "Sub", "C.cs"));
            var changes = checkpoint.Compare();
            var summary = string.Join(" ", changes.Select(c => $"{c.Path}:{c.Status}:+{c.Added}-{c.Removed}"));
            if (summary != "A.cs:modified:+1-1 B.cs:added:+1-0 Sub/C.cs:deleted:+0-1" || !changes[0].Diff.Contains("-two\n+2\n"))
                throw new Exception("Checkpoint compare reported: " + summary);
            checkpoint.Restore("A.cs");
            if (File.ReadAllText(Path.Combine(repo, "A.cs")) != "one\ntwo\nthree\n" || checkpoint.Compare().Count != 2)
                throw new Exception("Restoring one file must restore only that file.");
            checkpoint.Restore();
            if (checkpoint.Compare().Count != 0 || File.Exists(Path.Combine(repo, "B.cs")) || !File.Exists(Path.Combine(repo, "Sub", "C.cs")))
                throw new Exception("Restoring a checkpoint must delete added files and recreate deleted ones.");
            Console.WriteLine("PASS: repository checkpoint compare, diff, and restore.");
        }
        finally { Directory.Delete(repo, true); }
    }
}
