using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

// Pi's RPC records and stored messages in rhDock's terms (MonoCode's piProtocol.ts).
public sealed partial class Pi
{
    private readonly Dictionary<string, (string Name, JsonElement Args)> tools = [];
    private string? last, stop, failure;

    private void Handle(JsonElement record)
    {
        var sid = current ?? "";
        switch (Str(record, "type"))
        {
            case "response" when Str(record, "id") is { } id && calls.TryGetValue(id, out var call):
                if (Bool(record, "success")) call.TrySetResult(record.TryGetProperty("data", out var data) ? data : default);
                else call.TrySetException(new InvalidOperationException(Str(record, "error") ?? $"{name} refused {Str(record, "command")}."));
                break;
            case "message_update":
                var update = Obj(record, "assistantMessageEvent");
                switch (Str(update, "type"))
                {
                    case "text_start" when last == "text": Emit(new TextDelta(sid, "\n\n")); break;
                    case "thinking_start" when last == "reasoning": Emit(new ReasoningDelta(sid, "\n\n")); break;
                    case "text_delta": Emit(new TextDelta(sid, Str(update, "delta") ?? "")); break;
                    case "thinking_delta": Emit(new ReasoningDelta(sid, Str(update, "delta") ?? "")); break;
                }
                break;
            case "message_end" when Str(Obj(record, "message"), "role") == "assistant":
                var message = Obj(record, "message");
                (stop, failure) = (Str(message, "stopReason"), Str(message, "errorMessage"));
                var usage = Obj(message, "usage");
                spent = spent.Add(new TokenUsage(Num(usage, "input") ?? 0, Num(usage, "output") ?? 0, Num(usage, "reasoning") ?? 0, Num(usage, "cacheRead") ?? 0, Num(usage, "cacheWrite") ?? 0));
                break;
            case "tool_execution_start" when Str(record, "toolCallId") is { } started:
                var toolName = Str(record, "toolName") ?? "tool";
                var args = Obj(record, "args").Clone();
                tools[started] = (toolName, args);
                Emit(Tool(sid, started, "InProgress", default));
                break;
            case "tool_execution_end" when Str(record, "toolCallId") is { } ended:
                Emit(Tool(sid, ended, Bool(record, "isError") ? "Failed" : "Completed", Obj(record, "result")));
                break;
            case "auto_retry_start":
                Emit(new Notice(sid, $"Retrying after a provider error (attempt {Num(record, "attempt")} of {Num(record, "maxAttempts")}): {Str(record, "errorMessage")}"));
                break;
            case "compaction_end":
                Emit(new Notice(sid, Str(record, "errorMessage") ?? (Bool(record, "aborted") ? "Compaction cancelled." : "Compacted the conversation.")));
                break;
            case "extension_error": Emit(new Notice(sid, $"A {name} extension failed: {Str(record, "error")}")); break;
            case "extension_ui_request": Dialog(sid, record); break;
            case "agent_settled": _ = Settle(sid); break;
        }
    }

