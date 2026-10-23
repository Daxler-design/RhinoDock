using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace rhDock.Agents;

// The contract between the Agent tab and one coding agent (ADR 0008), after MonoCode's HarnessAdapter and HarnessEvent.
// The host only sees these types; each adapter translates its agent's own protocol.

/// <summary>One coding agent behind the Agent tab. Calls may come from any thread; events arrive on background threads.</summary>
public interface IAgentHarness : IDisposable
{
    /// <summary>Starts the agent if needed and scopes it to the repository. The registry (<see cref="Harnesses"/>) has already found its executable.</summary>
    Task<AgentInfo> StartAsync(string repository);
    Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository);
    /// <summary>Replays a stored conversation as events, oldest first.</summary>
    Task LoadAsync(string repository, string sessionId);
    /// <summary>Creates an empty conversation with the options chosen so far. The host calls it just before the first prompt.</summary>
    Task<string> CreateAsync(string repository);
    /// <summary>Sends a prompt and completes when the turn ends.</summary>
    Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt);
    /// <summary>Adds a message to the running turn, which the agent reads at its next step. Returns at once.</summary>
    Task SteerAsync(string repository, string sessionId, AgentPrompt prompt);
    Task CancelAsync(string sessionId);
    /// <summary>
    /// Rewinds to before a user message: the message and everything after it leave the conversation, undoable until the next prompt.
    /// Returns false when the agent could not restore the files, so the host restores them itself.
    /// </summary>
    Task<bool> RewindAsync(string repository, string sessionId, string messageId);
    Task UndoRewindAsync(string repository, string sessionId);
    /// <summary>Answers a permission request with AllowOnce, AllowAlways, or RejectOnce; null cancels it. Each agent maps the decision to its own reply.</summary>
    Task ReplyPermissionAsync(string sessionId, string requestId, string? decision);
    /// <summary>Answers a question by field key; null dismisses it.</summary>
    Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer);
    Task<AgentCatalog> CatalogAsync(string repository, string? sessionId);
    /// <summary>Sets a <see cref="ConfigOption"/> for the conversation, or for the next one when <paramref name="sessionId"/> is null.</summary>
    Task SetOptionAsync(string repository, string? sessionId, string optionId, string value);
    /// <summary>
    /// Names a new conversation the agent left unnamed, with the agent's own model (<see cref="ConversationTitle"/>), and stores the
    /// name where the agent keeps it. The host calls it once, after the first turn of a conversation it created. Returns the name,
    /// or null for agents that name conversations themselves or cannot.
    /// </summary>
    Task<string?> TitleAsync(string repository, string sessionId, string message) => Task.FromResult<string?>(null);
    /// <summary>
    /// Forks a stored conversation into a new one that carries its history, for a side chat. The fork is the agent's own: rhDock
    /// pastes nothing. Returns the new id, or null when the agent cannot fork (the host then seeds a new conversation instead).
    /// </summary>
    Task<string?> ForkAsync(string repository, string sessionId) => Task.FromResult<string?>(null);
    /// <summary>Removes a conversation made by <see cref="ForkAsync"/> when its side chat closes. Agents whose forks are ephemeral, or that cannot delete, do nothing.</summary>
    Task DiscardAsync(string repository, string sessionId) => Task.CompletedTask;
    event Action<AgentEvent>? Event;
}

