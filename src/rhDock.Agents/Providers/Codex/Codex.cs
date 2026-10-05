using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// Codex through <c>codex app-server</c> (MonoCode's providers/codex): one JSON-RPC process for all threads, which are Codex's own
/// stored conversations. Approvals and questions arrive as requests from the server; <c>CodexProtocol.cs</c> translates its notifications.
/// Method and field names come from <c>codex app-server generate-ts</c> of Codex 0.160.
/// </summary>
public sealed partial class Codex(string executable, Action<string> log) : IAgentHarness
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, Conversation> threads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> waiting = new();
    private readonly SemaphoreSlim starting = new(1, 1);
    private JsonLines? lines;
    private JsonRpc? rpc;
    private AgentInfo? info;
    private Choice next = new(null, null);
    private Model[] models = [];
    private readonly ConcurrentDictionary<string, string> skillPaths = new();

    public event Action<AgentEvent>? Event;

    private sealed record Choice(string? Model, string? Effort);
    private sealed record Model(string Id, string Name, string[] Efforts, string? DefaultEffort, bool Default, bool Images);

    // One thread's turn, choices, and rewind state. Codex names user messages by item id; a rewind needs the turn holding it.
    private sealed class Conversation
    {
        public Choice Choice = new(null, null);
        public string? Turn, RevertTurn, RevertMessage, Last;
        public TaskCompletionSource<TurnResult>? Done;
        public TokenUsage Total = new(0, 0, 0, 0, 0), AtStart = new(0, 0, 0, 0, 0);
        public double? Window;
        public readonly Dictionary<string, string> TurnOf = [];
        public readonly Dictionary<string, JsonElement> Items = [];
    }

    public async Task<AgentInfo> StartAsync(string repository)
    {
        await starting.WaitAsync();
        try
        {
            if (lines is { Running: true } && rpc is not null && info is not null) return info;
            var process = AgentProcess.Launch(executable, ["app-server"], AgentProcess.RealPath(repository), AgentProcess.ExtendedPath(), log);
            var started = new JsonLines(process, log);
            var client = new JsonRpc(started, tagged: false) { OnNotification = Handle, OnRequest = Ask };
            started.Exited += () =>
            {
                foreach (var thread in threads.Values) thread.Done?.TrySetResult(new TurnResult("error", "Codex stopped. Details are in the agent log."));
                Event?.Invoke(new Exited("Codex stopped."));
            };
            (lines, rpc) = (started, client);
            await client.Request("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "rhdock", ["title"] = "rhDock", ["version"] = "0.5" },
                // Plan mode is a collaboration mode, which Codex keeps behind its experimental API.
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
            });
            await client.Notify("initialized");
            info = new AgentInfo("Codex", Harnesses.Version(executable), executable);
            return info;
        }
        finally { starting.Release(); }
    }

    public async Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository)
    {
        var result = await Rpc.Request("thread/list", new JsonObject { ["cwd"] = AgentProcess.RealPath(repository), ["limit"] = 50 });
        return Arr(result, "data").Select(t => new ConversationInfo(Str(t, "id")!, Str(t, "name"),
            Num(t, "updatedAt") is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at) : null, AgentPrompt.Title(Str(t, "preview")))).ToArray();
    }

    public async Task<string> CreateAsync(string repository)
    {
        Choice choice;
        lock (gate) { choice = next; next = new(null, null); }
        var parameters = Policy("ask", forThread: true);
        parameters["cwd"] = AgentProcess.RealPath(repository);
        if (choice.Model is { } model) parameters["model"] = model;
        var result = await Rpc.Request("thread/start", parameters);
        var id = Str(Obj(result, "thread"), "id")!;
        threads[id] = new Conversation { Choice = choice with { Model = choice.Model ?? Str(result, "model"), Effort = choice.Effort ?? Str(result, "reasoningEffort") } };
        return id;
    }

    public async Task LoadAsync(string repository, string sessionId)
    {
        var thread = threads.GetOrAdd(sessionId, _ => new Conversation());
        // Resuming subscribes this client to the thread's events; turns come page by page.
        var opened = await Rpc.Request("thread/resume", new JsonObject { ["threadId"] = sessionId, ["excludeTurns"] = true, ["approvalsReviewer"] = "user" });
        lock (gate) if (thread.Choice.Model is null) thread.Choice = new(Str(opened, "model"), Str(opened, "reasoningEffort"));
        string? cursor = null;
        var turns = new List<JsonElement>();
        do
        {
            var page = await Rpc.Request("thread/turns/list", new JsonObject { ["threadId"] = sessionId, ["sortDirection"] = "asc", ["itemsView"] = "full", ["limit"] = 100, ["cursor"] = cursor });
            turns.AddRange(Arr(page, "data"));
            cursor = Str(page, "nextCursor");
        }
        while (cursor is not null && turns.Count < 500);
        Replay(sessionId, thread, turns);
    }

    public async Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        var thread = threads.GetOrAdd(sessionId, _ => new Conversation());
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Done = done;
        thread.AtStart = thread.Total;
        // A rewind takes effect with the next prompt, so it can be undone until then.
        if (thread.RevertTurn is { } revert)
        {
            await Rpc.Request("thread/revert", new JsonObject { ["threadId"] = sessionId, ["beforeTurnId"] = revert });
            lock (gate) { thread.RevertTurn = null; thread.RevertMessage = null; }
        }
        if (prompt.Compact) await Rpc.Request("thread/compact/start", new JsonObject { ["threadId"] = sessionId });
        else
        {
            var parameters = Policy(prompt.Mode, forThread: false);
            parameters["threadId"] = sessionId;
            parameters["input"] = Input(prompt, repository);
            Choice choice;
            lock (gate) choice = thread.Choice;
            if (choice.Model is { } model) parameters["model"] = model;
            if (choice.Effort is { } effort) parameters["effort"] = effort;
            parameters["collaborationMode"] = new JsonObject
            {
                ["mode"] = prompt.Mode == "plan" ? "plan" : "default",
                ["settings"] = new JsonObject { ["model"] = choice.Model, ["reasoning_effort"] = choice.Effort, ["developer_instructions"] = null },
            };
            var started = await Rpc.Request("turn/start", parameters);
            thread.Turn = Str(Obj(started, "turn"), "id");
        }
        return await done.Task;
    }

    public async Task SteerAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        if (!threads.TryGetValue(sessionId, out var thread) || thread.Turn is not { } turn) throw new InvalidOperationException("No turn is running.");
        await Rpc.Request("turn/steer", new JsonObject { ["threadId"] = sessionId, ["expectedTurnId"] = turn, ["input"] = Input(prompt, repository) });
    }

    public async Task CancelAsync(string sessionId)
    {
        if (!threads.TryGetValue(sessionId, out var thread) || thread.Turn is not { } turn) return;
        foreach (var (id, call) in waiting) call.TrySetResult(new JsonObject { ["decision"] = "cancel" });
        await Rpc.Request("turn/interrupt", new JsonObject { ["threadId"] = sessionId, ["turnId"] = turn });
    }

    // Codex reverts the conversation only; rhDock restores the files from its own copy.
    public Task<bool> RewindAsync(string repository, string sessionId, string messageId)
    {
        var thread = threads.GetOrAdd(sessionId, _ => new Conversation());
        lock (gate)
        {
            if (!thread.TurnOf.TryGetValue(messageId, out var turn)) throw new InvalidOperationException("Codex has no turn for this message.");
            (thread.RevertTurn, thread.RevertMessage) = (turn, messageId);
        }
        return Task.FromResult(false);
    }

    public Task UndoRewindAsync(string repository, string sessionId)
    {
        if (threads.TryGetValue(sessionId, out var thread)) lock (gate) { thread.RevertTurn = null; thread.RevertMessage = null; }
        return Task.CompletedTask;
    }

    public Task ReplyPermissionAsync(string sessionId, string requestId, string? decision)
    {
        var once = onceOnly.TryRemove(requestId, out _);
        if (waiting.TryRemove(requestId, out var call))
            call.TrySetResult(new JsonObject { ["decision"] = decision switch { "AllowAlways" when !once => "acceptForSession", "AllowOnce" or "AllowAlways" => "accept", null => "cancel", _ => "decline" } });
        return Task.CompletedTask;
    }

    public Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer)
    {
        if (!waiting.TryRemove(requestId, out var call)) return Task.CompletedTask;
        var answers = new JsonObject();
        foreach (var (key, value) in answer ?? new Dictionary<string, JsonNode?>())
            answers[key] = new JsonObject { ["answers"] = value is JsonArray many ? new JsonArray([.. many.Select(v => (JsonNode?)JsonValue.Create(v?.ToString()))]) : new JsonArray(JsonValue.Create(value?.ToString())) };
        call.TrySetResult(new JsonObject { ["answers"] = answers });
        return Task.CompletedTask;
    }

    // The app-server never names a thread; Codex's own apps do. Like them, write the title once with an ephemeral, read-only,
    // low-effort exec (no stored session), then set it on the thread: Codex keeps it in session_index.jsonl and announces the rename.
    public async Task<string?> TitleAsync(string repository, string sessionId, string message)
    {
        var answer = Path.Combine(Path.GetTempPath(), $"rhdock-title-{Guid.NewGuid():N}.txt");
        string? title;
        try
        {
            await Task.Run(() => AgentProcess.Capture(executable, ["exec", "--ephemeral", "--skip-git-repo-check", "-s", "read-only", "-c", "model_reasoning_effort=\"low\"",
                "-o", answer, ConversationTitle.Prompt(message)], AgentProcess.RealPath(repository), TimeSpan.FromSeconds(90)));
            title = ConversationTitle.Clean(File.Exists(answer) ? await File.ReadAllTextAsync(answer) : null);
        }
        finally { File.Delete(answer); }
        if (title is null) return null;
        await Rpc.Request("thread/name/set", new JsonObject { ["threadId"] = sessionId, ["name"] = title });
        return title;
    }

    public async Task<AgentCatalog> CatalogAsync(string repository, string? sessionId)
    {
        if (models.Length == 0)
            models = Arr(await Rpc.Request("model/list", new JsonObject()), "data").Where(m => !Bool(m, "hidden")).Select(m => new Model(Str(m, "id")!, Str(m, "displayName") ?? Str(m, "id")!,
                Arr(m, "supportedReasoningEfforts").Select(e => Str(e, "reasoningEffort")!).ToArray(), Str(m, "defaultReasoningEffort"), Bool(m, "isDefault"),
                Arr(m, "inputModalities").Any(i => i.GetString() == "image"))).ToArray();
        var skills = Arr(await Rpc.Request("skills/list", new JsonObject { ["cwds"] = new JsonArray(AgentProcess.RealPath(repository)) }), "data")
            .SelectMany(entry => Arr(entry, "skills")).Where(s => Bool(s, "enabled"))
            .Select(s => { skillPaths[Str(s, "name")!] = Str(s, "path") ?? ""; return new SkillInfo(Str(s, "name")!, Str(s, "name")!, Str(s, "shortDescription") ?? Str(s, "description")); }).ToArray();
        Choice choice;
        lock (gate) choice = sessionId is not null && threads.TryGetValue(sessionId, out var thread) ? thread.Choice : next;
        var model = models.FirstOrDefault(m => m.Id == choice.Model) ?? models.FirstOrDefault(m => m.Default) ?? models.FirstOrDefault();
        var options = new List<ConfigOption> { new("model", "Model", "model", model?.Id, models.Select(m => new OptionChoice(m.Id, m.Name)).ToArray()) };
        if (model is { Efforts.Length: > 0 })
            options.Add(new("effort", "Effort", "thought_level", choice.Effort ?? model.DefaultEffort, model.Efforts.Select(OptionChoice.Effort).ToArray()));
        return new AgentCatalog(options, [], skills, model?.Images ?? true);
    }

    // Codex applies a model or effort with the next turn.
    public Task SetOptionAsync(string repository, string? sessionId, string optionId, string value)
    {
        lock (gate)
        {
            var thread = sessionId is null ? null : threads.GetOrAdd(sessionId, _ => new Conversation());
            var choice = thread?.Choice ?? next;
            choice = optionId switch { "model" => choice with { Model = value, Effort = null }, "effort" => choice with { Effort = value }, _ => choice };
            if (thread is null) next = choice; else thread.Choice = choice;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lines?.Dispose();
        lines = null;
        rpc = null;
    }

    private JsonRpc Rpc => rpc ?? throw new InvalidOperationException("Codex is not running.");

    // rhDock's modes as Codex approval and sandbox policies (MonoCode's runtimeModeToCodexConfig). Ask runs read-only, so every edit asks.
    private static JsonObject Policy(string mode, bool forThread)
    {
        var (approval, sandbox, policy) = mode switch
        {
            "edits" => ("on-request", "workspace-write", "workspaceWrite"),
            "all" => ("on-request", "danger-full-access", "dangerFullAccess"),
            "plan" => ("never", "read-only", "readOnly"),
            _ => ("untrusted", "read-only", "readOnly"),
        };
        var result = new JsonObject { ["approvalPolicy"] = approval, ["approvalsReviewer"] = "user" };
        if (forThread) result["sandbox"] = sandbox;
        else result["sandboxPolicy"] = new JsonObject { ["type"] = policy };
        return result;
    }

    private JsonArray Input(AgentPrompt prompt, string repository)
    {
        var input = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt.Tagged(), ["text_elements"] = new JsonArray() } };
        foreach (var file in prompt.Files) input.Add(new JsonObject { ["type"] = "mention", ["name"] = System.IO.Path.GetFileName(file), ["path"] = file });
        foreach (var image in prompt.Images) input.Add(new JsonObject { ["type"] = "image", ["url"] = $"data:{image.MimeType};base64,{image.Data}" });
        if (prompt.Skill is { } skill) input.Add(new JsonObject { ["type"] = "skill", ["name"] = skill, ["path"] = skillPaths.GetValueOrDefault(skill, "") });
        return input;
    }
}
