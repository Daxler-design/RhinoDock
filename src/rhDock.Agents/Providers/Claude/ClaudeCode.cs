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
/// Claude Code through its stream-JSON mode (MonoCode's providers/claude): one CLI process for the open conversation, prompts and
/// answers on stdin, events on stdout, and <c>control_request</c> for permissions, models, interrupts, and file rewinds. Conversations
/// are Claude Code's own files in <c>~/.claude/projects</c>; <c>ClaudeProtocol.cs</c> translates both.
/// </summary>
public sealed partial class ClaudeCode(string executable, Action<string> log) : IAgentHarness
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, Conversation> conversations = new();
    private readonly ConcurrentDictionary<string, Request> requests = new();
    private readonly SemaphoreSlim spawning = new(1, 1);
    private Live? live;
    private AgentInfo? info;
    private Choice next = new(null, null);
    private (string Repository, AgentCatalog Catalog)? catalog;
    private long nextControl;

    public event Action<AgentEvent>? Event;

    private sealed record Choice(string? Model, string? Effort);
    private sealed record Request(string ToolName, JsonElement Input, JsonElement Suggestions);
    private sealed record ModelInfo(string Value, string Name, string[] Efforts);

    // A conversation's choices and rewind state. Model, effort, and mode change on the running CLI; a rewind needs a new one.
    private sealed class Conversation
    {
        public Choice Choice = new(null, null);
        public string? Mode;
        public string? ResumeAt, RewoundTo;
        // A fork not yet prompted: its CLI resumes this parent with --fork-session under the fork's own id.
        public string? ForkOf;
        public bool Stored;
        public double Cost;
        public string? Last;
    }

    // The running CLI and the turn it is in.
    private sealed class Live
    {
        public required JsonLines Lines;
        public required string Repository, SessionId, Key;
        public bool Prompted, Cancelled;
        public TaskCompletionSource<TurnResult>? Turn;
        public ModelInfo[] Models = [];
        // Each CLI's own pending control requests, so the old CLI exiting fails only its own.
        public readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Controls = new();
        public string? Model;
        public double? Window;
    }

    public async Task<AgentInfo> StartAsync(string repository)
    {
        info ??= new AgentInfo("Claude Code", Harnesses.Version(executable), executable);
        lock (gate) if (live?.Repository == repository) return info;
        // A CLI for an empty conversation, so the catalog loads now and the first prompt starts at once.
        await Ensure(repository, Guid.NewGuid().ToString(), stored: false);
        return info;
    }

    public Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository)
    {
        var folder = ProjectFolder(repository);
        if (!Directory.Exists(folder)) return Task.FromResult<IReadOnlyList<ConversationInfo>>([]);
        // ponytail: newest 50 files, titled by their first prompt; read a summary entry if long files need better titles.
        IReadOnlyList<ConversationInfo> list = new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(50)
            .Select(f => (File: f, Title: Title(f.FullName))).Where(c => c.Title.First is not null)
            .Select(c => new ConversationInfo(Path.GetFileNameWithoutExtension(c.File.Name), c.Title.Named, new DateTimeOffset(c.File.LastWriteTimeUtc), c.Title.First)).ToArray();
        return Task.FromResult(list);
    }

    public async Task<string> CreateAsync(string repository)
    {
        Choice choice;
        lock (gate) { choice = next; next = new(null, null); }
        // The CLI started for the catalog has an unused conversation id; take it.
        string id;
        lock (gate) id = live is { Prompted: false } l && l.Repository == repository && !conversations.ContainsKey(l.SessionId) ? l.SessionId : Guid.NewGuid().ToString();
        conversations[id] = new Conversation { Choice = choice };
        lock (gate) { tools.Clear(); tasks.Clear(); }
        await Ensure(repository, id, stored: false);
        return id;
    }

    public Task LoadAsync(string repository, string sessionId)
    {
        var conversation = conversations.GetOrAdd(sessionId, _ => new Conversation());
        conversation.Stored = true;
        Replay(repository, sessionId, conversation);
        return Task.CompletedTask;
    }

    public async Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        var conversation = conversations.GetOrAdd(sessionId, _ => new Conversation { Stored = true });
        conversation.Mode = prompt.Mode;
        var current = await Ensure(repository, sessionId, conversation.Stored);
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        current.Turn = done;
        current.Cancelled = false;
        try { await Mode(current, prompt.Mode); }
        catch (InvalidOperationException ex)
        {
            log("set_permission_mode " + ex.Message);
            // Auto needs an account, model, and policy that allow it; say so rather than quietly asking instead.
            if (prompt.Mode == "auto") { conversation.Mode = "ask"; Event?.Invoke(new ModeUnavailable(sessionId, "auto", "Claude Code did not accept Auto mode (" + ex.Message + ")")); try { await Mode(current, "ask"); } catch (InvalidOperationException) { } }
        }
        current.Prompted = true;
        conversation.Stored = true;
        // Claude Code compacts, runs commands, and loads skills from slash text.
        var text = prompt.Compact ? "/compact" : prompt.Command is { } command ? $"/{command} {prompt.Text}".Trim() : prompt.Skill is { } skill ? $"/{skill} {prompt.WithFiles()}".Trim() : prompt.WithFiles();
        await current.Lines.Send(UserMessage(text, prompt.Images));
        var result = await done.Task;
        lock (gate) { conversation.ResumeAt = null; conversation.RewoundTo = null; conversation.ForkOf = null; }
        return result;
    }

    // Claude Code forks when a CLI resumes the parent with --fork-session; the fork's file appears with its first prompt.
    public Task<string?> ForkAsync(string repository, string sessionId)
    {
        Choice choice;
        lock (gate) choice = conversations.TryGetValue(sessionId, out var parent) ? parent.Choice : next;
        var id = Guid.NewGuid().ToString();
        conversations[id] = new Conversation { Choice = choice, Stored = true, ForkOf = sessionId };
        return Task.FromResult<string?>(id);
    }

    // Claude Code reads a message sent during a turn at its next step.
    public async Task SteerAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        Live? current;
        lock (gate) current = live?.SessionId == sessionId ? live : null;
        if (current?.Turn is null) throw new InvalidOperationException("No turn is running.");
        await current.Lines.Send(UserMessage(prompt.WithFiles(), prompt.Images));
    }

    public async Task CancelAsync(string sessionId)
    {
        Live? current;
        lock (gate) current = live?.SessionId == sessionId ? live : null;
        if (current is null) return;
        current.Cancelled = true;
        foreach (var (id, _) in requests) await Respond(current, id, new JsonObject { ["behavior"] = "deny", ["message"] = "The user stopped the turn." });
        requests.Clear();
        await Control(current, new JsonObject { ["subtype"] = "interrupt" });
    }

    // Claude Code restores the files itself; the conversation resumes from the message before the prompt at the next prompt.
    public async Task<bool> RewindAsync(string repository, string sessionId, string messageId)
    {
        var conversation = conversations.GetOrAdd(sessionId, _ => new Conversation { Stored = true });
        var before = Before(repository, sessionId, messageId) ?? throw new InvalidOperationException("Claude Code cannot rewind to before its first message; start a new conversation instead.");
        var restored = false;
        try
        {
            var current = await Ensure(repository, sessionId, stored: true);
            var answer = await Control(current, new JsonObject { ["subtype"] = "rewind_files", ["user_message_id"] = messageId });
            restored = Bool(answer, "canRewind");
        }
        catch (InvalidOperationException ex) { log("rewind_files " + ex.Message); }
        lock (gate) { conversation.ResumeAt = before; conversation.RewoundTo = messageId; }
        return restored;
    }

    public Task UndoRewindAsync(string repository, string sessionId)
    {
        if (conversations.TryGetValue(sessionId, out var conversation)) lock (gate) { conversation.ResumeAt = null; conversation.RewoundTo = null; }
        return Task.CompletedTask;
    }

    public async Task ReplyPermissionAsync(string sessionId, string requestId, string? decision)
    {
        if (!requests.TryRemove(requestId, out var request) || CurrentFor(sessionId) is not { } current) return;
        var allow = decision is "AllowOnce" or "AllowAlways";
        var response = new JsonObject { ["behavior"] = allow ? "allow" : "deny" };
        if (allow) response["updatedInput"] = JsonNode.Parse(request.Input.GetRawText());
        else response["message"] = "The user declined this tool call.";
        if (decision == "AllowAlways" && request.Suggestions.ValueKind == JsonValueKind.Array) response["updatedPermissions"] = JsonNode.Parse(request.Suggestions.GetRawText());
        await Respond(current, requestId, response);
    }

    // AskUserQuestion answers travel as the tool's input, keyed by question text.
    public async Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer)
    {
        if (!requests.TryRemove(requestId, out var request) || CurrentFor(sessionId) is not { } current) return;
        if (answer is null) { await Respond(current, requestId, new JsonObject { ["behavior"] = "deny", ["message"] = "The user skipped the question." }); return; }
        var answers = new JsonObject();
        foreach (var (key, value) in answer)
            answers[key] = value is JsonArray many ? string.Join(", ", many.Select(v => v?.ToString())) : value?.ToString();
        var input = JsonNode.Parse(request.Input.GetRawText())!.AsObject();
        input["answers"] = answers;
        await Respond(current, requestId, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input });
    }

    public async Task<AgentCatalog> CatalogAsync(string repository, string? sessionId)
    {
        Live? current;
        lock (gate) current = live;
        if (current is null || current.Repository != repository) current = await Ensure(repository, Guid.NewGuid().ToString(), stored: false);
        AgentCatalog commands;
        lock (gate) commands = catalog is { } c && c.Repository == repository ? c.Catalog : new AgentCatalog([], [], [], true);
        if (commands.Commands.Count == 0)
        {
            var init = await Control(current, new JsonObject { ["subtype"] = "initialize" });
            var listed = await Control(current, new JsonObject { ["subtype"] = "list_models" });
            current.Models = Arr(listed, "models").Where(m => !Bool(m, "disabled"))
                .Select(m => new ModelInfo(Str(m, "value")!, ModelName(m), Arr(m, "supportedEffortLevels").Select(e => e.GetString()!).ToArray())).ToArray();
            commands = new AgentCatalog([], Arr(init, "commands").Select(c => new CommandInfo(Str(c, "name")!, Str(c, "description"))).ToArray(), [], true);
            lock (gate) catalog = (repository, commands);
        }
        Choice choice;
        lock (gate) choice = sessionId is not null && conversations.TryGetValue(sessionId, out var conversation) ? conversation.Choice : next;
        var model = choice.Model ?? "default";
        var options = new List<ConfigOption> { new("model", "Model", "model", model, current.Models.Select(m => new OptionChoice(m.Value, m.Name)).ToArray()) };
        if (current.Models.FirstOrDefault(m => m.Value == model) is { Efforts.Length: > 0 } chosen)
            options.Add(new("effort", "Effort", "thought_level", choice.Effort ?? "default", [new("default", "Default"), .. chosen.Efforts.Select(OptionChoice.Effort)]));
        return commands with { Options = options };
    }

    public async Task SetOptionAsync(string repository, string? sessionId, string optionId, string value)
    {
        Conversation? conversation = sessionId is null ? null : conversations.GetOrAdd(sessionId, _ => new Conversation { Stored = true });
        lock (gate)
        {
            var choice = conversation?.Choice ?? next;
            choice = optionId switch { "model" => choice with { Model = value }, "effort" => choice with { Effort = value == "default" ? null : value }, _ => choice };
            if (conversation is null) next = choice; else conversation.Choice = choice;
        }
        // Both apply to the running CLI; Claude Code takes effort as a flag setting, so no restart is needed.
        if (CurrentFor(sessionId) is not { } current) return;
        var setting = value == "default" ? null : value;
        if (optionId == "model") await Control(current, new JsonObject { ["subtype"] = "set_model", ["model"] = setting });
        else if (optionId == "effort") await Control(current, new JsonObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JsonObject { ["effortLevel"] = setting } });
    }

    public void Dispose()
    {
        Live? old;
        lock (gate) { old = live; live = null; }
        old?.Lines.Dispose();
        old?.Turn?.TrySetResult(new TurnResult("error", "Claude Code stopped."));
    }

    private Live? CurrentFor(string? sessionId)
    {
        lock (gate) return live is { } l && (sessionId is null || l.SessionId == sessionId) ? l : null;
    }

    // The CLI for this conversation and resume point, started if the running one differs. A new CLI takes the effort as a flag.
    private async Task<Live> Ensure(string repository, string sessionId, bool stored)
    {
        await spawning.WaitAsync();
        try
        {
            var conversation = conversations.TryGetValue(sessionId, out var c) ? c : null;
            Choice choice;
            lock (gate) choice = conversation?.Choice ?? next;
            var key = conversation?.ResumeAt ?? "";
            lock (gate) if (live is { } same && same.Repository == repository && same.SessionId == sessionId && same.Key == key && same.Lines.Running) return same;
            Live? old;
            lock (gate) old = live;
            old?.Lines.Dispose();
            var args = new List<string>
            {
                "--output-format", "stream-json", "--verbose", "--input-format", "stream-json", "--include-partial-messages", "--replay-user-messages",
                "--permission-prompt-tool", "stdio", "--setting-sources=user,project,local", "--permission-mode", PermissionMode(conversation?.Mode),
            };
            if (choice.Model is { } model && model != "default") args.AddRange(["--model", model]);
            if (choice.Effort is { } effort) args.AddRange(["--effort", effort]);
            if (conversation?.ForkOf is { } parent) args.AddRange(["--resume", parent, "--fork-session", "--session-id", sessionId]);
            else if (stored) { args.AddRange(["--resume", sessionId]); if (conversation?.ResumeAt is { } at) args.AddRange(["--resume-session-at", at]); }
            else args.AddRange(["--session-id", sessionId]);
            var process = AgentProcess.Launch(executable, args, AgentProcess.RealPath(repository), AgentProcess.ExtendedPath(), log);
            var lines = new JsonLines(process, log);
            var started = new Live { Lines = lines, Repository = repository, SessionId = sessionId, Key = key, Prompted = stored, Models = old?.Models ?? [] };
            lines.Received += line => Handle(started, line);
            lines.Exited += () =>
            {
                started.Turn?.TrySetResult(new TurnResult("error", "Claude Code stopped. Details are in the agent log."));
                foreach (var call in started.Controls.Values) call.TrySetException(new InvalidOperationException("Claude Code stopped."));
            };
            lock (gate) live = started;
            return started;
        }
        finally { spawning.Release(); }
    }

    private Task Mode(Live current, string mode) =>
        Control(current, new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = PermissionMode(mode) });

    // rhDock's modes are Claude Code's own; Allow all needs the CLI's opt-in flag, which a running CLI may lack, so it asks and rhDock approves.
    private static string PermissionMode(string? mode) => mode switch { "edits" => "acceptEdits", "plan" => "plan", "auto" => "auto", _ => "default" };

    private async Task<JsonElement> Control(Live current, JsonObject request)
    {
        var id = "rhdock_" + Interlocked.Increment(ref nextControl);
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        current.Controls[id] = done;
        try
        {
            await current.Lines.Send(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { current.Controls.TryRemove(id, out _); }
    }

    private static Task Respond(Live current, string requestId, JsonObject response) =>
        current.Lines.Send(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response } });

    private static JsonObject UserMessage(string text, IReadOnlyList<AgentImage> images)
    {
        var content = new JsonArray();
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images)
            content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType == "image/jpg" ? "image/jpeg" : image.MimeType, ["data"] = image.Data } });
        return new JsonObject { ["type"] = "user", ["session_id"] = "", ["parent_tool_use_id"] = null, ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
    }

    // Claude Code keeps a repository's conversations in a folder named after its real path, with every other character a dash.
    private static string ProjectFolder(string repository) => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects", string.Concat(AgentProcess.RealPath(repository).Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')));

    // The catalog calls an alias "Opus" and its description "Opus 5 · …"; the description names the version (MonoCode's pickerName).
    private static string ModelName(JsonElement m)
    {
        var name = Str(m, "displayName") ?? Str(m, "value")!;
        var head = (Str(m, "description") ?? "").Split('·')[0].Trim();
        if (Str(m, "value") == "default" && head.Length > 0) return $"Default ({head})";
        return head.Length > name.Length && head.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? head : name;
    }
}