public sealed record AgentInfo(string Name, string? Version, string Executable);
/// <summary>A stored conversation. <c>Title</c> is the agent's own name for it, null when unnamed; <c>Preview</c> is its first prompt.</summary>
public sealed record ConversationInfo(string Id, string? Title, DateTimeOffset? Updated, string? Preview = null);
/// <summary>One value of an option. <c>Group</c> is the model provider, for a picker grouped by provider; <c>Recent</c> marks models the agent used lately.</summary>
public sealed record OptionChoice(string Value, string? Name, string? Group = null, bool Recent = false)
{
    /// <summary>A reasoning level under one name for every agent: low, medium, xhigh as Low, Medium, Extra High.</summary>
    public static OptionChoice Effort(string level) => new(level, level switch { "" => level, "xhigh" => "Extra High", _ => char.ToUpperInvariant(level[0]) + level[1..] });
}
/// <summary>A select option such as model, effort, or the agent's own mode. <c>Category</c> is model, thought_level, or mode.</summary>
public sealed record ConfigOption(string Id, string Name, string Category, string? Current, IReadOnlyList<OptionChoice> Choices);
public sealed record CommandInfo(string Name, string? Description);
public sealed record SkillInfo(string Id, string Name, string? Description);
public sealed record AgentCatalog(IReadOnlyList<ConfigOption> Options, IReadOnlyList<CommandInfo> Commands, IReadOnlyList<SkillInfo> Skills, bool Images, bool ListsConversations = true, bool LoadsConversations = true);
public sealed record AgentImage(string MimeType, string Data, string Name);
/// <summary>
/// One user message. <c>Context</c> holds named text blocks rhDock adds (the active Script, the Rhino selection); <c>Files</c> are absolute paths.
/// <c>Command</c> runs an agent command and <c>Skill</c> attaches a skill; <c>Compact</c> summarizes the conversation instead of prompting.
/// <c>Mode</c> is rhDock's permission mode (ask, edits, plan, all); agents with native modes apply it, and the host approves the rest.
/// </summary>
public sealed partial record AgentPrompt(string Text, IReadOnlyList<(string Name, string Text)> Context, IReadOnlyList<string> Files, IReadOnlyList<AgentImage> Images,
    string? Command = null, string? Skill = null, bool Compact = false, string Mode = "ask")
{
    /// <summary>The text with rhDock's context as tagged blocks, so a replayed message can show it as chips again.</summary>
    public string Tagged() => string.Concat(Context.Select(c => $"[rhdock://{c.Name}]\n{c.Text}\n[/rhdock]\n")) + Text;

    /// <summary><see cref="Tagged"/> plus the attached files as paths, for agents that read files themselves.</summary>
    public string WithFiles() => Tagged() + (Files.Count == 0 ? "" : "\n\nAttached files:\n" + string.Join("\n", Files.Select(f => "- " + f)));

    /// <summary>
    /// A sent message read back, the inverse of <see cref="WithFiles"/>: the user's text, the names of its context blocks, and its
    /// attached paths. Context sent by 0.3.1 as plain text counts as active-script. Text without rhDock's markup comes back unchanged.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Context, IReadOnlyList<string> Files) Parse(string sent)
    {
        var context = new List<string>();
        var files = new List<string>();
        var text = ContextBlock().Replace(sent, m => { context.Add(m.Groups["name"].Value); return ""; });
        text = LegacyContext().Replace(text, _ => { context.Add("active-script"); return ""; });
        text = FileList().Replace(text, m => { files.AddRange(m.Groups["path"].Captures.Select(c => c.Value.Trim())); return ""; });
        return (text, context, files);
    }

    /// <summary>
    /// A conversation title from a sent message: its first line without rhDock's markup; null when empty. The panel ellipsizes it to
    /// the space it has, so the cap only bounds the snapshot.
    /// </summary>
    public static string? Title(string? sent)
    {
        var line = sent is null ? "" : Parse(sent).Text.Trim().Split('\n')[0].Trim();
        return line.Length == 0 ? null : line.Length <= 160 ? line : line[..159] + "…";
    }

    [GeneratedRegex(@"\[rhdock://(?<name>[a-z-]+)\]\n[\s\S]*?\n\[/rhdock\]\n?")]
    private static partial Regex ContextBlock();
    [GeneratedRegex(@"^rhDock active Script: [\s\S]*?\nLast result: [^\n]*\n(?:Error: [^\n]*\n)?")]
    private static partial Regex LegacyContext();
    [GeneratedRegex(@"\n\nAttached files:\n(?:- (?<path>[^\n]+)\n?)+$")]
    private static partial Regex FileList();
}
/// <summary>How a turn ended: end_turn, cancelled, or error.</summary>
public sealed record TurnResult(string Stop, string? Error)
{
    /// <summary>
    /// The error for people. Agents often pass a provider's response through, such as <c>400 {"error":{"message":"…"}}</c>;
    /// that becomes <c>400 …</c>. Anything else comes back unchanged.
    /// </summary>
    public string? Message
    {
        get
        {
            if (Error is not { } error || error.IndexOf('{') is not (>= 0 and var at)) return Error;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(error[at..]);
                var root = json.RootElement;
                var message = Json.Str(Json.Obj(root, "error"), "message") ?? Json.Str(root, "message");
                return message is null ? Error : (error[..at].Trim() + " " + message).Trim();
            }
            catch (System.Text.Json.JsonException) { return Error; }
        }
    }
}
public sealed record TokenUsage(double Input, double Output, double Reasoning, double CacheRead, double CacheWrite)
{
    public double Total => Input + Output + Reasoning + CacheRead + CacheWrite;
    public TokenUsage Add(TokenUsage o) => new(Input + o.Input, Output + o.Output, Reasoning + o.Reasoning, CacheRead + o.CacheRead, CacheWrite + o.CacheWrite);
}
public sealed record QuestionOption(string Value, string Label, string? Description);
/// <summary>One question. Type is text, choice, multi, boolean, or number; <c>Custom</c> allows a typed answer besides the options.</summary>
public sealed record QuestionField(string Key, string? Title, string? Description, string Type, IReadOnlyList<QuestionOption> Options, bool Custom, bool Required);

