using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

// ACP session updates and permission requests in rhDock's terms (MonoCode's core/acp.ts). Kinds and statuses are ACP's in rhDock's spelling.
public sealed partial class AcpAgent
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Session, string Call), ToolCall> tools = new();

    private void Handle(string method, JsonElement p)
    {
        if (method != "session/update" || Str(p, "sessionId") is not { } sid) return;
        var session = sessions.GetOrAdd(sid, _ => new Session());
        var update = Obj(p, "update");
        if (Str(update, "sessionUpdate") != "user_message_chunk") Flush(sid, session);
        switch (Str(update, "sessionUpdate"))
        {
            // A replayed prompt arrives in chunks; it is one message until another kind of update.
            case "user_message_chunk" when session.Replaying:
                var content = Obj(update, "content");
                session.User ??= (new StringBuilder(), []);
                if (Str(content, "type") == "text") session.User.Value.Text.Append(Str(content, "text"));
                else if (Str(content, "type") == "image") session.User.Value.Images.Add(new(Str(content, "mimeType") ?? "image/png", Str(content, "data") ?? "", "image"));
                return;
            case "agent_message_chunk" when Str(Obj(update, "content"), "text") is { } text: Emit(session, new TextDelta(sid, text)); break;
            case "agent_message_chunk" when ToolImages.From(Obj(update, "content"), "image") is { } picture: Emit(session, new MediaMessage(sid, [picture])); break;
            case "agent_thought_chunk" when Str(Obj(update, "content"), "text") is { } thought: Emit(session, new ReasoningDelta(sid, thought)); break;
            case "tool_call" or "tool_call_update": Emit(session, Tool(sid, update)); break;
            case "plan":
                Emit(session, new PlanUpdate(sid, Arr(update, "entries").Select(e => (Str(e, "status") switch { "completed" => "Completed", "in_progress" => "InProgress", _ => "Pending" }, Str(e, "content") ?? "")).ToArray()));
                break;
            case "available_commands_update":
                session.Commands = Arr(update, "availableCommands").Select(c => new CommandInfo(Str(c, "name")!, Str(c, "description"))).ToArray();
                Event?.Invoke(new CatalogChanged());
                break;
            case "current_mode_update" when Str(update, "currentModeId") is { } mode:
                lock (gate) session.Modes = Replace(session.Modes, "currentModeId", mode);
                Event?.Invoke(new CatalogChanged());
                break;
            case "config_option_update":
                Remember(session, update);
                Event?.Invoke(new CatalogChanged());
                break;
            case "usage_update": Emit(session, new ContextUsage(sid, Num(update, "used"), Num(update, "size"), Num(Obj(update, "cost"), "amount"))); break;
            case "session_info_update" when Str(update, "title") is { } title: Emit(session, new Renamed(sid, title)); break;
        }
    }

    // The agent asks before a tool call; rhDock offers no file system or terminal, so those requests fail and the agent uses its own.
    private async Task<JsonNode?> Ask(string method, JsonElement p)
    {
        if (method != "session/request_permission") throw new NotSupportedException($"rhDock does not offer {method}.");
        var sid = Str(p, "sessionId") ?? "";
        var call = Obj(p, "toolCall");
        var id = Str(call, "toolCallId") ?? Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = answer;
        var tool = Tool(sid, call);
        Event?.Invoke(tool);
        options[id] = Arr(p, "options").Select(o => (Str(o, "kind") ?? "", Str(o, "optionId")!)).ToArray();
        Event?.Invoke(new PermissionAsked(sid, id, tool.Title ?? "a tool", tool.Kind, tool.Detail));
        try { return await answer.Task; }
        finally { options.TryRemove(id, out _); if (waiting.TryRemove(id, out _)) Event?.Invoke(new RequestClosed(sid, id)); }
    }

    private ToolCall Tool(string sid, JsonElement update)
    {
        var kind = Str(update, "kind") switch
        {
            "read" => "Read", "edit" => "Edit", "delete" => "Delete", "move" => "Move", "search" => "Search",
            "execute" => "Execute", "think" => "Think", "fetch" => "Fetch", null => null, _ => "Other",
        };
        var status = Str(update, "status") switch { "pending" => "Pending", "in_progress" => "InProgress", "completed" => "Completed", "failed" => "Failed", _ => null };
        var detail = new StringBuilder();
        var title = Str(update, "title");
        var images = ToolImages.In(Arr(update, "content").Select(b => Obj(b, "content")), title ?? "image");
        foreach (var block in Arr(update, "content"))
            if (Str(block, "type") == "diff")
            {
                foreach (var line in (Str(block, "oldText") ?? "").Split('\n').Where(_ => Str(block, "oldText") is { Length: > 0 })) detail.Append('-').Append(line).Append('\n');
                foreach (var line in (Str(block, "newText") ?? "").Split('\n')) detail.Append('+').Append(line).Append('\n');
            }
            else if (Str(Obj(block, "content"), "text") is { } text) detail.Append(text).Append('\n');
        var files = Arr(update, "locations").Select(l => Str(l, "path")).OfType<string>().ToArray();
        if (detail.Length == 0 && kind == "Execute" && First(Obj(update, "rawInput"), "command", "cmd") is { } command) detail.Append("$ ").Append(command);
        var id = Str(update, "toolCallId") ?? "";
        tools.TryGetValue((sid, id), out var previous);
        var next = new ToolCall(sid, id, Str(update, "title") ?? previous?.Title, kind ?? previous?.Kind, status ?? previous?.Status,
            detail.Length > 0 ? detail.ToString().TrimEnd('\n') : previous?.Detail, files.Length > 0 ? files : previous?.Files, Images: images ?? previous?.Images);
        tools[(sid, id)] = next;
        return next;
    }

    private void Emit(Session session, AgentEvent e) => Event?.Invoke(e);

    private void Flush(string sid, Session session)
    {
        if (session.User is not { } user) return;
        session.User = null;
        Event?.Invoke(new UserMessage(sid, $"acp:{Guid.NewGuid():N}", user.Text.ToString(), user.Images, []));
    }
}
