using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using rhDock.Agents;
using rhDock.Rhino;

static class AgentHostChecks
{
    public static async Task Run()
    {
        var repo = Path.Combine(Path.GetTempPath(), "rhdock-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "a.txt");
        File.WriteAllText(file, "before\n");
        var host = new AgentHost();
        using var agent = new Fake(file);
        typeof(AgentHost).GetField("harness", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, agent);
        agent.Event += e => typeof(AgentHost).GetMethod("OnEvent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [e]);
        JsonElement State() => JsonSerializer.SerializeToElement(host.Snapshot());
        async Task Ready()
        {
            for (var i = 0; i < 200 && State().GetProperty("status").GetString() is "starting" or "working"; i++) await Task.Delay(20);
            if (State().GetProperty("status").GetString() != "ready") throw new Exception(State().GetRawText());
        }
        try
        {
            host.Connect(repo);
            await Ready();
            host.SetMode("edits");
            host.Prompt(repo, "edit", [], [], []);
            await Ready();
            if (agent.Mode != "edits") throw new Exception("Every prompt must carry the selected permission mode.");
            if (State().GetProperty("turn").GetProperty("elapsedMs").GetDouble() < 0) throw new Exception("Turn duration must be recorded.");
            if (State().GetProperty("conversations").EnumerateArray().Any(c => c.GetProperty("activity").GetString() == "done"))
                throw new Exception("A turn that finishes on screen is read; it must not show the unread dot.");
            host.SetMode("ask");
            var prompt = State().GetProperty("items").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "user").GetProperty("id").GetInt32();
            host.Restore(prompt);
            await Ready();
            if (File.ReadAllText(file) != "before\n") throw new Exception("Native rewind must restore the file.");
            agent.Emit(new ContextUsage("fake", 1000, 200000, null));
            host.UndoRewind();
            await Ready();
            if (File.ReadAllText(file) != "after\n") throw new Exception("Undo must restore rhDock's copy even when the agent restored files itself.");
            if (State().GetProperty("usage").ValueKind != JsonValueKind.Object) throw new Exception("Reloading after Undo must keep the context use until the agent reports it again.");
            Console.WriteLine("PASS: host native rewind and Undo restore the files.");
            agent.NativeRestore = false;
            var loadedPrompt = State().GetProperty("items").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "user").GetProperty("id").GetInt32();
            host.Restore(loadedPrompt);
            await Ready();
            if (File.ReadAllText(file) != "before\n") throw new Exception("The host must restore its own checkpoint when the agent cannot.");
            host.UndoRewind();
            await Ready();
            if (File.ReadAllText(file) != "after\n") throw new Exception("Undo must also restore files after a host checkpoint rewind.");
            Console.WriteLine("PASS: host checkpoint rewind and Undo restore the files.");

            agent.Emit(new PermissionAsked("fake", "read", "Read a file", "Read", null));
            for (var i = 0; i < 100 && !agent.Allowed.Contains("read"); i++) await Task.Delay(10);
            if (!agent.Allowed.Contains("read") || State().GetProperty("permission").ValueKind != JsonValueKind.Null)
                throw new Exception("Read-only permission requests must be approved automatically in Ask mode.");
            Console.WriteLine("PASS: host approves read-only requests automatically.");
            agent.Emit(new PermissionAsked("fake", "one", "Edit one", "Edit", null));
            agent.Emit(new PermissionAsked("fake", "two", Path.Combine(repo, "Components", "Two.cs"), "Edit", null));
            if (string.Join("/", State().GetProperty("permission").GetProperty("options").EnumerateArray().Select(o => o.GetProperty("name").GetString())) != "Allow once/Always allow/Reject")
                throw new Exception("Every agent's permission request must offer Allow once, Always allow, and Reject.");
            host.Answer("AllowOnce");
            if (State().GetProperty("permission").GetProperty("title").GetString() != Path.Combine("Components", "Two.cs")) throw new Exception("Concurrent requests must stay available after answering the first one, with repository paths shown relative.");
            host.Answer("AllowOnce");
            // The same file under the root's real path (macOS /var is /private/var) also reads relative.
            agent.Emit(new PermissionAsked("fake", "three", Path.Combine(AgentProcess.RealPath(repo), "Three.cs"), "Edit", null));
            if (State().GetProperty("permission").GetProperty("title").GetString() != "Three.cs") throw new Exception("A path under the repository's real path must read relative too.");
            host.Answer("AllowOnce");
            agent.Emit(new ToolCall("fake", "parent", "Task", "Think", "InProgress", null, null));
            agent.Emit(new ToolCall("fake", "child", "Read", "Read", "Completed", "source", ["a.cs", "a.cs"], "parent"));
            agent.Emit(new PlanUpdate("fake", [("InProgress", "Read the Script")]));
            agent.Emit(new PlanProposed("fake", "# Plan"));
            if (State().GetProperty("items").EnumerateArray().Last().GetProperty("kind").GetString() != "proposal" || State().GetProperty("permission").ValueKind != JsonValueKind.Null)
                throw new Exception("A proposed plan must be a transcript item, not a permission request.");
            agent.Emit(new UserMessage("fake", "replayed", "[rhdock://active-script]\nScript\n[/rhdock]\nhi\n\nAttached files:\n- /r/A.cs\n- /r/b.txt", [], ["/r/A.cs"]));
            var replayed = State().GetProperty("items").EnumerateArray().Last(i => i.GetProperty("kind").GetString() == "user");
            if (replayed.GetProperty("text").GetString() != "hi" || string.Join(",", replayed.GetProperty("chips").EnumerateArray().Select(c => c.GetString())) != "Script context,A.cs,b.txt")
                throw new Exception("A replayed prompt must show its context and attached files as chips, not as text: " + replayed.GetRawText());
            agent.Emit(new Rewound("fake", "replayed", "[rhdock://active-script]\nScript\n[/rhdock]\nhi\n\nAttached files:\n- /r/A.cs"));
            if (State().GetProperty("rewound").GetProperty("text").GetString() != "hi") throw new Exception("The rewound prompt returned to the composer must be the user's text only.");
            if (!State().GetProperty("items").EnumerateArray().Any(i => i.GetProperty("parent").GetString() == "parent") || State().GetProperty("plan").GetArrayLength() != 1)
                throw new Exception("Subagent parents and a separate plan must reach the panel.");
            if (State().GetProperty("items").EnumerateArray().First(i => i.GetProperty("callId").GetString() == "child").GetProperty("files").GetArrayLength() != 1)
                throw new Exception("A tool row must list each file once.");
            Console.WriteLine("PASS: prompt mode, duration, concurrent requests, nested steps, and plan snapshots.");
            // Rhino's dispatcher may wait for its UI thread. That thread needs the host lock to publish a snapshot.
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = false;
            Rhino.RhinoApp.Dispatch = action =>
            {
                var ui = Task.Run(action);
                if (!ui.Wait(500)) blocked = true;
                dispatched.TrySetResult();
            };
            await Task.Delay(100);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            host.Prompt(repo, "followup", [], [], []);
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Rhino.RhinoApp.Dispatch = action => action();
            await Ready();
            if (blocked || timer.ElapsedMilliseconds > 400)
                throw new Exception("A prompt or followup must not dispatch to Rhino while holding the snapshot lock.");
            Console.WriteLine("PASS: followup refresh never waits for the UI while holding the host lock.");
            agent.Emit(new PermissionAsked("fake", "ended-one", "Edit one", "Edit", null));
            agent.Emit(new PermissionAsked("fake", "ended-two", "Edit two", "Edit", null));
            agent.Emit(new Exited("The agent exited"));
            host.Answer("AllowOnce");
            if (State().GetProperty("permission").ValueKind != JsonValueKind.Null)
                throw new Exception("An exited agent must discard queued requests; a stale panel answer cannot reopen one.");
            Console.WriteLine("PASS: agent exit discards pending requests.");
            await Switching(repo, file);
            await Naming(repo, file);
        }
        finally { host.Stop(); Directory.Delete(repo, true); }
    }