public abstract record AgentEvent(string SessionId);
/// <summary>A user message from a replayed conversation.</summary>
public sealed record UserMessage(string SessionId, string MessageId, string Text, IReadOnlyList<AgentImage> Images, IReadOnlyList<string> Files) : AgentEvent(SessionId);
/// <summary>The agent stored a prompt the host sent, under this id.</summary>
public sealed record PromptAccepted(string SessionId, string MessageId) : AgentEvent(SessionId);
/// <summary>The conversation is rewound to before this user message; <c>Text</c> is that message.</summary>
public sealed record Rewound(string SessionId, string MessageId, string Text) : AgentEvent(SessionId);
public sealed record TextDelta(string SessionId, string Text) : AgentEvent(SessionId);
/// <summary>Images the agent put in its reply (embedded raster data); they show with the reply.</summary>
public sealed record MediaMessage(string SessionId, IReadOnlyList<AgentImage> Images) : AgentEvent(SessionId);
/// <summary>The agent could not use the requested permission mode (for example Auto for an account, model, or policy that lacks it); rhDock returns to Ask and says why.</summary>
public sealed record ModeUnavailable(string SessionId, string Mode, string Reason) : AgentEvent(SessionId);
public sealed record ReasoningDelta(string SessionId, string Text) : AgentEvent(SessionId);
/// <summary>
/// A tool call started or changed; null fields keep their last value. Kind is Read, Edit, Delete, Move, Search, Execute, Fetch, Think, or Other;
/// Status is Pending, InProgress, Completed, or Failed. <c>Parent</c> is the call of the subagent that made it, which shows it as one of its steps.
/// </summary>
public sealed record ToolCall(string SessionId, string CallId, string? Title, string? Kind, string? Status, string? Detail, IReadOnlyList<string>? Files, string? Parent = null, IReadOnlyList<AgentImage>? Images = null) : AgentEvent(SessionId);
/// <summary>The agent's plan; each entry is a status (Pending, InProgress, Completed) and its text.</summary>
public sealed record PlanUpdate(string SessionId, IReadOnlyList<(string Status, string Content)> Entries) : AgentEvent(SessionId);
/// <summary>
/// A plan the agent proposes in plan mode (Claude Code's ExitPlanMode, Codex's plan item), as Markdown. Following MonoCode, nothing
/// is approved or declined: the user builds it with a later prompt or replies to refine it.
/// </summary>
public sealed record PlanProposed(string SessionId, string Text) : AgentEvent(SessionId);
public sealed record PermissionAsked(string SessionId, string RequestId, string Title, string? Kind, string? Detail) : AgentEvent(SessionId);
public sealed record QuestionAsked(string SessionId, string RequestId, string? Title, IReadOnlyList<QuestionField> Fields) : AgentEvent(SessionId);
/// <summary>A permission request or question was answered or withdrawn, possibly elsewhere.</summary>
public sealed record RequestClosed(string SessionId, string RequestId) : AgentEvent(SessionId);
/// <summary>Context window use after the latest model request, and the conversation's cost so far.</summary>
public sealed record ContextUsage(string SessionId, double? Used, double? Size, double? Cost) : AgentEvent(SessionId);
/// <summary>Token totals for the turn that just ended.</summary>
public sealed record TurnUsage(string SessionId, TokenUsage Tokens) : AgentEvent(SessionId);
public sealed record Renamed(string SessionId, string Title) : AgentEvent(SessionId);
public sealed record Notice(string SessionId, string Text) : AgentEvent(SessionId);
/// <summary>Models, modes, commands, or skills changed (OpenCode loads them after its first call); <c>SessionId</c> is empty.</summary>
public sealed record CatalogChanged() : AgentEvent("");
/// <summary>The agent process ended; <c>SessionId</c> is empty.</summary>
public sealed record Exited(string Message) : AgentEvent("");