    // Pi will not continue on its own once settled; an error or abort shows in the last assistant message.
    private async Task Settle(string sid)
    {
        try { await Stats(sid, turnEnded: true); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { log("stats " + ex.Message); }
        var result = stop switch { "aborted" => new TurnResult("cancelled", null), "error" when failure == "This operation was aborted" => new TurnResult("cancelled", null), "error" => new TurnResult("error", failure ?? "The turn failed."), _ => new TurnResult("end_turn", null) };
        (stop, failure) = (null, null);
        turn?.TrySetResult(result);
    }

    private async Task Stats(string sid, bool turnEnded)
    {
        var stats = await Call(new JsonObject { ["type"] = "get_session_stats" });
        var context = Obj(stats, "contextUsage");
        Emit(new ContextUsage(sid, Num(context, "tokens"), Num(context, "contextWindow"), Num(stats, "cost")));
        if (turnEnded && spent.Total > 0) Emit(new TurnUsage(sid, spent));
    }

    // Extension dialogs become questions; notifications become notices. Pi resolves a timed-out dialog itself.
    private void Dialog(string sid, JsonElement request)
    {
        var id = Str(request, "id")!;
        var method = Str(request, "method");
        var title = Str(request, "title");
        QuestionField? field = method switch
        {
            "select" => new("value", title, Str(request, "message"), "choice", Arr(request, "options").Select(o => new QuestionOption(o.GetString() ?? "", o.GetString() ?? "", null)).ToArray(), false, true),
            "confirm" => new("value", title, Str(request, "message"), "boolean", [], false, true),
            "input" or "editor" => new("value", title, Str(request, "placeholder") ?? Str(request, "message"), "text", [], false, true),
            _ => null,
        };
        if (method == "notify") Emit(new Notice(sid, Str(request, "message") ?? title ?? ""));
        if (field is null) return;
        dialogs[id] = method!;
        Emit(new QuestionAsked(sid, id, title, [field]));
    }

    private ToolCall Tool(string sid, string id, string status, JsonElement result)
    {
        var (toolName, args) = tools.TryGetValue(id, out var t) ? t : ("tool", default(JsonElement));
        var (kind, title, file) = ToolPreview.Describe(toolName, args);
        var text = string.Join("\n", Arr(result, "content").Select(c => Str(c, "text")).OfType<string>());
        var details = Obj(result, "details");
        var detail = Str(details, "patch")
            ?? (kind == "Execute" ? $"$ {Str(args, "command")}" + (text.Length > 0 ? "\n" + text : "") : null)
            ?? (toolName == "edit" ? string.Join("\n", Arr(args, "edits").Select(e => $"-{Str(e, "oldText")?.Replace("\n", "\n-")}\n+{Str(e, "newText")?.Replace("\n", "\n+")}")) : null)
            ?? (text.Length > 0 ? text : null);
        return new ToolCall(sid, id, title, kind, status, detail, file is null ? null : [file], Images: ToolImages.In(Arr(result, "content"), title));
    }

    private void Emit(AgentEvent e)
    {
        if (e is TextDelta or ReasoningDelta or ToolCall) last = e switch { TextDelta => "text", ReasoningDelta => "reasoning", _ => null };
        Event?.Invoke(e);
    }

    private void Replay(string sid, IEnumerable<JsonElement> messages)
    {
        tools.Clear();
        last = null;
        var index = 0;
        foreach (var message in messages)
        {
            index++;
            switch (Str(message, "role"))
            {
                case "user":
                    var content = message.TryGetProperty("content", out var c) ? c : default;
                    var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Join("\n", Arr(message, "content").Select(b => Str(b, "text")).OfType<string>());
                    var images = Arr(message, "content").Where(b => Str(b, "type") == "image").Select(b => new AgentImage(Str(b, "mimeType") ?? "image/png", Str(b, "data") ?? "", "image")).ToArray();
                    Emit(new UserMessage(sid, $"pi:{index}", text, images, []));
                    break;
                case "assistant":
                    foreach (var block in Arr(message, "content"))
                        switch (Str(block, "type"))
                        {
                            case "text": Emit(new TextDelta(sid, (last == "text" ? "\n\n" : "") + Str(block, "text"))); break;
                            case "image" when ToolImages.From(block, "image") is { } picture: Emit(new MediaMessage(sid, [picture])); break;
                            case "thinking" when Str(block, "thinking") is { Length: > 0 } thought: Emit(new ReasoningDelta(sid, thought)); break;
                            case "toolCall" when Str(block, "id") is { } id:
                                tools[id] = (Str(block, "name") ?? "tool", Obj(block, "arguments").Clone());
                                Emit(Tool(sid, id, "Completed", default));
                                break;
                        }
                    break;
                case "toolResult" when Str(message, "toolCallId") is { } result:
                    Emit(Tool(sid, result, Bool(message, "isError") ? "Failed" : "Completed", message));
                    break;
                case "bashExecution":
                    Emit(new ToolCall(sid, $"bash:{index}", Str(message, "command"), "Execute", "Completed", $"$ {Str(message, "command")}\n{Str(message, "output")}", null));
                    break;
                case "compactionSummary": Emit(new Notice(sid, "Compacted the conversation.")); break;
            }
        }
    }

    // A session's name (its latest session_info) and its first prompt.
    private static (string? Named, string? First) Title(string file)
    {
        string? first = null, name = null;
        try
        {
            foreach (var text in File.ReadLines(file))
            {
                using var json = JsonDocument.Parse(text);
                var entry = json.RootElement;
                if (Str(entry, "type") == "session_info" && Str(entry, "name") is { Length: > 0 } named) name = named;
                if (first is null && Str(entry, "type") == "message" && Obj(entry, "message") is var m && Str(m, "role") == "user")
                    first = m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : string.Join(" ", Arr(m, "content").Select(b => Str(b, "text")).OfType<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return (name, AgentPrompt.Title(first));
    }
}
