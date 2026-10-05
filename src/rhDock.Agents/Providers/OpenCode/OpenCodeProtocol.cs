using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

// OpenCode's events and stored messages in rhDock's terms (MonoCode's opencodeProtocol.ts).
public sealed partial class OpenCode
{
    // A subagent runs in a child conversation; its tool calls show as steps of the parent's subagent call, and its permission
    // requests and questions in the parent conversation. Answers still go to the child, so each request remembers its conversation.
    private readonly ConcurrentDictionary<string, (string Session, string Call)> children = new();
    private readonly ConcurrentDictionary<string, string> requestSessions = new();

    private void Handle(JsonElement e)
    {
        var type = Str(e, "type");
        if (type is "model.updated" or "agent.updated" or "command.updated" or "skill.updated" or "provider.updated") { Event?.Invoke(new CatalogChanged()); return; }
        if (!e.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        var sid = Str(data, "sessionID") ?? (data.TryGetProperty("form", out var f) ? Str(f, "sessionID") : null);
        if (sid is null) return;
        if (type == "session.tool.progress" && Str(Obj(data, "metadata"), "sessionID") is { } child && child != sid) children[child] = (sid, Str(data, "id")!);
        if (children.TryGetValue(sid, out var parent)) { Child(sid, parent, type, data); return; }
        var state = live.GetOrAdd(sid, _ => new Live());
        switch (type)
        {
            case "session.text.started" when state.Last == "text": Emit(new TextDelta(sid, "\n\n")); break;
            case "session.text.delta": Emit(new TextDelta(sid, Str(data, "delta") ?? "")); break;
            case "session.reasoning.started" when state.Last == "reasoning": Emit(new ReasoningDelta(sid, "\n\n")); break;
            case "session.reasoning.delta": Emit(new ReasoningDelta(sid, Str(data, "delta") ?? "")); break;
            case "session.tool.input.started" or "session.tool.called" or "session.tool.success" or "session.tool.failed":
                Emit(ToolUpdate(sid, type, data));
                break;
            case "session.step.ended" when data.TryGetProperty("tokens", out var stepTokens):
                var step = Tokens(stepTokens);
                state.Turn = state.Turn.Add(step);
                Emit(new ContextUsage(sid, step.Total, ContextSize(state.Model), state.Cost));
                break;
            case "session.usage.updated":
                state.Cost = Num(data, "cost");
                break;
            case "session.step.started" when data.TryGetProperty("model", out var stepModel) && stepModel.ValueKind == JsonValueKind.Object:
                state.Model = ModelOf(stepModel) with { Variant = state.Model?.Variant };
                state.Agent = Str(data, "agent") ?? state.Agent;
                break;
            case "session.renamed" when Str(data, "title") is { } title: Emit(new Renamed(sid, title)); break;
            case "session.retry.scheduled":
                var reason = data.TryGetProperty("error", out var why) ? Str(why, "message") : null;
                Emit(new Notice(sid, $"Retrying after a provider error{(reason is null ? "" : ": " + reason)} (attempt {Num(data, "attempt")})."));
                break;
            case "session.compaction.ended": Emit(new Notice(sid, "Compacted the conversation.")); break;
            case "session.compaction.failed": Emit(new Notice(sid, "Compaction failed" + (Str(Obj(data, "error"), "message") is { } cm ? ": " + cm : "."))); break;
            case "permission.asked" or "form.created" or "permission.replied" or "permission.rejected" or "form.replied" or "form.cancelled": Request(sid, sid, type, data); break;
            case "session.execution.succeeded": End(sid, state, "end_turn", null); break;
            case "session.execution.interrupted": End(sid, state, "cancelled", null); break;
            case "session.execution.failed":
                End(sid, state, "error", data.TryGetProperty("error", out var fe) ? Str(fe, "message") ?? fe.ToString() : "The turn failed.");
                break;
        }
    }

    // Events of a subagent's conversation: only its tool calls and its requests reach the parent.
    private void Child(string sid, (string Session, string Call) parent, string? type, JsonElement data)
    {
        if (type is "session.tool.input.started" or "session.tool.called" or "session.tool.success" or "session.tool.failed")
            Emit(ToolUpdate(sid, type, data) with { SessionId = parent.Session, Parent = parent.Call });
        else if (type is "permission.asked" or "form.created" or "permission.replied" or "permission.rejected" or "form.replied" or "form.cancelled")
            Request(parent.Session, sid, type, data);
    }

    private void Request(string shown, string owner, string? type, JsonElement data)
    {
        switch (type)
        {
            case "permission.asked":
                var asked = Permission(shown, data);
                requestSessions[asked.RequestId] = owner;
                Emit(asked);
                break;
            case "form.created" when data.TryGetProperty("form", out var form):
                var question = Question(shown, form);
                requestSessions[question.RequestId] = owner;
                Emit(question);
                break;
            case "permission.replied" or "permission.rejected": Emit(new RequestClosed(shown, Str(data, "requestID") ?? Str(data, "id") ?? "")); break;
            case "form.replied" or "form.cancelled": Emit(new RequestClosed(shown, Str(data, "id") ?? "")); break;
        }
    }

    // The conversation that owns a request: the child for a subagent's request, else the one it was shown in.
    private string Owner(string sessionId, string requestId) => requestSessions.TryRemove(requestId, out var owner) ? owner : sessionId;

    private ToolCall ToolUpdate(string sid, string? type, JsonElement data)
    {
        var callId = Str(data, "id")!;
        switch (type)
        {
            case "session.tool.input.started":
                tools[callId] = new Tool { Name = Str(data, "name") ?? "tool" };
                return ToolEvent(sid, callId, "Pending", null, null);
            case "session.tool.called":
                var called = tools.GetOrAdd(callId, _ => new Tool());
                if (data.TryGetProperty("input", out var input)) called.Input = input.Clone();
                Plan(sid, called);
                return ToolEvent(sid, callId, "InProgress", null, null);
            case "session.tool.success":
                return ToolEvent(sid, callId, "Completed", data.TryGetProperty("content", out var content) ? content : null, data.TryGetProperty("metadata", out var meta) ? meta : null);
            default:
                var failed = ToolEvent(sid, callId, "Failed", data.TryGetProperty("content", out var c) ? c : null, null);
                return failed with { Detail = failed.Detail ?? Str(Obj(data, "error"), "message") };
        }
    }

    // OpenCode's plan is its todo list.
    private void Plan(string sid, Tool tool)
    {
        if (tool.Name == "todowrite" && tool.Input is { } input && Arr(input, "todos").ToArray() is { Length: > 0 } todos)
            Emit(new PlanUpdate(sid, todos.Select(t => (Str(t, "status") switch { "completed" or "cancelled" => "Completed", "in_progress" => "InProgress", _ => "Pending" }, Str(t, "content") ?? "")).ToArray()));
    }

    private void End(string sid, Live state, string stop, string? error)
    {
        if (state.Turn.Total > 0) Emit(new TurnUsage(sid, state.Turn));
        if (turns.TryGetValue(sid, out var turn)) turn.TrySetResult(new TurnResult(stop, error));
    }

    private void Emit(AgentEvent e)
    {
        if (live.TryGetValue(e.SessionId, out var state) && e is not ToolCall { Parent: not null })
            state.Last = e switch { TextDelta => "text", ReasoningDelta => "reasoning", _ => null };
        Event?.Invoke(e);
    }

    // Replays one stored message; returns the token use of an assistant message for the context meter.
    private TokenUsage? Replay(string sid, JsonElement message)
    {
        switch (Str(message, "type"))
        {
            case "user":
                var images = new List<AgentImage>();
                var files = new List<string>();
                foreach (var file in Arr(message, "files"))
                    if (Str(file, "mime") is { } mime && mime.StartsWith("image/", StringComparison.Ordinal) && Str(file, "data") is { } data)
                        images.Add(new AgentImage(mime, data, Str(file, "name") ?? "image"));
                    else if (Str(file, "name") is { } name) files.Add(name);
                Emit(new UserMessage(sid, Str(message, "id")!, Str(message, "text") ?? "", images, files));
                return null;
            case "assistant":
                foreach (var part in Arr(message, "content"))
                    switch (Str(part, "type"))
                    {
                        case "text":
                            if (live.TryGetValue(sid, out var s) && s.Last == "text") Emit(new TextDelta(sid, "\n\n"));
                            Emit(new TextDelta(sid, Str(part, "text") ?? ""));
                            break;
                        case "reasoning": Emit(new ReasoningDelta(sid, Str(part, "text") ?? "")); break;
                        case "tool":
                            var id = Str(part, "id")!;
                            var toolState = part.GetProperty("state");
                            var tool = tools[id] = new Tool { Name = Str(part, "name") ?? "tool", Input = Obj(toolState, "input") is { ValueKind: JsonValueKind.Object } i ? i.Clone() : null };
                            Plan(sid, tool);
                            Emit(ToolEvent(sid, id, Str(toolState, "status") == "error" ? "Failed" : "Completed",
                                toolState.TryGetProperty("content", out var c) ? c : null, toolState.TryGetProperty("metadata", out var m) ? m : null));
                            break;
                    }
                return message.TryGetProperty("tokens", out var t) ? Tokens(t) : null;
            case "compaction" when Str(message, "status") == "completed":
                Emit(new Notice(sid, "Compacted the conversation."));
                return null;
            case "skill":
                Emit(new Notice(sid, $"Loaded the {Str(message, "name")} skill."));
                return null;
            case "shell":
                var output = Str(Obj(message, "output"), "output");
                Emit(new ToolCall(sid, Str(message, "id")!, Str(message, "command"), "Execute", Str(message, "status") == "exited" ? "Completed" : "Failed",
                    $"$ {Str(message, "command")}" + (output is null ? "" : "\n" + output), []));
                return null;
            default: return null;
        }
    }

    // A tool call in rhDock's terms: a short title, a kind for the icon, the files it touched, and the detail shown when opened.
    private ToolCall ToolEvent(string sid, string callId, string status, JsonElement? content, JsonElement? metadata)
    {
        var tool = tools.GetOrAdd(callId, _ => new Tool());
        var input = tool.Input ?? default;
        var (kind, title, file) = ToolPreview.Describe(tool.Name, input);
        var files = new List<string>();
        if (file is not null) files.Add(file);
        var patches = new List<string>();
        foreach (var entry in metadata is { } meta ? Arr(meta, "files") : [])
        {
            if (Str(entry, "file") is { } changed && !files.Contains(changed)) files.Add(changed);
            if (Str(entry, "patch") is { } patch) patches.Add(patch);
        }
        var text = content is { ValueKind: JsonValueKind.Array } parts ? parts.EnumerateArray().Select(p => Str(p, "text")).FirstOrDefault(t => t is not null) : null;
        var detail = patches.Count > 0 ? string.Join("\n", patches)
            : kind == "Execute" && Str(input, "command") is { } command ? $"$ {command}" + (text is null ? "" : "\n" + text)
            : text;
        return new ToolCall(sid, callId, title, kind, status, detail, files.Count > 0 ? files : null);
    }

    private static PermissionAsked Permission(string sid, JsonElement data)
    {
        var action = Str(data, "action") ?? "use a tool";
        var resources = Arr(data, "resources").Select(x => x.GetString() ?? "").ToArray();
        var patches = Arr(Obj(data, "metadata"), "files").Select(f => Str(f, "patch")).OfType<string>().ToArray();
        var kind = action switch { "edit" => "Edit", "shell" => "Execute", "read" => "Read", "webfetch" or "websearch" => "Fetch", _ => null };
        return new PermissionAsked(sid, Str(data, "id")!, resources.Length > 0 ? string.Join(", ", resources) : action, kind ?? action,
            patches.Length > 0 ? string.Join("\n", patches) : action == "shell" && resources.Length > 0 ? "$ " + string.Join("\n$ ", resources) : null);
    }

    private static QuestionAsked Question(string sid, JsonElement form)
    {
        var fields = Arr(form, "fields").Where(f => !Bool(f, "hidden")).Select(f =>
        {
            var options = Arr(f, "options").Select(x => new QuestionOption(Str(x, "value") ?? "", Str(x, "label") ?? Str(x, "value") ?? "", Str(x, "description"))).ToArray();
            var type = Str(f, "type") switch
            {
                "multiselect" => "multi",
                "boolean" => "boolean",
                "number" or "integer" => "number",
                _ => options.Length > 0 ? "choice" : "text",
            };
            return new QuestionField(Str(f, "key")!, Str(f, "title"), Str(f, "description"), type, options, Bool(f, "custom"), Bool(f, "required"));
        }).ToArray();
        return new QuestionAsked(sid, Str(form, "id")!, Str(form, "title"), fields);
    }
}
