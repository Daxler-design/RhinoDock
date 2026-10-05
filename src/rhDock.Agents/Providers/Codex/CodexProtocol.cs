using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

// Codex app-server notifications, requests, and stored turns in rhDock's terms (MonoCode's codexProtocol.ts).
public sealed partial class Codex
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Session, string Call)> children = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> childRoots = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> childSubscriptions = new();
    private readonly Dictionary<string, List<AgentEvent>> earlyChildEvents = [];

    private void Handle(string method, JsonElement p)
    {
        if (method == "thread/started")
        {
            var started = Obj(p, "thread");
            if (Str(started, "id") is { } child && Str(Obj(Obj(Obj(started, "source"), "subagent"), "thread_spawn"), "parent_thread_id") is { } parent)
            {
                childRoots[child] = parent;
                SubscribeChild(child);
            }
            return;
        }
        if (Str(p, "threadId") is not { } tid) return;
        var thread = threads.GetOrAdd(tid, _ => new Conversation());
        switch (method)
        {
            case "turn/started": thread.Turn = Str(Obj(p, "turn"), "id"); break;
            case "turn/completed": End(tid, thread, Obj(p, "turn")); break;
            case "item/agentMessage/delta": Emit(thread, new TextDelta(tid, Str(p, "delta") ?? "")); break;
            case "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta": Emit(thread, new ReasoningDelta(tid, Str(p, "delta") ?? "")); break;
            case "item/started": Item(tid, thread, Obj(p, "item"), Str(p, "turnId"), live: true, done: false); break;
            case "item/completed": Item(tid, thread, Obj(p, "item"), Str(p, "turnId"), live: true, done: true); break;
            case "turn/plan/updated":
                Emit(thread, new PlanUpdate(tid, Arr(p, "plan").Select(s => (Str(s, "status") switch { "completed" => "Completed", "inProgress" => "InProgress", _ => "Pending" }, Str(s, "step") ?? "")).ToArray()));
                break;
            case "thread/tokenUsage/updated":
                var usage = Obj(p, "tokenUsage");
                thread.Total = Tokens(Obj(usage, "total"));
                thread.Window = Num(usage, "modelContextWindow") ?? thread.Window;
                var last = Obj(usage, "last");
                Emit(thread, new ContextUsage(tid, (Num(last, "inputTokens") ?? 0) + (Num(last, "outputTokens") ?? 0), thread.Window, null));
                break;
            case "thread/name/updated" when Str(p, "threadName") is { } name: Emit(thread, new Renamed(tid, name)); break;
            case "error" when Bool(p, "willRetry"): Emit(thread, new Notice(tid, $"Retrying after a provider error: {Str(Obj(p, "error"), "message")}")); break;
            // A compaction outside a turn ends here.
            case "thread/compacted":
                Emit(thread, new Notice(tid, "Compacted the conversation."));
                if (thread.Turn is null) thread.Done?.TrySetResult(new TurnResult("end_turn", null));
                break;
        }
    }

    // Requests from the server wait for the panel. A request's id is the item it is about.
    private async Task<JsonNode?> Ask(string method, JsonElement p)
    {
        if (method == "currentTime/read") return new JsonObject { ["currentTimeAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var tid = Str(p, "threadId") ?? "";
        var thread = threads.GetOrAdd(tid, _ => new Conversation());
        var id = Str(p, "approvalId") ?? Str(p, "itemId") ?? Guid.NewGuid().ToString();
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = answer;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                var actions = Arr(p, "commandActions").ToArray();
                var readOnly = actions.Length > 0 && actions.All(a => Str(a, "type") is "read" or "listFiles" or "search");
                if (p.TryGetProperty("availableDecisions", out _) && !Arr(p, "availableDecisions").Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "acceptForSession")) onceOnly[id] = true;
                var command = Command(p, actions);
                Emit(thread, new PermissionAsked(tid, id, command, readOnly ? "Read" : "Execute", (Str(p, "reason") is { } reason ? reason + "\n" : "") + "$ " + command));
                break;
            case "item/fileChange/requestApproval":
                JsonElement change;
                lock (gate) thread.Items.TryGetValue(Str(p, "itemId") ?? "", out change);
                var (title, files, diff) = Change(change);
                Emit(thread, new PermissionAsked(tid, id, files.Length == 1 ? files[0] : title, "Edit", diff));
                break;
            case "item/tool/requestUserInput":
                Emit(thread, new QuestionAsked(tid, id, null, Arr(p, "questions").Select(q => new QuestionField(Str(q, "id")!, Str(q, "header"), Str(q, "question"),
                    Arr(q, "options").Any() ? "choice" : "text", Arr(q, "options").Select(o => new QuestionOption(Str(o, "label") ?? "", Str(o, "label") ?? "", Str(o, "description"))).ToArray(),
                    Bool(q, "isOther"), true)).ToArray()));
                break;
            default:
                waiting.TryRemove(id, out _);
                throw new NotSupportedException($"rhDock does not answer {method}.");
        }
        try { return await answer.Task; }
        finally { if (waiting.TryRemove(id, out _)) Emit(thread, new RequestClosed(tid, id)); }
    }

    // Requests that cannot be allowed for the rest of the conversation; Always allow answers them once.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> onceOnly = new();

    private void End(string tid, Conversation thread, JsonElement turn)
    {
        var spent = new TokenUsage(thread.Total.Input - thread.AtStart.Input, thread.Total.Output - thread.AtStart.Output, thread.Total.Reasoning - thread.AtStart.Reasoning,
            thread.Total.CacheRead - thread.AtStart.CacheRead, 0);
        if (spent.Total > 0) Emit(thread, new TurnUsage(tid, spent));
        thread.Turn = null;
        var result = Str(turn, "status") switch
        {
            "interrupted" => new TurnResult("cancelled", null),
            "failed" => new TurnResult("error", Str(Obj(turn, "error"), "message") ?? "The turn failed."),
            _ => new TurnResult("end_turn", null),
        };
        if (children.TryGetValue(tid, out var parent))
            Emit(threads.GetOrAdd(parent.Session, _ => new Conversation()), new ToolCall(parent.Session, parent.Call, null, "Think", result.Stop == "end_turn" ? "Completed" : "Failed", null, null));
        thread.Done?.TrySetResult(result);
    }

    // One thread item, as it starts, as it completes, or from a stored turn.
    private void Item(string tid, Conversation thread, JsonElement item, string? turn, bool live, bool done)
    {
        var id = Str(item, "id");
        if (id is null) return;
        var status = !done ? "InProgress" : Str(item, "status") switch { "failed" or "declined" => "Failed", _ => "Completed" };
        switch (Str(item, "type"))
        {
            case "userMessage":
                if (turn is not null) lock (gate) thread.TurnOf[id] = turn;
                var content = Arr(item, "content").ToArray();
                var text = string.Join("\n", content.Where(c => Str(c, "type") == "text").Select(c => Str(c, "text")));
                if (live && !done) Emit(thread, new PromptAccepted(tid, id));
                else if (!live)
                {
                    if (id == thread.RevertMessage) { Emit(thread, new Rewound(tid, id, text)); throw new OperationCanceledException(); }
                    var images = content.Where(c => Str(c, "type") == "image" && Str(c, "url") is { } u && u.StartsWith("data:", StringComparison.Ordinal))
                        .Select(c => { var u = Str(c, "url")!; var comma = u.IndexOf(','); return new AgentImage(u[5..u.IndexOf(';')], u[(comma + 1)..], "image"); }).ToArray();
                    Emit(thread, new UserMessage(tid, id, text, images, content.Where(c => Str(c, "type") == "mention").Select(c => Str(c, "path") ?? "").ToArray()));
                }
                break;
            case "agentMessage":
                if (!done && thread.Last == "text") Emit(thread, new TextDelta(tid, "\n\n"));
                if (!live) Emit(thread, new TextDelta(tid, (thread.Last == "text" ? "\n\n" : "") + Str(item, "text")));
                break;
            case "plan" when done && Str(item, "text") is { Length: > 0 } proposed:
                Emit(thread, new PlanProposed(tid, proposed));
                break;
            case "reasoning" when !live:
                var thought = string.Join("\n\n", Arr(item, "summary").Concat(Arr(item, "content")).Select(t => t.GetString()));
                if (thought.Length > 0) Emit(thread, new ReasoningDelta(tid, thought));
                break;
            case "commandExecution":
                var actions = Arr(item, "commandActions").ToArray();
                var command = Command(item, actions);
                var kind = actions.Length > 0 && actions.All(a => Str(a, "type") == "read") ? "Read" : actions.Length > 0 && actions.All(a => Str(a, "type") is "listFiles" or "search") ? "Search" : "Execute";
                var title = kind == "Read" ? "Read " + string.Join(", ", actions.Select(a => Str(a, "name"))) : command;
                Emit(thread, new ToolCall(tid, id, title, kind, status, "$ " + command + (Str(item, "aggregatedOutput") is { Length: > 0 } output ? "\n" + output.TrimEnd() : ""),
                    actions.Select(a => Str(a, "path")).OfType<string>().ToArray()));
                break;
            case "fileChange":
                lock (gate) thread.Items[id] = item.Clone();
                var (summary, files, diff) = Change(item);
                Emit(thread, new ToolCall(tid, id, summary, "Edit", status, diff, files));
                break;
            case "mcpToolCall":
                Emit(thread, new ToolCall(tid, id, ToolPreview.Mcp(Str(item, "server") ?? "mcp", Str(item, "tool") ?? "tool"), "Other", status, Str(Obj(item, "error"), "message"), null));
                break;
            case "webSearch":
                Emit(thread, new ToolCall(tid, id, $"Search the web for {Str(item, "query")}", "Fetch", status, null, null));
                break;
            case "collabAgentToolCall":
                var receivers = Arr(item, "receiverThreadIds").Select(r => r.GetString()).OfType<string>().Where(r => r != tid).ToArray();
                foreach (var child in receivers)
                {
                    childRoots[child] = tid;
                    // Wait/send/close calls must not move a child out from under its original spawn task.
                    children.TryAdd(child, (tid, id));
                }
                Emit(thread, new ToolCall(tid, id, Str(item, "prompt") is { } prompt ? prompt.Split('\n')[0] : Str(item, "tool") switch { "wait" => "Wait for subagents", "sendInput" => "Send to subagent", "closeAgent" => "Close subagent", _ => "Subagent" }, "Think", status, Str(item, "prompt"), null));
                foreach (var child in receivers)
                {
                    AgentEvent[] buffered;
                    lock (gate) buffered = earlyChildEvents.Remove(child, out var early) ? [.. early] : [];
                    var childThread = threads.GetOrAdd(child, _ => new Conversation());
                    foreach (var e in buffered) Emit(childThread, e);
                    SubscribeChild(child);
                }
                break;
            case "subAgentActivity":
                // Current servers identify children here instead of in spawn-call receivers.
                var activityId = id;
                var childId = Str(item, "agentThreadId");
                if (childId is not null && childId != tid)
                {
                    childRoots[childId] = tid;
                    children.TryAdd(childId, (tid, id));
                    activityId = children[childId].Call;
                }
                Emit(thread, new ToolCall(tid, activityId, "Subagent " + Str(item, "agentPath"), "Think", Str(item, "kind") switch { "completed" => "Completed", "interrupted" => "Failed", _ => "InProgress" }, Str(item, "agentPath"), null));
                if (childId is not null && childId != tid)
                {
                    AgentEvent[] buffered;
                    lock (gate) buffered = earlyChildEvents.Remove(childId, out var early) ? [.. early] : [];
                    var childThread = threads.GetOrAdd(childId, _ => new Conversation());
                    foreach (var e in buffered) Emit(childThread, e);
                    SubscribeChild(childId);
                }
                break;
            case "contextCompaction" when done && !live: Emit(thread, new Notice(tid, "Compacted the conversation.")); break;
        }
    }

    private void Replay(string sid, Conversation thread, List<JsonElement> turns)
    {
        thread.Last = null;
        try
        {
            foreach (var turn in turns)
                foreach (var item in Arr(turn, "items"))
                    Item(sid, thread, item, Str(turn, "id"), live: false, done: true);
        }
        catch (OperationCanceledException) { } // stopped at the rewind boundary
    }

    private void Emit(Conversation thread, AgentEvent e)
    {
        if (children.TryGetValue(e.SessionId, out var parent))
        {
            var root = parent.Session;
            for (var depth = 0; depth < 10 && children.TryGetValue(root, out var ancestor); depth++) root = ancestor.Session;
            e = e switch
            {
                ToolCall tool => tool with { SessionId = root, Parent = parent.Call },
                PermissionAsked asked => asked with { SessionId = root },
                QuestionAsked asked => asked with { SessionId = root },
                RequestClosed closed => closed with { SessionId = root },
                _ => null!,
            };
            if (e is not null) Event?.Invoke(e);
            return;
        }
        if (childRoots.ContainsKey(e.SessionId))
        {
            if (e is ToolCall or PermissionAsked or QuestionAsked or RequestClosed)
                lock (gate)
                {
                    if (!earlyChildEvents.TryGetValue(e.SessionId, out var early)) earlyChildEvents[e.SessionId] = early = [];
                    if (early.Count < 200) early.Add(e);
                }
            return;
        }
        if (e is TextDelta or ReasoningDelta or ToolCall) thread.Last = e switch { TextDelta => "text", ReasoningDelta => "reasoning", _ => null };
        Event?.Invoke(e);
    }

    // A child is its own app-server thread; subscribe to its tools as soon as its ID is known, off the stdout reader.
    private void SubscribeChild(string id)
    {
        if (rpc is null || !childSubscriptions.TryAdd(id, true)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadAsync("", id);
            }
            catch (Exception ex) { log("subagent " + id + ": " + ex.Message); }
        });
    }

    // The command as the agent meant it: Codex wraps it in a login shell, its actions keep the plain command.
    private static string Command(JsonElement source, JsonElement[] actions)
    {
        if (actions.Length > 0 && actions.All(a => Str(a, "command") is not null)) return string.Join(" && ", actions.Select(a => Str(a, "command")));
        var command = Str(source, "command") ?? "";
        var quoted = System.Text.RegularExpressions.Regex.Match(command, @"^\S+ -l?c '(.*)'$", System.Text.RegularExpressions.RegexOptions.Singleline);
        return quoted.Success ? quoted.Groups[1].Value.Replace("'\\''", "'") : command;
    }

    private static (string Title, string[] Files, string? Diff) Change(JsonElement item)
    {
        var changes = Arr(item, "changes").ToArray();
        var files = changes.Select(c => Str(c, "path") ?? "").ToArray();
        var title = files.Length == 1 ? $"{Verb(changes[0])} {Path.GetFileName(files[0])}" : $"Edit {files.Length} files";
        var diff = string.Join("\n", changes.Select(c => (changes.Length > 1 ? $"--- {Path.GetFileName(Str(c, "path"))}\n" : "") + Str(c, "diff")?.TrimEnd()));
        return (title, files, diff.Length > 0 ? diff : null);
    }

    private static string Verb(JsonElement change) => Str(Obj(change, "kind"), "type") switch { "add" => "Write", "delete" => "Delete", _ => "Edit" };

    private static TokenUsage Tokens(JsonElement t) =>
        new((Num(t, "inputTokens") ?? 0) - (Num(t, "cachedInputTokens") ?? 0), Num(t, "outputTokens") ?? 0, Num(t, "reasoningOutputTokens") ?? 0, Num(t, "cachedInputTokens") ?? 0, 0);
}
