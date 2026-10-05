using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

// Claude Code's stream-JSON lines and stored conversation files in rhDock's terms (MonoCode's claudeProtocol.ts).
public sealed partial class ClaudeCode
{
    // Tool calls by id, so a result can be shown with its call; and Claude's TaskCreate list, which is its plan.
    private readonly Dictionary<string, (string Name, JsonElement Input)> tools = [];
    private readonly List<(string Id, string Text, string Status)> tasks = [];

    private void Handle(Live current, JsonElement line)
    {
        var sid = current.SessionId;
        var parent = Str(line, "parent_tool_use_id");
        switch (Str(line, "type"))
        {
            case "control_response":
                var response = Obj(line, "response");
                if (Str(response, "request_id") is { } id && current.Controls.TryGetValue(id, out var call))
                    if (Str(response, "subtype") == "error") call.TrySetException(new InvalidOperationException(Str(response, "error") ?? "Claude Code refused the request."));
                    else call.TrySetResult(Obj(response, "response"));
                break;
            case "control_request": _ = Ask(current, line); break;
            case "control_cancel_request" when Str(line, "request_id") is { } cancelled && requests.TryRemove(cancelled, out _):
                Emit(new RequestClosed(sid, cancelled));
                break;
            case "system":
                switch (Str(line, "subtype"))
                {
                    case "init": current.Model = Str(line, "model"); break;
                    case "compact_boundary": Emit(new Notice(sid, "Compacted the conversation.")); break;
                    case "api_retry": Emit(new Notice(sid, $"Retrying after a provider error (attempt {Num(line, "attempt")}).")); break;
                }
                break;
            case "stream_event" when parent is null:
                var ev = Obj(line, "event");
                if (Str(ev, "type") == "content_block_start")
                {
                    var kind = Str(Obj(ev, "content_block"), "type");
                    var last = conversations.TryGetValue(sid, out var c) ? c.Last : null;
                    if (kind == "text" && last == "text") Emit(new TextDelta(sid, "\n\n"));
                    if (kind == "thinking" && last == "reasoning") Emit(new ReasoningDelta(sid, "\n\n"));
                }
                else if (Str(ev, "type") == "content_block_delta")
                {
                    var delta = Obj(ev, "delta");
                    if (Str(delta, "type") == "text_delta") Emit(new TextDelta(sid, Str(delta, "text") ?? ""));
                    else if (Str(delta, "type") == "thinking_delta") Emit(new ReasoningDelta(sid, Str(delta, "thinking") ?? ""));
                }
                break;
            case "assistant":
                Assistant(sid, line, streamed: true);
                if (parent is null && Obj(Obj(line, "message"), "usage") is { ValueKind: JsonValueKind.Object } usage)
                    Emit(new ContextUsage(sid, Context(usage), current.Window ?? 200_000, conversations.TryGetValue(sid, out var cost) ? cost.Cost : null));
                break;
            case "user":
                // The echo of a prompt or steer (--replay-user-messages) names the stored message.
                if (parent is null && Bool(line, "isReplay") && Str(line, "uuid") is { } uuid) Emit(new PromptAccepted(sid, uuid));
                else Results(sid, line);
                break;
            case "result": End(current, line); break;
            case "rate_limit_event" when Str(Obj(line, "rate_limit_info"), "status") == "rejected":
                var resets = Num(Obj(line, "rate_limit_info"), "resetsAt") is { } at ? $"; it resets at {DateTimeOffset.FromUnixTimeSeconds((long)at).ToLocalTime():t}" : "";
                Emit(new Notice(sid, $"Claude's usage limit is reached{resets}."));
                break;
        }
    }

    // Tool calls of an assistant message; text was already streamed unless this is a replay.
    private void Assistant(string sid, JsonElement line, bool streamed)
    {
        var parent = Str(line, "parent_tool_use_id");
        foreach (var block in Arr(Obj(line, "message"), "content"))
            switch (Str(block, "type"))
            {
                case "text" when !streamed && parent is null:
                    if (conversations.TryGetValue(sid, out var c) && c.Last == "text") Emit(new TextDelta(sid, "\n\n"));
                    Emit(new TextDelta(sid, Str(block, "text") ?? ""));
                    break;
                case "thinking" when !streamed && parent is null && Str(block, "thinking") is { Length: > 0 } thought: Emit(new ReasoningDelta(sid, thought)); break;
                case "tool_use" or "server_tool_use" or "mcp_tool_use" when Str(block, "id") is { } id:
                    var name = Str(block, "name") ?? "tool";
                    var input = Obj(block, "input").Clone();
                    tools[id] = (name, input);
                    // A proposed plan is its own item, not a tool row.
                    if (name == "ExitPlanMode") { if (Str(input, "plan") is { Length: > 0 } plan) Emit(new PlanProposed(sid, plan)); break; }
                    var (kind, title, file) = ToolPreview.Describe(name, input);
                    Emit(new ToolCall(sid, id, title, kind, streamed ? "InProgress" : "Completed", Preview(name, input), file is null ? null : [file], parent));
                    if (name == "TodoWrite" && Arr(input, "todos").ToArray() is { Length: > 0 } todos)
                        Emit(new PlanUpdate(sid, todos.Select(t => (Status(Str(t, "status")), Str(t, "content") ?? Str(t, "activeForm") ?? "")).ToArray()));
                    break;
            }
    }

