using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// An agent that speaks the Agent Client Protocol on stdio (MonoCode's core/acp.ts): Cursor, Grok Build, fx, Hermes, and Antigravity.
/// One process, JSON-RPC 2.0, <c>session/update</c> for everything that happens, and <c>session/request_permission</c> for approvals.
/// rhDock offers no file system or terminal to the agent, so it uses its own tools. <c>AcpProtocol.cs</c> translates the updates.
/// </summary>
public sealed partial class AcpAgent(string name, string executable, IReadOnlyList<string> args, Action<string> log) : IAgentHarness
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> waiting = new();
    private readonly ConcurrentDictionary<string, (string Kind, string Id)[]> options = new();
    private readonly SemaphoreSlim starting = new(1, 1);
    private JsonLines? lines;
    private JsonRpc? rpc;
    private AgentInfo? info;
    private string? repository, spare;
    private bool canList, canLoad, images = true;

    public event Action<AgentEvent>? Event;

    // What the agent announced for one session: its options, commands, and the turn in progress.
    private sealed class Session
    {
        public JsonElement Models, Modes;
        public List<JsonElement> Config = [];
        public CommandInfo[] Commands = [];
        public bool Replaying;
        public (System.Text.StringBuilder Text, List<AgentImage> Images)? User;
    }

    public async Task<AgentInfo> StartAsync(string repository)
    {
        await starting.WaitAsync();
        try
        {
            if (lines is { Running: true } && this.repository == repository && info is not null) return info;
            lines?.Dispose();
            var process = AgentProcess.Launch(executable, args, AgentProcess.RealPath(repository), AgentProcess.ExtendedPath(), log);
            var started = new JsonLines(process, log);
            var client = new JsonRpc(started, tagged: true) { OnNotification = Handle, OnRequest = Ask };
            started.Exited += () => { if (lines == started) Event?.Invoke(new Exited($"{name} stopped.")); };
            (lines, rpc, this.repository, spare) = (started, client, repository, null);
            tools.Clear();
            var init = await client.Request("initialize", new JsonObject
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject { ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false }, ["terminal"] = false },
                ["clientInfo"] = new JsonObject { ["name"] = "rhdock", ["version"] = "0.5" },
            }, TimeSpan.FromMinutes(2));
            var capabilities = Obj(init, "agentCapabilities");
            canLoad = Bool(capabilities, "loadSession");
            canList = Obj(Obj(capabilities, "sessionCapabilities"), "list").ValueKind == JsonValueKind.Object;
            images = Bool(Obj(capabilities, "promptCapabilities"), "image");
            var agent = Obj(init, "agentInfo");
            info = new AgentInfo(name, Str(agent, "version") ?? Harnesses.Version(executable), executable);
            return info;
        }
        finally { starting.Release(); }
    }

    public async Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository)
    {
        if (!canList) return [];
        var result = await Rpc.Request("session/list", new JsonObject { ["cwd"] = AgentProcess.RealPath(repository) });
        return Arr(result, "sessions").Select(s => new ConversationInfo(Str(s, "sessionId")!, Str(s, "title"),
            DateTimeOffset.TryParse(Str(s, "updatedAt"), out var at) ? at : null)).ToArray();
    }

    // A session made to read the catalog before the first prompt becomes the new conversation.
    public async Task<string> CreateAsync(string repository)
    {
        string? id;
        lock (gate) { id = spare; spare = null; }
        return id ?? await NewSession(repository);
    }

    public async Task LoadAsync(string repository, string sessionId)
    {
        if (!canLoad) throw new NotSupportedException($"{name} cannot reopen conversations.");
        var session = sessions.GetOrAdd(sessionId, _ => new Session());
        session.Replaying = true;
        try { Remember(session, await Rpc.Request("session/load", new JsonObject { ["sessionId"] = sessionId, ["cwd"] = AgentProcess.RealPath(repository), ["mcpServers"] = new JsonArray() }, TimeSpan.FromMinutes(2))); }
        finally { Flush(sessionId, session); session.Replaying = false; }
    }

    public async Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        var blocks = new JsonArray();
        var text = prompt.Compact ? "/compact" : prompt.Command is { } command ? $"/{command} {prompt.Text}".Trim() : prompt.Skill is { } skill ? $"/{skill} {prompt.Tagged()}" : prompt.Tagged();
        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var file in prompt.Files) blocks.Add(new JsonObject { ["type"] = "resource_link", ["uri"] = new Uri(file).AbsoluteUri, ["name"] = System.IO.Path.GetFileName(file) });
        foreach (var image in prompt.Images) blocks.Add(new JsonObject { ["type"] = "image", ["mimeType"] = image.MimeType, ["data"] = image.Data });
        try
        {
            var result = await Rpc.Request("session/prompt", new JsonObject { ["sessionId"] = sessionId, ["prompt"] = blocks }, Timeout.InfiniteTimeSpan);
            return new TurnResult(Str(result, "stopReason") == "cancelled" ? "cancelled" : "end_turn", null);
        }
        catch (InvalidOperationException ex) { return new TurnResult("error", ex.Message); }
    }

    public Task SteerAsync(string repository, string sessionId, AgentPrompt prompt) => throw new NotSupportedException($"{name} cannot take a message during a turn.");

    public async Task CancelAsync(string sessionId)
    {
        foreach (var (_, call) in waiting) call.TrySetResult(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } });
        await Rpc.Notify("session/cancel", new JsonObject { ["sessionId"] = sessionId });
    }

    public Task<bool> RewindAsync(string repository, string sessionId, string messageId) => throw new NotSupportedException($"{name} cannot rewind.");
    public Task UndoRewindAsync(string repository, string sessionId) => Task.CompletedTask;

    // The agent offers its own options by kind; Always allow falls back to once, and Reject to rejecting always, when the agent lacks one.
    public Task ReplyPermissionAsync(string sessionId, string requestId, string? decision)
    {
        options.TryRemove(requestId, out var offered);
        string? Pick(params string[] kinds) => kinds.Select(k => offered?.FirstOrDefault(o => o.Kind == k).Id).FirstOrDefault(i => i is not null);
        var chosen = decision switch
        {
            "AllowAlways" => Pick("allow_always", "allow_once"),
            "AllowOnce" => Pick("allow_once", "allow_always"),
            "RejectOnce" => Pick("reject_once", "reject_always"),
            _ => null,
        };
        if (waiting.TryRemove(requestId, out var call))
            call.TrySetResult(new JsonObject { ["outcome"] = chosen is null ? new JsonObject { ["outcome"] = "cancelled" } : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen } });
        return Task.CompletedTask;
    }

    public Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer) => Task.CompletedTask;

    public async Task<AgentCatalog> CatalogAsync(string repository, string? sessionId)
    {
        if (sessionId is null)
        {
            lock (gate) sessionId = spare;
            if (sessionId is null) { sessionId = await NewSession(repository); lock (gate) spare = sessionId; }
        }
        var session = sessions.GetOrAdd(sessionId, _ => new Session());
        var options = new List<ConfigOption>();
        lock (gate)
        {
            // Newer agents describe everything as config options; older ones announce models and modes.
            options.AddRange(session.Config.Select(o => new ConfigOption(Str(o, "id")!, Str(o, "name") ?? Str(o, "id")!, Str(o, "category") ?? "", Str(o, "currentValue"),
                Arr(o, "options").Select(c => new OptionChoice(Str(c, "value")!, Str(c, "name"), Group(Str(c, "description")))).ToArray())));
            if (!options.Any(o => o.Category == "model") && session.Models.ValueKind == JsonValueKind.Object)
                options.Add(new("model", "Model", "model", Str(session.Models, "currentModelId"),
                    Arr(session.Models, "availableModels").Select(m => new OptionChoice(Str(m, "modelId")!, Str(m, "name"), Group(Str(m, "description")))).ToArray()));
            if (!options.Any(o => o.Category == "mode") && session.Modes.ValueKind == JsonValueKind.Object && Arr(session.Modes, "availableModes").Count() > 1)
                options.Add(new("mode", "Mode", "mode", Str(session.Modes, "currentModeId"), Arr(session.Modes, "availableModes").Select(m => new OptionChoice(Str(m, "id")!, Str(m, "name"))).ToArray()));
        }
        return new AgentCatalog(options, session.Commands, [], images, canList, canLoad);
    }

    // The new conversation's session is the spare one, so options set before the first prompt apply to it.
    public async Task SetOptionAsync(string repository, string? sessionId, string optionId, string value)
    {
        lock (gate) sessionId ??= spare;
        if (sessionId is null) return;
        var session = sessions.GetOrAdd(sessionId, _ => new Session());
        bool configured;
        lock (gate) configured = session.Config.Any(o => Str(o, "id") == optionId);
        if (configured) Remember(session, await Rpc.Request("session/set_config_option", new JsonObject { ["sessionId"] = sessionId, ["configId"] = optionId, ["value"] = value }));
        else if (optionId == "model")
        {
            await Rpc.Request("session/set_model", new JsonObject { ["sessionId"] = sessionId, ["modelId"] = value });
            lock (gate) session.Models = Replace(session.Models, "currentModelId", value);
        }
        else if (optionId == "mode")
        {
            await Rpc.Request("session/set_mode", new JsonObject { ["sessionId"] = sessionId, ["modeId"] = value });
            lock (gate) session.Modes = Replace(session.Modes, "currentModeId", value);
        }
    }

    public void Dispose()
    {
        lines?.Dispose();
        lines = null;
        rpc = null;
    }

    private JsonRpc Rpc => rpc ?? throw new InvalidOperationException($"{name} is not running.");

    private async Task<string> NewSession(string repository)
    {
        var result = await Rpc.Request("session/new", new JsonObject { ["cwd"] = AgentProcess.RealPath(repository), ["mcpServers"] = new JsonArray() }, TimeSpan.FromMinutes(2));
        var id = Str(result, "sessionId")!;
        Remember(sessions.GetOrAdd(id, _ => new Session()), result);
        return id;
    }

    // session/new, session/load, and set_config_option answer with the session's models, modes, and options.
    private void Remember(Session session, JsonElement result)
    {
        lock (gate)
        {
            if (Obj(result, "models") is { ValueKind: JsonValueKind.Object } models) session.Models = models.Clone();
            if (Obj(result, "modes") is { ValueKind: JsonValueKind.Object } modes) session.Modes = modes.Clone();
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("configOptions", out var config) && config.ValueKind == JsonValueKind.Array)
                session.Config = config.EnumerateArray().Select(c => c.Clone()).ToList();
        }
    }

    private static JsonElement Replace(JsonElement e, string property, string value)
    {
        if (e.ValueKind != JsonValueKind.Object) return e;
        var node = JsonNode.Parse(e.GetRawText())!.AsObject();
        node[property] = value;
        return JsonSerializer.SerializeToElement(node);
    }

    // Hermes describes a model as "Provider: OpenRouter".
    private static string? Group(string? description) => description?.StartsWith("Provider: ", StringComparison.Ordinal) == true ? description["Provider: ".Length..] : null;
}