    // Live: the real host and agent, with one UI thread that Rhino's dispatcher waits for and that takes every snapshot, as the panel does.
    public static async Task RunLive(string id, string? model, string? effort)
    {
        var repo = Path.Combine(Path.GetTempPath(), $"rhdock-host-{id}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "hello\n");
        var ui = new System.Collections.Concurrent.BlockingCollection<Action>();
        var slowest = 0L;
        var thread = new Thread(() =>
        {
            foreach (var action in ui.GetConsumingEnumerable())
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                action();
                JsonSerializer.SerializeToElement(AgentHost.Current.Snapshot());
                slowest = Math.Max(slowest, timer.ElapsedMilliseconds);
            }
        }) { IsBackground = true };
        thread.Start();
        Rhino.RhinoApp.Dispatch = action =>
        {
            using var done = new ManualResetEventSlim();
            ui.Add(() => { action(); done.Set(); });
            if (!done.Wait(5000)) throw new Exception("The UI thread stalled for 5 s; Rhino's watchdog would abort.");
        };
        JsonElement State() { var s = new TaskCompletionSource<JsonElement>(); ui.Add(() => s.SetResult(JsonSerializer.SerializeToElement(AgentHost.Current.Snapshot()))); return s.Task.WaitAsync(TimeSpan.FromSeconds(5)).Result; }
        async Task Ready(int seconds)
        {
            for (var i = 0; i < seconds * 10 && State().GetProperty("status").GetString() is "starting" or "working" or "idle"; i++) await Task.Delay(100);
            if (State().GetProperty("status").GetString() != "ready") throw new Exception(State().GetRawText());
        }
        try
        {
            AgentHost.Current.SelectAgent(repo, id);
            AgentHost.Current.Connect(repo);
            await Ready(60);
            string Options() => string.Join(", ", State().GetProperty("config").EnumerateArray().Select(o => $"{o.GetProperty("id")}={o.GetProperty("current")} ({o.GetProperty("options").GetArrayLength()})"));
            Console.WriteLine("PASS: host connected; options " + Options());
            if (model is not null) { AgentHost.Current.SetOption("model", model); await Ready(30); }
            if (effort is not null) { AgentHost.Current.SetOption("effort", effort); await Ready(30); }
            AgentHost.Current.Prompt(repo, "Reply with only the word OK.", [("active-script", "rhDock host smoke")], [], []);
            await Ready(180);
            var items = State().GetProperty("items").EnumerateArray().ToArray();
            if (!items.Any(i => i.GetProperty("kind").GetString() == "assistant" && i.GetProperty("text").GetString()!.Contains("OK")))
                throw new Exception("The host must show the answer: " + State().GetRawText());
            Console.WriteLine($"PASS: host prompt round trip; options {Options()}; slowest UI refresh {slowest} ms.");
            // An agent that leaves conversations unnamed gets one title from its own model, stored where the agent lists it.
            if (id == "codex")
            {
                for (var i = 0; i < 120 && State().GetProperty("title").GetString() == "Reply with only the word OK."; i++) await Task.Delay(1000);
                var stored = State().GetProperty("conversations").EnumerateArray().FirstOrDefault(c => c.GetProperty("open").GetBoolean());
                Console.WriteLine($"PASS?: title \"{State().GetProperty("title").GetString()}\"; stored row \"{(stored.ValueKind == JsonValueKind.Object ? stored.GetProperty("title").GetString() : null)}\".");
                if (State().GetProperty("title").GetString() == "Reply with only the word OK.") throw new Exception("Codex must title a new conversation after its first turn.");
            }
            if (!State().GetProperty("config").EnumerateArray().Any(o => o.GetProperty("options").GetArrayLength() > 0)) throw new Exception("The model and effort choices must survive the first prompt.");
            if (slowest > 1000) throw new Exception("A UI refresh must not wait on the host for a second.");
            // X2: in Ask mode an edit must reach the panel as a permission card.
            AgentHost.Current.Prompt(repo, "Replace hello with goodbye in a.txt using your edit tool. Change nothing else.", [], [], []);
            for (var i = 0; i < 1800 && State() is var s && s.GetProperty("permission").ValueKind == JsonValueKind.Null && s.GetProperty("status").GetString() == "working"; i++) await Task.Delay(100);
            var asked = State().GetProperty("permission");
            if (asked.ValueKind == JsonValueKind.Null)
                throw new Exception($"In Ask mode an edit must show a permission card; a.txt is now \"{File.ReadAllText(Path.Combine(repo, "a.txt")).Trim()}\". Mode: {State().GetProperty("mode")}");
            AgentHost.Current.Answer("AllowOnce");
            await Ready(180);
            Console.WriteLine($"PASS: host Ask-mode edit asked ({asked.GetProperty("kind")} {asked.GetProperty("title")}); a.txt is \"{File.ReadAllText(Path.Combine(repo, "a.txt")).Trim()}\".");
        }
        finally { AgentHost.Shutdown(); ui.CompleteAdding(); Directory.Delete(repo, true); }
    }