    // Tool results arrive as a user message.
    private void Results(string sid, JsonElement line)
    {
        var parent = Str(line, "parent_tool_use_id");
        var structured = line.TryGetProperty("tool_use_result", out var r) ? r : default;
        foreach (var block in Arr(Obj(line, "message"), "content"))
        {
            if (Str(block, "type") != "tool_result" || Str(block, "tool_use_id") is not { } id || !tools.TryGetValue(id, out var tool) || tool.Name == "ExitPlanMode") continue;
            var text = Str(block, "content") ?? string.Join("\n", Arr(block, "content").Select(p => Str(p, "text")).OfType<string>());
            var failed = Bool(block, "is_error");
            var (kind, title, _) = ToolPreview.Describe(tool.Name, tool.Input);
            Emit(new ToolCall(sid, id, title, kind, failed ? "Failed" : "Completed", Detail(tool.Name, tool.Input, structured, text), null, parent));
            if (!failed && parent is null && TrackTask(tool.Name, tool.Input, text)) Emit(new PlanUpdate(sid, tasks.Select(t => (t.Status, t.Text)).ToArray()));
        }
    }

    private void End(Live current, JsonElement result)
    {
        var sid = current.SessionId;
        if (Obj(result, "modelUsage") is { ValueKind: JsonValueKind.Object } models)
            foreach (var model in models.EnumerateObject())
                if (Num(model.Value, "contextWindow") is { } window) current.Window = window;
        if (conversations.TryGetValue(sid, out var conversation)) conversation.Cost += Num(result, "total_cost_usd") ?? 0;
        var usage = Obj(result, "usage");
        if (usage.ValueKind == JsonValueKind.Object)
            Emit(new TurnUsage(sid, new TokenUsage(Num(usage, "input_tokens") ?? 0, Num(usage, "output_tokens") ?? 0, 0, Num(usage, "cache_read_input_tokens") ?? 0, Num(usage, "cache_creation_input_tokens") ?? 0)));
        var stop = current.Cancelled ? "cancelled" : Str(result, "subtype") == "success" && !Bool(result, "is_error") ? "end_turn" : "error";
        var error = stop == "error" ? string.Join(" ", Arr(result, "errors").Select(e => e.GetString())) is { Length: > 0 } errors ? errors : Str(result, "result") ?? "The turn failed." : null;
        current.Turn?.TrySetResult(new TurnResult(stop, error));
        current.Turn = null;
    }

    // A permission request or AskUserQuestion. Reads stay automatic, as in Claude Code.
    private async Task Ask(Live current, JsonElement line)
    {
        var request = Obj(line, "request");
        var id = Str(line, "request_id")!;
        if (Str(request, "subtype") is not ("can_use_tool" or "permission"))
        {
            await Respond(current, id, new());
            return;
        }
        var name = Str(request, "tool_name") ?? "tool";
        var input = Obj(request, "input").Clone();
        requests[id] = new Request(name, input, request.TryGetProperty("permission_suggestions", out var s) ? s.Clone() : default);
        var sid = current.SessionId;
        if (name == "AskUserQuestion")
        {
            Emit(new QuestionAsked(sid, id, null, Arr(input, "questions").Select(q => new QuestionField(Str(q, "question") ?? "", Str(q, "header"), Str(q, "question"),
                Bool(q, "multiSelect") ? "multi" : "choice", Arr(q, "options").Select(o => new QuestionOption(Str(o, "label") ?? "", Str(o, "label") ?? "", Str(o, "description"))).ToArray(), true, true)).ToArray()));
            return;
        }
        // MonoCode's answer: the plan is shown for review, and the CLI waits in plan mode for the user's next prompt.
        if (name == "ExitPlanMode")
        {
            requests.TryRemove(id, out _);
            await Respond(current, id, new System.Text.Json.Nodes.JsonObject { ["behavior"] = "deny", ["message"] = "The client captured your proposed plan. Stop here and wait for the user's feedback or implementation request in a later turn." });
            return;
        }
        // The card says "wants to <kind> <title>", so the title names the file or command, as the other agents' requests do.
        var (kind, title, file) = ToolPreview.Describe(name, input);
        Emit(new PermissionAsked(sid, id, file ?? Str(input, "command") ?? title, kind, Preview(name, input)));
    }

