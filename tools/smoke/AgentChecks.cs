using System.Text.Json.Nodes;
using rhDock.Agents;

static class AgentChecks
{
    // Live: drives one agent through the adapter the Agent tab uses, outside Rhino, in a disposable Git repository.
    // Questions get their first option and permissions are allowed once. Steps the agent does not support are skipped.
    public static async Task RunLive(string id, string? agentPath, string? model, bool keep)
    {
        var harness = Harnesses.Get(id);
        if (harness.Id != id) throw new Exception($"Unknown agent {id}; known: {string.Join(", ", Harnesses.All.Select(h => h.Id))}.");
        var exe = Harnesses.Find(harness, agentPath, AgentProcess.ExtendedPath());
        if (exe is null) { Console.WriteLine($"SKIP {id}: not installed ({harness.Install})."); return; }
        var cwd = Path.Combine(Path.GetTempPath(), $"rhdock-{id}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "a.txt"), "hello\n");
        // OpenCode snapshots files for rewind only in a Git repository with a commit; rhDock covers other repositories with its own copies.
        foreach (var git in new[] { "init -q", "add .", "-c user.name=smoke -c user.email=smoke@localhost commit -qm start" })
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", git) { WorkingDirectory = cwd })?.WaitForExit();
        var log = Path.Combine(cwd, "agent.log");
        var events = new List<AgentEvent>();
        using var agent = harness.Create(exe, line => { lock (log) File.AppendAllText(log, line + "\n"); });
        string? sessionId = null;
        AgentEvent[] All() { lock (events) return events.ToArray(); }
        void Clear() { lock (events) events.Clear(); }
        string File_() => File.ReadAllText(Path.Combine(cwd, "a.txt")).Trim();
        agent.Event += e =>
        {
            lock (events) events.Add(e);
            switch (e)
            {
                case TextDelta t: Console.Write(t.Text); break;
                case ToolCall { Status: "Completed" or "Failed" } t: Console.WriteLine($"\n[{t.Kind} {t.Status}: {t.Title}{(t.Parent is null ? "" : " (step)")}]"); break;
                case PermissionAsked p:
                    Console.WriteLine($"\n[permission {p.Kind}: {p.Title}]");
                    _ = agent.ReplyPermissionAsync(p.SessionId, p.RequestId, "AllowOnce");
                    break;
                case QuestionAsked q:
                    Console.WriteLine($"\n[question: {q.Fields[0].Description ?? q.Fields[0].Title}]");
                    _ = agent.ReplyQuestionAsync(q.SessionId, q.RequestId, new Dictionary<string, JsonNode?> { [q.Fields[0].Key] = q.Fields[0].Options.FirstOrDefault()?.Value ?? "yes" });
                    break;
                case Notice n: Console.WriteLine($"\n[{n.Text}]"); break;
            }
        };
        try
        {
            var info = await agent.StartAsync(cwd);
            Console.WriteLine($"PASS: started {info.Name} {info.Version} ({info.Executable})");
            // OpenCode loads its catalog after the first call and then announces it.
            var catalog = await agent.CatalogAsync(cwd, null);
            for (var i = 0; i < 50 && catalog.Options.FirstOrDefault(o => o.Id == "model")?.Choices.Count is null or 0; i++) { await Task.Delay(200); catalog = await agent.CatalogAsync(cwd, null); }
            var models = catalog.Options.FirstOrDefault(o => o.Id == "model") ?? throw new Exception("The catalog must offer a model option.");
            Console.WriteLine($"PASS: catalog: {models.Choices.Count} models (current {models.Current}), {catalog.Commands.Count} commands, {catalog.Skills.Count} skills, options {string.Join(", ", catalog.Options.Select(o => o.Id))}");
            if (models.Choices.Count == 0) throw new Exception("The catalog must list models.");
            if (model is not null) await agent.SetOptionAsync(cwd, null, "model", model);

            sessionId = await agent.CreateAsync(cwd);
            async Task<TurnResult> Turn(AgentPrompt prompt)
            {
                var result = await agent.PromptAsync(cwd, sessionId, prompt).WaitAsync(TimeSpan.FromMinutes(5));
                Console.WriteLine($"\n-> {result.Stop}{(result.Error is null ? "" : ": " + result.Error)}");
                return result;
            }
            AgentPrompt Text(string text) => new(text, [("active-script", "rhDock smoke context")], [], [], Mode: "ask");

            if (id is "opencode" or "claude")
            {
                if ((await Turn(Text("Use your question tool to ask me to pick red or blue, then reply with only the color I picked."))).Stop != "end_turn")
                    throw new Exception("The question turn must end after the answer.");
                // Whether the model calls the tool is up to the model.
                Console.WriteLine(All().OfType<QuestionAsked>().Any() ? "PASS: question round trip." : "SKIP: the model answered without asking a question.");
            }

            Clear();
            if ((await Turn(Text("This is a file-edit integration test. Read a.txt, then actually replace hello with goodbye using your edit or patch tool. Do not just describe the change. Preserve the trailing newline. No other file changes."))).Stop != "end_turn") throw new Exception("The edit turn must end normally.");
            var editPrompt = All().OfType<PromptAccepted>().FirstOrDefault()?.MessageId;
            if (File_() != "goodbye" || !All().OfType<ToolCall>().Any(t => t.Kind == "Edit" && t.Status == "Completed" && t.Detail?.Contains("+goodbye") == true)) throw new Exception("An edit must change the file and report its patch.");
            if (harness.Features.Permissions && !All().OfType<PermissionAsked>().Any(p => p.Kind == "Edit")) throw new Exception("In Ask mode an edit must ask permission.");
            Console.WriteLine($"PASS: edit{(harness.Features.Permissions ? " with permission" : "")} and patch.");

            var usage = All().OfType<ContextUsage>().LastOrDefault();
            if (usage is not { Used: > 0, Size: > 0 }) throw new Exception("Context usage must be reported.");
            Console.WriteLine($"PASS: usage {usage.Used} of {usage.Size} tokens{(All().OfType<TurnUsage>().Any() ? ", turn totals" : "")}.");

            if (catalog.ListsConversations)
            {
                var list = await agent.ListAsync(cwd);
                if (list.All(c => c.Id != sessionId)) throw new Exception("The conversation must be listed.");
                Console.WriteLine($"PASS: list ({list.Count}).");
            }
            if (catalog.LoadsConversations)
            {
                Clear();
                await agent.LoadAsync(cwd, sessionId);
                var replayed = All().OfType<UserMessage>().ToArray();
                if (replayed.Length < 1 || !replayed.Any(u => u.Text.Contains("[rhdock://active-script]", StringComparison.Ordinal)) || !All().OfType<ToolCall>().Any(t => t.Kind == "Edit"))
                    throw new Exception("Loading must replay user messages with their context and the tool calls.");
                Console.WriteLine($"PASS: replay ({All().Length} events).");
            }

            if (harness.Features.Rewind && editPrompt is not null)
            {
                var restored = await agent.RewindAsync(cwd, sessionId, editPrompt);
                if (restored && File_() != "hello") throw new Exception("A rewind that reports restored files must put the file back.");
                Clear();
                await agent.LoadAsync(cwd, sessionId);
                if (All().OfType<Rewound>().SingleOrDefault()?.MessageId != editPrompt || All().OfType<ToolCall>().Any(t => t.Kind == "Edit"))
                    throw new Exception("A rewound conversation must replay up to the boundary and report it.");
                await agent.UndoRewindAsync(cwd, sessionId);
                Clear();
                await agent.LoadAsync(cwd, sessionId);
                if (All().OfType<Rewound>().Any() || !All().OfType<ToolCall>().Any(t => t.Kind == "Edit")) throw new Exception("Undoing a rewind must bring the conversation back.");
                // A rewind takes effect with the next prompt.
                await agent.RewindAsync(cwd, sessionId, editPrompt);
                await Turn(Text("Reply with only the word OK."));
                Clear();
                await agent.LoadAsync(cwd, sessionId);
                if (All().OfType<ToolCall>().Any(t => t.Kind == "Edit") || !All().OfType<UserMessage>().Any(u => u.Text.Contains("only the word OK", StringComparison.Ordinal)))
                    throw new Exception("After a rewind the next prompt must continue from before the rewound prompt.");
                Console.WriteLine($"PASS: rewind ({(restored ? "agent restored files" : "rhDock restores files")}), undo, and continue.");
            }

            if (harness.Features.Compact || catalog.Commands.Any(c => c.Name == "compact"))
            {
                // A prompt on the current branch, so a refusal can only come from the compaction.
                var beforeCompact = All().OfType<UserMessage>().LastOrDefault()?.MessageId ?? editPrompt;
                var compact = await Turn(new AgentPrompt("", [], [], [], Compact: true));
                if (id is "pi" or "omp" && compact.Error?.Contains("Nothing to compact", StringComparison.Ordinal) == true)
                    Console.WriteLine("SKIP: Pi says the session is too small to compact.");
                else if (compact.Stop != "end_turn") throw new Exception("Compact must end the turn: " + compact.Error);
                else Console.WriteLine("PASS: compact.");
                if (id == "claude" && beforeCompact is not null)
                {
                    try { await agent.RewindAsync(cwd, sessionId, beforeCompact); throw new Exception("A rewind to before a compaction must be refused."); }
                    catch (InvalidOperationException) { Console.WriteLine("PASS: rewind before a compaction is refused."); }
                }
            }

            if (harness.Features.Steer)
            {
                Clear();
                var turn = agent.PromptAsync(cwd, sessionId, Text("Run the shell command `sleep 8` and then count from 1 to 5, one number per line."));
                for (var i = 0; i < 600 && !All().Any(e => e is ToolCall or TextDelta); i++) await Task.Delay(100);
                await agent.SteerAsync(cwd, sessionId, Text("After counting, also say the word BANANA."));
                await turn.WaitAsync(TimeSpan.FromMinutes(5));
                Console.WriteLine($"\nPASS: steer accepted{(All().OfType<PromptAccepted>().Count() >= 2 ? " and stored" : "")}.");
            }

            Clear();
            var cancelled = agent.PromptAsync(cwd, sessionId, Text("Run the shell command `sleep 30`, then count slowly from 1 to 200, one number per line."));
            for (var i = 0; i < 600 && !All().Any(e => e is ToolCall or TextDelta); i++) await Task.Delay(100);
            await agent.CancelAsync(sessionId);
            var stopped = await cancelled.WaitAsync(TimeSpan.FromMinutes(2));
            if (stopped.Stop != "cancelled") throw new Exception("Cancel must end the turn as cancelled, not " + stopped.Stop);
            Console.WriteLine("\nPASS: cancel.");
        }
        finally
        {
            agent.Dispose();
            Console.WriteLine($"Log: {log}");
            if (!keep) try { Directory.Delete(cwd, true); } catch (IOException) { }
        }
    }
}