    // The title rule: a new unnamed conversation is titled once by its agent; a named or loaded one never is.
    private static async Task Naming(string repo, string file)
    {
        JsonElement State(AgentHost host) => JsonSerializer.SerializeToElement(host.Snapshot());
        async Task Until(Func<bool> condition) { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20); }
        async Task<(AgentHost Host, Fake Agent)> Open(string? named, string? load = null)
        {
            Fake? made = null;
            var host = new AgentHost(_ => made = new Fake(file, load ?? "unnamed") { Named = named });
            typeof(AgentHost).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, []);
            host.Connect(repo);
            await Until(() => State(host).GetProperty("status").GetString() == "ready");
            if (load is not null) { host.Load(repo, load); host = AgentHost.Current; await Until(() => State(host).GetProperty("status").GetString() == "ready"); }
            return (host, made!);
        }
        try
        {
            var (fresh, unnamed) = await Open(null);
            fresh.Prompt(repo, "Fix the falloff curve", [], [], []);
            await Until(() => State(fresh).GetProperty("title").GetString() == "Generated title");
            fresh.Prompt(repo, "And the colors", [], [], []);
            await Until(() => State(fresh).GetProperty("status").GetString() == "ready" && unnamed.Prompts.Count == 2);
            await Task.Delay(200);
            if (unnamed.Titled != 1 || State(fresh).GetProperty("title").GetString() != "Generated title")
                throw new Exception($"A new unnamed conversation must be titled exactly once, after its first turn ({unnamed.Titled} calls).");
            var (named, agentNamed) = await Open("Agent's own name");
            named.Prompt(repo, "Fix the falloff curve", [], [], []);
            await Until(() => State(named).GetProperty("status").GetString() == "ready" && agentNamed.Prompts.Count == 1);
            await Task.Delay(200);
            var (loaded, stored) = await Open(null, "stored");
            loaded.Prompt(repo, "Continue", [], [], []);
            await Until(() => State(loaded).GetProperty("status").GetString() == "ready" && stored.Prompts.Count == 1);
            await Task.Delay(200);
            if (agentNamed.Titled != 0 || stored.Titled != 0) throw new Exception("A conversation the agent named, or one loaded from its history, must never be titled again.");
            Console.WriteLine("PASS: new unnamed conversations are titled once; named and loaded ones never.");
        }
        finally { AgentHost.Shutdown(); }
    }

    private static async Task Switching(string repo, string file)
    {
        var agents = new System.Collections.Concurrent.ConcurrentQueue<Fake>();
        var first = new AgentHost(_ => { var fake = new Fake(file, Guid.NewGuid().ToString()); agents.Enqueue(fake); return fake; });
        typeof(AgentHost).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, []);
        JsonElement State(AgentHost host) => JsonSerializer.SerializeToElement(host.Snapshot());
        async Task Until(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20);
            if (!condition()) throw new Exception("Timed out waiting for conversation activity.");
        }
        try
        {
            first.Connect(repo);
            await Until(() => State(first).GetProperty("status").GetString() == "ready");
            first.SetOption("model", "large");
            await Until(() => State(first).GetProperty("config")[0].GetProperty("current").GetString() == "large");
            first.Prompt(repo, "hold", [], [], []);
            await Until(() => agents.First().Holding);
            if (State(first).GetProperty("title").GetString() != "hold") throw new Exception("Before the agent names it, a conversation's header must show its first prompt, as the list does.");
            var running = agents.First();
            var sid = State(first).GetProperty("sessionId").GetString()!;
            first.Prompt(repo, "remove me", [], [], [], steer: true);
            var pending = State(first).GetProperty("queued")[0].GetProperty("id").GetInt32();
            first.Unqueue(pending);
            first.Prompt(repo, "steer me", [], [], [], steer: true);
            if (!State(first).GetProperty("queued")[0].GetProperty("steer").GetBoolean()) throw new Exception("Enter must remain pending while a tool runs.");
            running.Emit(new ToolCall(sid, "shell", "Shell", "Execute", "Completed", "$ echo done", null));
            await Until(() => running.Steered.Contains("steer me"));
            if (running.Steered.Contains("remove me")) throw new Exception("Removed pending steer must never reach the harness.");
            first.Prompt(repo, "next turn", [], [], []);
            first.NewConversation(repo);
            var second = AgentHost.Current;
            if (second == first || running.Disposed) throw new Exception("New must leave the existing turn running in its own conversation.");
            await Until(() => State(second).GetProperty("status").GetString() == "ready");
            if (agents.ElementAt(1).Model != "large") throw new Exception("New conversations must inherit the selected model.");
            running.Emit(new TextDelta(sid, "Background text"));
            running.Emit(new QuestionAsked(sid, "q", "Choose", [new("q0", null, null, "choice", [new("yes", "Yes", null)], false, true)]));
            var waiting = State(second).GetProperty("conversations").EnumerateArray().Single(c => c.GetProperty("id").GetString() == sid);
            if (waiting.GetProperty("activity").GetString() != "input" || State(second).GetProperty("items").GetArrayLength() != 0)
                throw new Exception("Background questions must flag the owner without contaminating the selected transcript.");
            second.Load(repo, sid);
            if (AgentHost.Current != first || State(first).GetProperty("question").ValueKind == JsonValueKind.Null) throw new Exception("Opening the running conversation must preserve its question.");
            first.AnswerQuestion(JsonSerializer.SerializeToElement(new { q0 = "yes" }));
            await Until(() => running.Answers.Contains("q"));
            first.SelectAgent(repo, "pi");
            var pi = AgentHost.Current;
            await Until(() => State(pi).GetProperty("status").GetString() == "ready");
            if (pi == first || running.Disposed) throw new Exception("Harness switching must leave background work alive.");
            if (State(pi).GetProperty("mode").GetString() != "all") throw new Exception("Pi must report Allow all consistently.");
            first.Attach("one", "image/png", "b25l", null, null);
            pi.Attach("two", "image/png", "dHdv", null, null);
            var firstImage = State(first).GetProperty("attachments")[0].GetProperty("id").GetInt32();
            var piImage = State(pi).GetProperty("attachments")[0].GetProperty("id").GetInt32();
            if (firstImage == piImage || pi.Picture(firstImage) != "data:image/png;base64,b25l" || first.Picture(piImage) != "data:image/png;base64,dHdv")
                throw new Exception("Images must keep unique IDs and remain retrievable after switching conversations.");
            running.Finish.TrySetResult(new TurnResult("end_turn", null));
            await Until(() => State(first).GetProperty("status").GetString() == "ready");
            if (State(first).GetProperty("title").GetString() != "Harness conversation title") throw new Exception("The live view must adopt the latest harness title after listing conversations.");
            if (!running.Prompts.Contains("next turn")) throw new Exception("A background turn must continue its queued prompt after it finishes.");
            var old = State(pi).GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == State(first).GetProperty("agentId").GetString());
            if (!old.GetProperty("activities").EnumerateArray().Any(a => a.GetString() == "done")) throw new Exception("A background harness must report completed work.");
            pi.SelectAgent(repo, State(first).GetProperty("agentId").GetString()!);
            if (AgentHost.Current != first) throw new Exception("Harness selection must resume its most recently selected conversation.");
            if (State(first).GetProperty("conversations").EnumerateArray().Any(c => c.GetProperty("activity").GetString() == "done"))
                throw new Exception("Opening a conversation must mark its finished turn read; done means unread.");
            // Explicitly reopening the selected conversation must not replay or clear its transcript.
            AgentHost.Current.Load(repo, sid);
            if (AgentHost.Current != first || !State(first).GetProperty("items").EnumerateArray().Any(i => i.GetProperty("text").GetString() == "Background text"))
                throw new Exception("Switching back must restore the original transcript without replaying it.");
            Console.WriteLine("PASS: pending steer/removal, background queues, concurrent sessions, questions, harness status, and transcript isolation.");
        }
        finally { AgentHost.Shutdown(); }
        if (agents.Any(a => !a.Disposed)) throw new Exception("Shutdown must dispose every open conversation's harness.");
        Console.WriteLine("PASS: shutdown stops every retained harness.");
    }

    private sealed class Fake(string file, string id = "fake") : IAgentHarness
    {
        public event Action<AgentEvent>? Event;
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Allowed = [];
        private bool rewound;
        public string? Mode;
        public string Model = "small";
        public bool NativeRestore = true;
        public bool Holding, Disposed;
        public readonly TaskCompletionSource<TurnResult> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Steered = [], Answers = [], Prompts = [];
        public void Emit(AgentEvent e) => Event?.Invoke(e);
        public Task<AgentInfo> StartAsync(string repository) => Task.FromResult(new AgentInfo("Fake", "1", "fake"));
        public Task<string> CreateAsync(string repository) => Task.FromResult(id);
        // Named: the title the agent stores; null makes the conversation unnamed, as Codex leaves it.
        public string? Named = "Harness conversation title";
        public int Titled;
        public Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository) => Task.FromResult<IReadOnlyList<ConversationInfo>>([new(id, Named, DateTimeOffset.UtcNow, "first prompt")]);
        public Task<string?> TitleAsync(string repository, string sessionId, string message) { Titled++; Named = "Generated title"; return Task.FromResult<string?>(Named); }
        public Task<AgentCatalog> CatalogAsync(string repository, string? sessionId) => Task.FromResult(new AgentCatalog([new("model", "Model", "model", Model, [new("small", "Small"), new("large", "Large")])], [], [], false));
        public Task SetOptionAsync(string repository, string? sessionId, string optionId, string value) { if (optionId == "model") Model = value; return Task.CompletedTask; }
        public Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
        {
            Mode = prompt.Mode;
            Prompts.Add(prompt.Text);
            Emit(new PromptAccepted(sessionId, "prompt"));
            if (prompt.Text == "hold") { Holding = true; return Finish.Task; }
            File.WriteAllText(file, "after\n");
            return Task.FromResult(new TurnResult("end_turn", null));
        }
        public Task LoadAsync(string repository, string sessionId)
        {
            if (rewound) Emit(new Rewound(sessionId, "prompt", "edit"));
            else Emit(new UserMessage(sessionId, "prompt", "edit", [], []));
            return Task.CompletedTask;
        }
        public Task<bool> RewindAsync(string repository, string sessionId, string messageId)
        {
            rewound = true;
            if (NativeRestore) File.WriteAllText(file, "before\n");
            return Task.FromResult(NativeRestore);
        }
        public Task UndoRewindAsync(string repository, string sessionId) { rewound = false; return Task.CompletedTask; }
        public Task ReplyPermissionAsync(string sessionId, string requestId, string? choiceId) { if (choiceId != null) Allowed.Add(requestId); return Task.CompletedTask; }
        public Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer) { Answers.Add(requestId); return Task.CompletedTask; }
        public Task SteerAsync(string repository, string sessionId, AgentPrompt prompt) { Steered.Add(prompt.Text); return Task.CompletedTask; }
        public Task CancelAsync(string sessionId) => Task.CompletedTask;
        public void Dispose() { Disposed = true; Finish.TrySetResult(new TurnResult("cancelled", null)); }
    }
}

// Host logic is compiled unchanged against a minimal UI/settings boundary; this does not test Rhino itself.
namespace Rhino
{
    static class RhinoApp
    {
        public static Action<Action> Dispatch = action => action();
        public static void InvokeOnUiThread(Action action) => Dispatch(action);
    }
}
namespace rhDock.Rhino
{
    sealed class RhinoDockPlugin
    {
        public static RhinoDockPlugin? Instance { get; } = new();
        public TestSettings Settings { get; } = new();
    }
    sealed class TestSettings
    {
        private readonly Dictionary<string, string> values = [];
        public string GetString(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        public void SetString(string name, string value) => values[name] = value;
    }
    sealed class SingleScriptHost
    {
        public static SingleScriptHost Current { get; } = new();
        public void RefreshPanel() { }
    }
}