    private void Emit(AgentEvent e)
    {
        if (conversations.TryGetValue(e.SessionId, out var conversation) && e is not ToolCall { Parent: not null })
            conversation.Last = e switch { TextDelta => "text", ReasoningDelta => "reasoning", _ => null };
        Event?.Invoke(e);
    }

    // What a call is about to do: an edit's change, a command line.
    private static string? Preview(string name, JsonElement input) => name switch
    {
        "Edit" => Lines(Str(input, "old_string"), Str(input, "new_string")),
        "MultiEdit" => string.Join("\n", Arr(input, "edits").Select(e => Lines(Str(e, "old_string"), Str(e, "new_string")))),
        "Write" => Lines(null, Str(input, "content")),
        "Bash" => Str(input, "command") is { } command ? "$ " + command : null,
        _ => null,
    };

    // What a call did: the patch Claude Code applied, or a command and its output.
    private static string? Detail(string name, JsonElement input, JsonElement structured, string text)
    {
        if (structured.ValueKind == JsonValueKind.Object && Arr(structured, "structuredPatch").ToArray() is { Length: > 0 } hunks)
            return string.Join("\n", hunks.Select(h => $"@@ -{Num(h, "oldStart")},{Num(h, "oldLines")} +{Num(h, "newStart")},{Num(h, "newLines")} @@\n" + string.Join("\n", Arr(h, "lines").Select(l => l.GetString()))));
        if (name == "Write") return Lines(null, Str(input, "content"));
        if (name == "Bash") return $"$ {Str(input, "command")}\n" + (structured.ValueKind == JsonValueKind.Object ? (Str(structured, "stdout") + "\n" + Str(structured, "stderr")).Trim() : text);
        return text.Length > 0 ? text : null;
    }

