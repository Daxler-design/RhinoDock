using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using rhDock.Agents;

static class AgentProtocolChecks
{
    public static void Run()
    {
        using var claude = new ClaudeCode("unused", _ => { });
        var liveType = typeof(ClaudeCode).GetNestedType("Live", BindingFlags.NonPublic)!;
        var live = Activator.CreateInstance(liveType)!;
        liveType.GetField("SessionId")!.SetValue(live, "fixture");
        var done = new TaskCompletionSource<TurnResult>();
        liveType.GetField("Turn")!.SetValue(live, done);
        typeof(ClaudeCode).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(claude,
            [live, JsonSerializer.SerializeToElement(new { type = "result", subtype = "success", is_error = true, result = "Usage limit reached" })]);
        if (done.Task.Result is not { Stop: "error", Error: "Usage limit reached" })
            throw new Exception("Claude's success subtype with is_error must remain an error.");
        Console.WriteLine("PASS: Claude reports API errors even with a success subtype.");
        var outbound = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(AgentProtocolChecks).Assembly.Location);
        start.ArgumentList.Add("--echo-agent");
        using var peer = System.Diagnostics.Process.Start(start)!;
        var linesType = typeof(ClaudeCode).Assembly.GetType("rhDock.Agents.JsonLines")!;
        using var lines = (IDisposable)Activator.CreateInstance(linesType, [peer, (Action<string>)(line => { if (line.StartsWith("-> ")) outbound.Enqueue(line[3..]); })])!;
        liveType.GetField("Lines")!.SetValue(live, lines);
        typeof(ClaudeCode).GetField("live", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(claude, live);
        var claudeEvents = new List<AgentEvent>();
        claude.Event += claudeEvents.Add;
        var claudeHandle = typeof(ClaudeCode).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        JsonElement ClaudeQuestion(string id) => JsonSerializer.SerializeToElement(new { type = "control_request", request_id = id,
            request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions = new[] {
                new { question = "Which colors?", header = "Colors", multiSelect = true, options = new[] { new { label = "Blue", description = "Cool" }, new { label = "Red", description = "Warm" } } } } } } });
        claudeHandle.Invoke(claude, [live, ClaudeQuestion("colors")]);
        var claudeQuestion = claudeEvents.OfType<QuestionAsked>().Last();
        if (claudeQuestion.Fields[0] is not { Key: "Which colors?", Type: "multi", Custom: true, Required: true }) throw new Exception("Claude must keep question text as its field key and allow multiple/custom choices.");
        claude.ReplyQuestionAsync("fixture", "colors", new Dictionary<string, JsonNode?> { ["Which colors?"] = new JsonArray("Blue", "Red") }).GetAwaiter().GetResult();
        var reply = JsonNode.Parse(outbound.Last())!["response"]!["response"]!;
        if (reply["behavior"]?.ToString() != "allow" || reply["updatedInput"]?["answers"]?["Which colors?"]?.ToString() != "Blue, Red" || reply["updatedInput"]?["questions"]?[0]?["header"]?.ToString() != "Colors")
            throw new Exception("Claude must write answers into the original AskUserQuestion input.");
        claudeHandle.Invoke(claude, [live, ClaudeQuestion("skip")]);
        claude.ReplyQuestionAsync("fixture", "skip", null).GetAwaiter().GetResult();
        if (JsonNode.Parse(outbound.Last())?["response"]?["response"]?["behavior"]?.ToString() != "deny") throw new Exception("Claude Skip must deny the question request.");
        claudeHandle.Invoke(claude, [live, JsonSerializer.SerializeToElement(new { type = "assistant", parent_tool_use_id = "claude-spawn", message = new { content = new[] { new { type = "tool_use", id = "claude-read", name = "Read", input = new { file_path = "a.txt" } } } } })]);
        if (!claudeEvents.OfType<ToolCall>().Any(t => t is { Parent: "claude-spawn", Kind: "Read" })) throw new Exception("Claude child tools must retain parent_tool_use_id.");
        Console.WriteLine("PASS: Claude multiple/custom question fields, native answer/Skip transport, and child tool parents.");
        foreach (var (tool_name, input, title) in new (string, object, string)[] { ("Write", new { file_path = "/r/b.txt", content = "hi" }, "/r/b.txt"), ("Bash", new { command = "make", description = "Build it" }, "make") })
        {
            claudeHandle.Invoke(claude, [live, JsonSerializer.SerializeToElement(new { type = "control_request", request_id = tool_name, request = new { subtype = "can_use_tool", tool_name, input } })]);
            if (claudeEvents.OfType<PermissionAsked>().Last().Title != title) throw new Exception($"A Claude {tool_name} permission must name {title}, as the card reads \"wants to <kind> <title>\".");
        }
        Console.WriteLine("PASS: Claude permission titles name the file or command.");
        var askedBefore = claudeEvents.OfType<PermissionAsked>().Count();
        claudeHandle.Invoke(claude, [live, JsonSerializer.SerializeToElement(new { type = "control_request", request_id = "exit-plan", request = new { subtype = "can_use_tool", tool_name = "ExitPlanMode", input = new { plan = "# Plan\n- Edit a.txt" } } })]);
        for (var i = 0; i < 50 && !outbound.Any(o => o.Contains("exit-plan")); i++) Thread.Sleep(20);
        if (claudeEvents.OfType<PermissionAsked>().Count() != askedBefore || JsonNode.Parse(outbound.Last(o => o.Contains("exit-plan")))?["response"]?["response"]?["behavior"]?.ToString() != "deny")
            throw new Exception("ExitPlanMode must be answered by the adapter, as MonoCode does, not shown as a permission.");
        claudeHandle.Invoke(claude, [live, JsonSerializer.SerializeToElement(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id = "plan-call", name = "ExitPlanMode", input = new { plan = "# Plan\n- Edit a.txt" } } } } })]);
        if (claudeEvents.OfType<PlanProposed>().LastOrDefault()?.Text != "# Plan\n- Edit a.txt" || claudeEvents.OfType<ToolCall>().Any(t => t.CallId == "plan-call"))
            throw new Exception("A Claude plan must arrive as PlanProposed, not as a tool row.");
        Console.WriteLine("PASS: Claude plans are proposed for review, never declined.");
        var describe = typeof(ClaudeCode).Assembly.GetType("rhDock.Agents.ToolPreview")!.GetMethod("Describe")!;
        string Titled(string named, object args) => ((ValueTuple<string, string, string?>)describe.Invoke(null, [named, JsonSerializer.SerializeToElement(args)])!) is var (kind, title, _) ? $"{kind} {title}" : "";
        foreach (var (mcpTool, mcpInput, expected) in new (string, object, string)[]
        {
            ("execute", new { code = "const out = await tools.rhino.run_python({ script: `print(1)` });" }, "Other rhino: run_python"),
            ("execute", new { code = "const results = search({ query: \"layer\" });\nreturn results;" }, "Search Search MCP tools for layer"),
            ("mcp__rhino__get_context", new { }, "Other rhino: get_context"),
            ("execute", new { command = "ls" }, "Execute ls"),
            ("ToolSearch", new { query = "select:ExitPlanMode" }, "Search Load the ExitPlanMode tool"),
            ("ToolSearch", new { query = "notebook" }, "Search Search tools for notebook"),
            ("ExitPlanMode", new { plan = "# Plan" }, "Think Propose the plan"),
        })
            if (Titled(mcpTool, mcpInput) != expected) throw new Exception($"Tools must get readable names, MCP calls as server: tool; {mcpTool} became {Titled(mcpTool, mcpInput)}.");
        Console.WriteLine("PASS: MCP and native tool calls get readable names across agents.");

        using var acp = new AcpAgent("Fixture", "unused", [], _ => { });
        var tool = typeof(AcpAgent).GetMethod("Tool", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
        tool.Invoke(acp, ["fixture", JsonSerializer.SerializeToElement(new { toolCallId = "edit", title = "Edit a.txt", kind = "edit", status = "pending",
            content = new[] { new { type = "diff", oldText = "hello", newText = "goodbye" } } })]);
        var completed = (ToolCall)tool.Invoke(acp, ["fixture", JsonSerializer.SerializeToElement(new { toolCallId = "edit", status = "completed" })])!;
        if (completed is not { Kind: "Edit", Title: "Edit a.txt", Status: "Completed" } || !completed.Detail!.Contains("+goodbye"))
            throw new Exception("Partial ACP updates must keep the call's kind, title, and patch.");
        Console.WriteLine("PASS: partial ACP tool updates preserve their previews.");
        using var pi = new Pi("Pi", "unused", _ => { });
        var notices = new List<Notice>();
        pi.Event += e => { if (e is Notice notice) notices.Add(notice); };
        typeof(Pi).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pi,
            [JsonSerializer.SerializeToElement(new { type = "compaction_end", errorMessage = "Nothing to compact", aborted = false })]);
        if (notices.Any(n => n.Text == "Compacted the conversation.")) throw new Exception("Failed Pi compaction must not claim it succeeded.");
        var piDone = new TaskCompletionSource<TurnResult>();
        foreach (var (field, value) in new (string, object)[] { ("turn", piDone), ("stop", "error"), ("failure", "This operation was aborted") })
            typeof(Pi).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pi, value);
        ((Task)typeof(Pi).GetMethod("Settle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pi, ["fixture"])!).GetAwaiter().GetResult();
        if (piDone.Task.Result.Stop != "cancelled") throw new Exception("Pi's aborted operation must end as cancelled.");
        Console.WriteLine("PASS: Pi maps aborted operations to cancellation.");
        using var codex = new Codex("unused", _ => { });
        var events = new List<AgentEvent>();
        codex.Event += events.Add;
        var handle = typeof(Codex).GetMethod("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        handle.Invoke(codex, ["item/started", JsonSerializer.SerializeToElement(new { threadId = "root", turnId = "turn", item = new { type = "collabAgentToolCall", id = "spawn", tool = "spawnAgent", prompt = "Read files", receiverThreadIds = Array.Empty<string>() } })]);
        handle.Invoke(codex, ["thread/started", JsonSerializer.SerializeToElement(new { thread = new { id = "child", source = new { subagent = new { thread_spawn = new { parent_thread_id = "root" } } } } })]);
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "child", turnId = "child-turn", item = new { type = "commandExecution", id = "read", command = "cat a.txt", aggregatedOutput = "hello", status = "completed", commandActions = new[] { new { type = "read", name = "a.txt", path = "a.txt", command = "cat a.txt" } } } })]);
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "root", turnId = "turn", item = new { type = "collabAgentToolCall", id = "spawn", tool = "spawnAgent", prompt = "Read files", receiverThreadIds = new[] { "child" } } })]);
        if (!events.OfType<ToolCall>().Any(t => t is { SessionId: "root", Parent: "spawn", Kind: "Read", Status: "Completed" } && t.Detail!.Contains("hello")))
            throw new Exception("Codex child tool calls must reach the parent conversation with their previews.");
        Console.WriteLine("PASS: Codex subagent tool calls retain their parent and preview.");
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "root", item = new { type = "subAgentActivity", id = "activity-start", kind = "started", agentThreadId = "modern-child", agentPath = "/root/inspect" } })]);
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "modern-child", item = new { type = "commandExecution", id = "modern-read", command = "cat b.txt", aggregatedOutput = "child output", status = "completed" } })]);
        if (!events.OfType<ToolCall>().Any(t => t is { SessionId: "root", Parent: "activity-start", CallId: "modern-read" }))
            throw new Exception("subAgentActivity must connect its child tools to the parent row.");
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "root", item = new { type = "subAgentActivity", id = "activity-end", kind = "completed", agentThreadId = "modern-child", agentPath = "/root/inspect" } })]);
        if (events.OfType<ToolCall>().Last() is not { CallId: "activity-start", Status: "Completed" })
            throw new Exception("Child lifetime updates must finish the original row instead of adding another.");
        handle.Invoke(codex, ["item/completed", JsonSerializer.SerializeToElement(new { threadId = "root", item = new { type = "collabAgentToolCall", id = "wait", tool = "wait", receiverThreadIds = Array.Empty<string>() } })]);
        if (events.OfType<ToolCall>().Last().Title != "Wait for subagents") throw new Exception("Wait calls must have a recognizable label.");
        handle.Invoke(codex, ["turn/completed", JsonSerializer.SerializeToElement(new { threadId = "modern-child", turn = new { status = "completed" } })]);
        if (events.OfType<ToolCall>().Last() is not { SessionId: "root", CallId: "activity-start", Parent: null, Status: "Completed" })
            throw new Exception("Child turn completion must finish the original parent row without parenting it to itself.");
        Console.WriteLine("PASS: modern Codex subagent lifetime, nested tool previews, and wait labels.");
        var ask = typeof(Codex).GetMethod("Ask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var response = (Task<JsonNode?>)ask.Invoke(codex, ["item/tool/requestUserInput", JsonSerializer.SerializeToElement(new { threadId = "child", itemId = "question", questions = new[] { new { id = "color", header = "Color", question = "Which color?", isOther = true, options = new[] { new { label = "Blue", description = "Cool" }, new { label = "Red", description = "Warm" } } } } })])!;
        var asked = events.OfType<QuestionAsked>().Last();
        if (asked is not { SessionId: "root" } || asked.Fields[0] is not { Key: "color", Custom: true, Required: true }) throw new Exception("Codex questions must map their field IDs and reach the parent.");
        codex.ReplyQuestionAsync("root", asked.RequestId, new Dictionary<string, JsonNode?> { ["color"] = "Blue" }).GetAwaiter().GetResult();
        if (response.GetAwaiter().GetResult()?["answers"]?["color"]?["answers"]?[0]?.ToString() != "Blue") throw new Exception("Codex answers must use the native answers array envelope.");
        Console.WriteLine("PASS: Codex question choices, custom field, and native reply envelope.");
        ForkChecks();
    }

    // Side chats fork through each agent's own API; Claude Code forks with --resume <parent> --fork-session --session-id <fork>.
    static void ForkChecks()
    {
        if (Harnesses.All.Where(h => h.Features.Fork).Select(h => h.Id).ToArray() is not ["claude", "codex", "opencode", "pi"])
            throw new Exception("Claude Code, Codex, OpenCode, and Pi must advertise native forks; ACP agents and omp decide at run time.");
        if (OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-fork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var argsFile = Path.Combine(folder, "args.txt");
        var script = Path.Combine(folder, "claude");
        File.WriteAllText(script, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{argsFile}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var claude = new ClaudeCode(script, _ => { });
        var fork = claude.ForkAsync(folder, "parent-id").GetAwaiter().GetResult()!;
        _ = claude.PromptAsync(folder, fork, new AgentPrompt("hi", [], [], []));
        for (var i = 0; i < 250 && !File.Exists(argsFile); i++) Thread.Sleep(20);
        Thread.Sleep(100);
        var args = File.ReadAllLines(argsFile);
        var at = Array.IndexOf(args, "--resume");
        if (at < 0 || args[at + 1] != "parent-id" || !args.Contains("--fork-session") || args[Array.IndexOf(args, "--session-id") + 1] != fork)
            throw new Exception("A Claude side chat must resume the parent with --fork-session under the fork's own --session-id: " + string.Join(' ', args));
        Directory.Delete(folder, true);
        Console.WriteLine("PASS: harness fork flags and the Claude Code fork command line.");
    }
}
