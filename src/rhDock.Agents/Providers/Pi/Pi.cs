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
/// Pi, and omp which forks it, through <c>--mode rpc</c> (MonoCode's providers/pi): one process per repository holding one session
/// at a time, commands and responses correlated by id, and session events on stdout. Pi runs tools without asking, by design, so
/// rhDock's permission modes do not apply; its extensions' dialogs arrive as questions. <c>PiProtocol.cs</c> translates the events.
/// Command names come from Pi 1.0.2's docs/rpc-commands.md.
/// </summary>
public sealed partial class Pi(string name, string executable, Action<string> log) : IAgentHarness
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> calls = new();
    private readonly ConcurrentDictionary<string, string> dialogs = new();
    private readonly SemaphoreSlim starting = new(1, 1);
    private JsonLines? lines;
    private string? repository, current;
    private AgentInfo? info;
    private TaskCompletionSource<TurnResult>? turn;
    private TokenUsage spent = new(0, 0, 0, 0, 0);
    private long nextId;

    public event Action<AgentEvent>? Event;

    public async Task<AgentInfo> StartAsync(string repository)
    {
        await starting.WaitAsync();
        try
        {
            if (lines is { Running: true } && this.repository == repository && info is not null) return info;
            lines?.Dispose();
            var process = AgentProcess.Launch(executable, ["--mode", "rpc"], AgentProcess.RealPath(repository), AgentProcess.ExtendedPath(), log);
            var started = new JsonLines(process, log);
            started.Received += Handle;
            started.Exited += () =>
            {
                if (lines != started) return;
                foreach (var call in calls.Values) call.TrySetException(new InvalidOperationException($"{name} stopped."));
                turn?.TrySetResult(new TurnResult("error", $"{name} stopped. Details are in the agent log."));
                Event?.Invoke(new Exited($"{name} stopped."));
            };
            (lines, this.repository, current) = (started, repository, null);
            var state = await Call(new JsonObject { ["type"] = "get_state" });
            current = Str(state, "sessionId");
            info = new AgentInfo(name, Harnesses.Version(executable), executable);
            return info;
        }
        finally { starting.Release(); }
    }

    public Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository)
    {
        var folder = SessionFolder(repository);
        if (!Directory.Exists(folder)) return Task.FromResult<IReadOnlyList<ConversationInfo>>([]);
        IReadOnlyList<ConversationInfo> list = new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(50)
            .Select(f => (File: f, Title: Title(f.FullName))).Where(c => c.Title.First is not null)
            .Select(c => new ConversationInfo(Id(c.File.Name), c.Title.Named, new DateTimeOffset(c.File.LastWriteTimeUtc), c.Title.First)).ToArray();
        return Task.FromResult(list);
    }

    // The process starts with an empty session; a new one is made only when that one has messages.
    public async Task<string> CreateAsync(string repository)
    {
        var state = await Call(new JsonObject { ["type"] = "get_state" });
        if ((Num(state, "messageCount") ?? 0) > 0)
        {
            await Call(new JsonObject { ["type"] = "new_session" });
            state = await Call(new JsonObject { ["type"] = "get_state" });
        }
        lock (gate) current = Str(state, "sessionId");
        return current!;
    }

    // clone copies the active branch into a new session file (with parentSession in its header) and switches to it.
    public async Task<string?> ForkAsync(string repository, string sessionId)
    {
        await Switch(repository, sessionId);
        if (Bool(await Call(new JsonObject { ["type"] = "clone" }), "cancelled")) return null;
        var id = Str(await Call(new JsonObject { ["type"] = "get_state" }), "sessionId");
        if (id is null || id == sessionId) return null;
        lock (gate) current = id;
        return id;
    }

    public async Task LoadAsync(string repository, string sessionId)
    {
        await Switch(repository, sessionId);
        var messages = await Call(new JsonObject { ["type"] = "get_messages" });
        Replay(sessionId, Arr(messages, "messages"));
        await Stats(sessionId, turnEnded: false);
    }

    public async Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        await Switch(repository, sessionId);
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        turn = done;
        spent = new(0, 0, 0, 0, 0);
        try
        {
            if (prompt.Compact)
            {
                try { await Call(new JsonObject { ["type"] = "compact" }, TimeSpan.FromMinutes(10)); }
                catch (InvalidOperationException ex) { return new TurnResult("error", ex.Message); }
                return new TurnResult("end_turn", null);
            }
            // Commands, prompt templates, and skills (skill:name) run from slash text.
            var text = prompt.Command is { } command ? $"/{command} {prompt.Text}".Trim() : prompt.Skill is { } skill ? $"/{skill} {prompt.WithFiles()}".Trim() : prompt.WithFiles();
            var accepted = await Call(Message("prompt", text, prompt.Images));
            if (Str(accepted, "disposition") == "handled") return new TurnResult("end_turn", null);
            return await done.Task;
        }
        finally { turn = null; }
    }

    // Pi delivers a steer after the running step's tool calls, before its next model request.
    public Task SteerAsync(string repository, string sessionId, AgentPrompt prompt) => Call(Message("steer", prompt.WithFiles(), prompt.Images));

    public async Task CancelAsync(string sessionId)
    {
        foreach (var id in dialogs.Keys) await Reply(id, new JsonObject { ["cancelled"] = true });
        await Call(new JsonObject { ["type"] = "abort" });
    }

    public Task<bool> RewindAsync(string repository, string sessionId, string messageId) => throw new NotSupportedException($"{name} cannot rewind in rhDock yet.");
    public Task UndoRewindAsync(string repository, string sessionId) => Task.CompletedTask;
    public Task ReplyPermissionAsync(string sessionId, string requestId, string? decision) => Task.CompletedTask;

    // An extension dialog: select and input take a value, confirm a yes or no.
    public Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer)
    {
        if (!dialogs.TryRemove(requestId, out var method)) return Task.CompletedTask;
        var value = answer?.Values.FirstOrDefault();
        return Reply(requestId, answer is null || value is null ? new JsonObject { ["cancelled"] = true }
            : method == "confirm" ? new JsonObject { ["confirmed"] = value.GetValueKind() == JsonValueKind.True }
            : new JsonObject { ["value"] = value.ToString() });
    }

    public async Task<AgentCatalog> CatalogAsync(string repository, string? sessionId)
    {
        var state = await Call(new JsonObject { ["type"] = "get_state" });
        var models = Arr(await Call(new JsonObject { ["type"] = "get_available_models" }), "models").ToArray();
        var levels = Arr(await Call(new JsonObject { ["type"] = "get_available_thinking_levels" }), "levels").Select(l => l.GetString()!).ToArray();
        var commands = Arr(await Call(new JsonObject { ["type"] = "get_commands" }), "commands").ToArray();
        var model = Obj(state, "model");
        var options = new List<ConfigOption>
        {
            new("model", "Model", "model", model.ValueKind == JsonValueKind.Object ? $"{Str(model, "provider")}/{Str(model, "id")}" : null,
                models.Select(m => new OptionChoice($"{Str(m, "provider")}/{Str(m, "id")}", Str(m, "name") ?? Str(m, "id"), Str(m, "provider"))).ToArray()),
        };
        if (levels.Length > 1) options.Add(new("effort", "Effort", "thought_level", Str(state, "thinkingLevel"), levels.Select(OptionChoice.Effort).ToArray()));
        return new AgentCatalog(options,
            commands.Where(c => Str(c, "source") != "skill").Select(c => new CommandInfo(Str(c, "name")!, Str(c, "description"))).ToArray(),
            commands.Where(c => Str(c, "source") == "skill").Select(c => new SkillInfo(Str(c, "name")!, Str(c, "name")!.Replace("skill:", ""), Str(c, "description"))).ToArray(),
            model.ValueKind != JsonValueKind.Object || Arr(model, "input").Any(i => i.GetString() == "image"));
    }

    // Pi keeps the model and thinking level with the session it is in, which is the conversation or the next one.
    public async Task SetOptionAsync(string repository, string? sessionId, string optionId, string value)
    {
        if (sessionId is not null) await Switch(repository, sessionId);
        if (optionId == "model" && value.IndexOf('/') is > 0 and var slash)
            await Call(new JsonObject { ["type"] = "set_model", ["provider"] = value[..slash], ["modelId"] = value[(slash + 1)..] });
        else if (optionId == "effort") await Call(new JsonObject { ["type"] = "set_thinking_level", ["level"] = value });
    }

    public void Dispose()
    {
        lines?.Dispose();
        lines = null;
    }

    private async Task Switch(string repository, string sessionId)
    {
        if (this.repository != repository || lines is not { Running: true }) await StartAsync(repository);
        if (current == sessionId) return;
        var file = new DirectoryInfo(SessionFolder(repository)).GetFiles($"*_{sessionId}.jsonl").FirstOrDefault()
            ?? throw new InvalidOperationException($"{name} has no stored session {sessionId}.");
        await Call(new JsonObject { ["type"] = "switch_session", ["sessionPath"] = file.FullName });
        lock (gate) current = sessionId;
    }

    private async Task<JsonElement> Call(JsonObject command, TimeSpan? timeout = null)
    {
        var client = lines ?? throw new InvalidOperationException($"{name} is not running.");
        var id = "rhdock_" + Interlocked.Increment(ref nextId);
        command["id"] = id;
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = done;
        try
        {
            await client.Send(command);
            return await done.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
        }
        finally { calls.TryRemove(id, out _); }
    }

    private Task Reply(string id, JsonObject response)
    {
        response["type"] = "extension_ui_response";
        response["id"] = id;
        return lines?.Send(response) ?? Task.CompletedTask;
    }

    private static JsonObject Message(string type, string text, IReadOnlyList<AgentImage> images)
    {
        var command = new JsonObject { ["type"] = type, ["message"] = text };
        if (images.Count > 0) command["images"] = new JsonArray([.. images.Select(i => (JsonNode)new JsonObject { ["type"] = "image", ["data"] = i.Data, ["mimeType"] = i.MimeType })]);
        return command;
    }

    // Pi keeps a folder's sessions under ~/.pi/agent/sessions/--<path>--, the path without its leading separator and with / \ : as dashes.
    private string SessionFolder(string repository)
    {
        var root = Environment.GetEnvironmentVariable(name == "omp" ? "OMP_CODING_AGENT_DIR" : "PI_CODING_AGENT_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name == "omp" ? ".omp" : ".pi", "agent");
        var path = AgentProcess.RealPath(repository).TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return Path.Combine(root, "sessions", $"--{path}--");
    }

    // Files are named <timestamp>_<session id>.jsonl.
    private static string Id(string file) => Path.GetFileNameWithoutExtension(file) is var stem && stem.IndexOf('_') is var at and >= 0 ? stem[(at + 1)..] : stem;
}