    private static string? Lines(string? before, string? after)
    {
        if (before is null && after is null) return null;
        var text = new StringBuilder();
        foreach (var l in (before ?? "").Split('\n').Where(_ => before is { Length: > 0 })) text.Append('-').Append(l).Append('\n');
        foreach (var l in (after ?? "").Split('\n').Where(_ => after is { Length: > 0 })) text.Append('+').Append(l).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    private static string Status(string? status) => status switch { "completed" => "Completed", "in_progress" => "InProgress", _ => "Pending" };

    // TaskCreate and TaskUpdate keep Claude's task list; the created id is only in the result text (as in MonoCode).
    private bool TrackTask(string name, JsonElement input, string result)
    {
        if (name == "TaskCreate" && First(input, "subject", "activeForm", "description") is { } text && Regex.Match(result, @"Task #([^\s:]+)") is { Success: true } m)
        {
            tasks.Add((m.Groups[1].Value, text.Trim(), "Pending"));
            return true;
        }
        if (name != "TaskUpdate") return false;
        var id = (input.TryGetProperty("taskId", out var raw) ? raw.ValueKind == JsonValueKind.Number ? raw.GetRawText() : raw.GetString() ?? "" : "").Trim().TrimStart('#');
        var at = tasks.FindIndex(t => t.Id == id);
        if (at < 0) return false;
        if (Str(input, "status") == "deleted") { tasks.RemoveAt(at); return true; }
        tasks[at] = (id, Str(input, "subject")?.Trim() ?? tasks[at].Text, Str(input, "status") is { } status ? Status(status) : tasks[at].Status);
        return true;
    }

    // The context the next request starts with: everything the model read, cached or not.
    private static double Context(JsonElement usage) =>
        (Num(usage, "input_tokens") ?? 0) + (Num(usage, "cache_read_input_tokens") ?? 0) + (Num(usage, "cache_creation_input_tokens") ?? 0) + (Num(usage, "output_tokens") ?? 0);

    // Stored conversations: one JSON line per entry, a tree by parentUuid. The newest entry is the current branch's tip.

    private static List<JsonElement> Read(string file)
    {
        var entries = new List<JsonElement>();
        if (!File.Exists(file)) return entries;
        foreach (var text in File.ReadLines(file))
            try { using var json = JsonDocument.Parse(text); entries.Add(json.RootElement.Clone()); }
            catch (JsonException) { }
        return entries;
    }

    // The current branch, oldest first. A compaction starts a new root that keeps its logical parent.
    private static List<JsonElement> Branch(List<JsonElement> entries)
    {
        var byId = new Dictionary<string, JsonElement>();
        foreach (var e in entries) if (Str(e, "uuid") is { } id) byId[id] = e;
        var tip = entries.LastOrDefault(e => Str(e, "type") is "user" or "assistant" && !Bool(e, "isSidechain"));
        var branch = new List<JsonElement>();
        var seen = new HashSet<string>();
        for (var at = tip; at.ValueKind == JsonValueKind.Object && Str(at, "uuid") is { } id && seen.Add(id);)
        {
            branch.Add(at);
            var up = Str(at, "parentUuid") ?? Str(at, "logicalParentUuid");
            at = up is not null && byId.TryGetValue(up, out var p) ? p : default;
        }
        branch.Reverse();
        return branch;
    }

    private static string StoredFile(string repository, string sessionId) => Path.Combine(ProjectFolder(repository), sessionId + ".jsonl");

    // A typed prompt, not a tool result, command echo, or Claude Code's own reminder.
    private static string? PromptText(JsonElement entry)
    {
        if (Str(entry, "type") != "user" || Bool(entry, "isMeta") || Bool(entry, "isSidechain") || Bool(entry, "isCompactSummary")) return null;
        var content = Obj(entry, "message").TryGetProperty("content", out var c) ? c : default;
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()
            : content.ValueKind == JsonValueKind.Array && content.EnumerateArray().All(b => Str(b, "type") is "text" or "image") ? string.Join("\n", content.EnumerateArray().Select(b => Str(b, "text")).OfType<string>()) : null;
        return text is null || text.StartsWith('<') ? null : text;
    }

    private void Replay(string repository, string sessionId, Conversation conversation)
    {
        tools.Clear();
        tasks.Clear();
        JsonElement usage = default;
        foreach (var entry in Branch(Read(StoredFile(repository, sessionId))))
        {
            if (Str(entry, "type") == "system" && Str(entry, "subtype") == "compact_boundary") { Emit(new Notice(sessionId, "Compacted the conversation.")); continue; }
            if (PromptText(entry) is { } text && Str(entry, "uuid") is { } uuid)
            {
                if (uuid == conversation.RewoundTo) { Emit(new Rewound(sessionId, uuid, text)); break; }
                var images = Arr(Obj(entry, "message"), "content").Where(b => Str(b, "type") == "image")
                    .Select(b => new AgentImage(Str(Obj(b, "source"), "media_type") ?? "image/png", Str(Obj(b, "source"), "data") ?? "", "image")).ToArray();
                Emit(new UserMessage(sessionId, uuid, text, images, []));
            }
            else if (Str(entry, "type") == "assistant" && !Bool(entry, "isSidechain"))
            {
                Assistant(sessionId, entry, streamed: false);
                if (Obj(Obj(entry, "message"), "usage") is { ValueKind: JsonValueKind.Object } u) usage = u;
            }
            else if (Str(entry, "type") == "user") Results(sessionId, entry);
        }
        if (usage.ValueKind == JsonValueKind.Object) Emit(new ContextUsage(sessionId, Context(usage), 200_000, null));
    }

    // The message a rewind resumes from: the last user or assistant message before the prompt (--resume-session-at takes no other entry).
    private string? Before(string repository, string sessionId, string messageId)
    {
        var branch = Branch(Read(StoredFile(repository, sessionId)));
        var at = branch.FindIndex(e => Str(e, "uuid") == messageId);
        // A resumed CLI loads only the messages after the latest compaction, and exits when asked to resume before it.
        if (at >= 0 && branch.Skip(at).Any(e => Str(e, "subtype") == "compact_boundary"))
            throw new InvalidOperationException("Claude Code cannot rewind to before a compaction; rewind to a later message or start a new conversation.");
        return at > 0 ? branch.Take(at).LastOrDefault(e => Str(e, "type") is "user" or "assistant") is { ValueKind: JsonValueKind.Object } before ? Str(before, "uuid") : null : null;
    }

    // A stored conversation's name, the latest custom-title record (Claude Code's /rename), and its first prompt.
    // ponytail: reads the whole file for a rename; keep an index if long conversation lists load slowly.
    private static (string? Named, string? First) Title(string file)
    {
        string? first = null, named = null;
        try
        {
            foreach (var text in File.ReadLines(file))
            {
                if (first is not null && !text.Contains("\"custom-title\"", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(text);
                if (Str(json.RootElement, "type") == "custom-title" && Str(json.RootElement, "customTitle") is { Length: > 0 } custom) named = custom;
                else if (first is null && PromptText(json.RootElement) is { } prompt) first = AgentPrompt.Title(prompt);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return (named, first);
    }
}
