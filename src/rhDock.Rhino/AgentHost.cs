using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rhino;
using rhDock.Agents;

namespace rhDock.Rhino;

/// <summary>
/// The Agent tab's host side (ADR 0007, ADR 0008): each open conversation retains its harness while another is selected.
/// Harness events arrive on background threads and only change state under <see cref="gate"/>; the panel reads a
/// bounded copy through <see cref="Snapshot"/> on the UI thread. Holds no Script-owned types or values.
/// </summary>
internal sealed partial class AgentHost
{
    private static AgentHost? current;
    private static readonly object hostsGate = new();
    private static readonly List<AgentHost> hosts = [];
    public static AgentHost Current
    {
        get { lock (hostsGate) { if (current is null) { current = new(); hosts.Add(current); } return current; } }
    }
    public static void Shutdown()
    {
        AgentHost[] closing;
        lock (hostsGate) { closing = [.. hosts]; hosts.Clear(); current = null; }
        foreach (var host in closing) host.Stop();
    }

    private static AgentHost[] OpenHosts() { lock (hostsGate) return [.. hosts]; }
    private readonly string viewId = Guid.NewGuid().ToString("N");
    private string? repositoryKey;
    private bool disposed;
    private (string Id, string Value)[] initialOptions = [];
    private readonly Func<string, IAgentHarness>? factory;
    internal AgentHost(Func<string, IAgentHarness>? factory = null) { this.factory = factory; }

    private void Activate()
    {
        lock (hostsGate) { hosts.Remove(this); hosts.Add(this); current = this; }
        lock (gate) { unread = false; favorites.Clear(); favorites.UnionWith(ReadFavorites()); }
        RhinoDockPlugin.Instance?.Settings.SetString("AgentId", agentId);
        Changed();
    }

    private AgentHost OpenConversation(string repository, string? id = null)
    {
        AgentHost next;
        lock (gate) next = new(factory) { agentId = id ?? agentId, repositoryKey = repository, installed = installed,
            mode = id is null ? mode : "ask", initialOptions = id is null ? catalog.Options.Where(o => o.Current is not null && o.Id != "mode").Select(o => (o.Id, o.Current!)).ToArray() : [] };
        next.Activate();
        return next;
    }

    // A path inside the Loaded Repository relative to it, under either spelling of the root (macOS /var is /private/var); else null.
    // The path may not exist yet, so only the root is resolved. Callers hold the gate.
    private string? Relative(string path)
    {
        if (sessionCwd is not { } root || !Path.IsPathFullyQualified(path)) return null;
        foreach (var under in new[] { root, AgentProcess.RealPath(root) })
            if (Path.GetRelativePath(under, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative)) return relative;
        return null;
    }

    // The header and the conversation list show one title: the agent's, else the first prompt's first line. Callers hold the gate.
    private string? ShownTitle() => title ?? AgentPrompt.Title(items.FirstOrDefault(i => i.Kind == "user")?.Text);

    // Done means unread: a turn that finished while another conversation was on screen, until this one is opened.
    private string? ActivityStatus() => permission is not null || question is not null ? "input"
        : status is "working" or "starting" ? "running" : status is "error" or "missing" || turnStop == "error" ? "error" : unread ? "done" : null;
    private bool unread;

    private sealed record OpenView(string Id, string AgentId, string? Repository, string? SessionId, string? Title, string? Activity);
    private OpenView View()
    {
        lock (gate) return new(viewId, agentId, repositoryKey ?? sessionCwd, sessionId, ShownTitle(), ActivityStatus());
    }

    // ponytail: every snapshot carries the whole bounded conversation; send deltas if long conversations make the panel lag.
    private const int MaxItems = 200, MaxText = 12000, MaxDetail = 4000, MaxLogLine = 4000, MaxPictures = 48, MaxConversations = 50, MaxAttachments = 8,
        MaxImageBase64 = 8 * 1024 * 1024, MaxFiles = 3000, MaxCheckpoints = 10;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "rhdock-agent.log");
    private static readonly object LogGate = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim work = new(1, 1);
    private readonly List<Item> items = [];
    private readonly Dictionary<int, string> pictures = [];
    private readonly List<Attachment> attachments = [];
    private readonly List<Queued> queue = [];
    // Files before each prompt, for rewinding a repository outside Git, where OpenCode keeps no snapshots. Keyed by item id until the agent names the message.
    private readonly Dictionary<string, RepositoryCheckpoint> checkpoints = [];
    private IAgentHarness? harness;
    private AgentInfo? info;
    private AgentCatalog catalog = new([], [], [], true);
    private string status = "idle"; // idle, starting, ready, working, missing, error
    private string mode = "ask"; // ask, edits, plan, all
    private string? error, sessionId, sessionCwd, title;
    private Conversation[] conversations = [];
    private PermissionAsked? permission;
    private QuestionAsked? question;
    private Rewound? rewound;
    private RepositoryCheckpoint? redo;
    private object? usage;
    private TokenUsage? turn;
    private double? elapsedMs;
    private string? turnStop;
    private (string Status, string Content)[] plan = [];
    private string agentId = Harnesses.Get(RhinoDockPlugin.Instance?.Settings.GetString("AgentId", Harnesses.DefaultId)).Id;
    private Dictionary<string, string?> installed = [];
    private readonly List<PermissionAsked> permissions = [];
    private readonly List<QuestionAsked> questions = [];
    private bool filesRestored;
    private readonly HashSet<string> favorites = ReadFavorites();

    private static HashSet<string> ReadFavorites()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(RhinoDockPlugin.Instance?.Settings.GetString("AgentFavorites", "[]") ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    public void Favorite(string value)
    {
        lock (gate)
        {
            if (!catalog.Options.Any(o => o.Category == "model" && o.Choices.Any(c => c.Value == value))) return;
            var key = agentId + ":" + value;
            if (!favorites.Remove(key)) favorites.Add(key);
            RhinoDockPlugin.Instance?.Settings.SetString("AgentFavorites", JsonSerializer.Serialize(favorites));
        }
        Changed();
    }
    private bool filesWanted, changesDirty, catalogQueued;
    private static int nextId;
    private int changesRunning;
    private static int NextId() => Interlocked.Increment(ref nextId);
    public string ViewId => viewId;
    private string[] files = [];
    private RepositoryCheckpoint? baseline;
    private IReadOnlyList<FileChange> changes = [];
    private bool refreshQueued;

    private sealed class Item
    {
        public required int Id;
        public required string Kind; // user, assistant, thought, tool, proposal, notice
        public string Text = "";
        public string? ToolCallId, Title, ToolKind, Status, Detail, MessageId, Parent;
        public double? ElapsedMs;
        public string[] Files = [], Chips = [];
        public int[] Images = [];
    }

    // Title is the agent's own name, null when unnamed; Preview is the first prompt, shown until there is a name.
    private sealed record Conversation(string Id, string? Title, string? UpdatedAt, string? Preview);
    private sealed record Attachment(int Id, string Name, string Kind, string? MimeType, string? Data, string? Path, string? Thumb);
    // Steer: pending for the running turn, sent when its current tool call finishes so a stray Enter can still be removed.
    private sealed record Queued(int Id, string Repository, string Text, AgentPrompt Prompt, string[] Chips, int[] Images, bool Steer = false);

    private static string? PathFor(string id) => RhinoDockPlugin.Instance?.Settings.GetString("AgentPath." + id,
        id == Harnesses.DefaultId ? RhinoDockPlugin.Instance.Settings.GetString("AgentPath", "") : "") is { Length: > 0 } path ? path : null;
    private string? UserPath => PathFor(agentId);

    public object Snapshot()
    {
        // Never take another conversation's lock while holding this one's lock.
        var views = OpenHosts().Select(h => h.View()).ToArray();
        lock (gate)
            return new
            {
                viewId,
                status, error, agent = info is null ? null : $"{info.Name} {info.Version}".Trim(), executable = info?.Executable, userPath = UserPath, repository = repositoryKey ?? sessionCwd, sessionId, title = ShownTitle(),
                usage, turn = new { input = turn?.Input, output = turn?.Output, cacheRead = turn?.CacheRead, cacheWrite = turn?.CacheWrite, reasoning = turn?.Reasoning, total = turn?.Total, elapsedMs },
                favorites = favorites.Where(f => f.StartsWith(agentId + ":", StringComparison.Ordinal)).Select(f => f[(agentId.Length + 1)..]).ToArray(),
                log = LogPath, mode = Harnesses.Get(agentId).Features.Permissions ? mode : "all", images = catalog.Images, canLoad = catalog.LoadsConversations, agentId, agentName = Harnesses.Get(agentId).Name, features = Harnesses.Get(agentId).Features,
                agents = Harnesses.All.Select(h => new { id = h.Id, name = h.Name, installed = installed.GetValueOrDefault(h.Id) is not null, install = h.Install, docs = h.Docs,
                    activities = views.Where(v => v.AgentId == h.Id && v.Repository == (repositoryKey ?? sessionCwd) && v.Activity is not null).Select(v => v.Activity).ToArray() }).ToArray(),
                plan = plan.Select(p => new { status = p.Status, content = p.Content }).ToArray(),
                items = items.Select(i => new { id = i.Id, kind = i.Kind, text = i.Text, title = i.Title, toolKind = i.ToolKind, status = i.Status, detail = i.Detail,
                    files = i.Files, chips = i.Chips, images = i.Images, callId = i.ToolCallId, parent = i.Parent, elapsedMs = i.ElapsedMs, checkpoint = i.MessageId is not null && Harnesses.Get(agentId).Features.Rewind }).ToArray(),
                conversations = views.Where(v => v.AgentId == agentId && v.Repository == (repositoryKey ?? sessionCwd) && (v.SessionId is not null || v.Activity is not null || v.Id == viewId))
                    .Select(v => new { id = v.SessionId ?? "local:" + v.Id, title = v.Title, updatedAt = (string?)null, activity = v.Activity, open = v.Id == viewId, live = true })
                    .Concat(conversations.Where(c => !views.Any(v => v.AgentId == agentId && v.Repository == (repositoryKey ?? sessionCwd) && v.SessionId == c.Id))
                        .Select(c => new { id = c.Id, title = c.Title ?? c.Preview, updatedAt = c.UpdatedAt, activity = (string?)null, open = c.Id == sessionId, live = false })).ToArray(),
                config = catalog.Options.Select(o => new { id = o.Id, name = o.Name, category = o.Category, current = o.Current,
                    options = o.Choices.Select(c => new { value = c.Value, name = c.Name, group = c.Group, recent = c.Recent }).ToArray() }).ToArray(),
                commands = catalog.Commands.Select(c => new { name = c.Name, description = c.Description ?? "" }).ToArray(),
                skills = catalog.Skills.Select(s => new { id = s.Id, name = s.Name, description = s.Description ?? "" }).ToArray(),
                attachments = attachments.Select(a => new { id = a.Id, name = a.Name, kind = a.Kind, thumb = a.Thumb }).ToArray(),
                queued = queue.Select(q => new { id = q.Id, text = q.Text, steer = q.Steer }).ToArray(),
                rewound = rewound is { } r ? new { id = r.MessageId, text = r.Text, filesRestored } : null,
                files = filesWanted ? files : null,
                changes = changes.Select(c => new { path = c.Path, status = c.Status, added = c.Added, removed = c.Removed, diff = c.Diff }).ToArray(),
                permission = permission is { } p ? new { title = p.Title, kind = p.Kind, detail = p.Detail, options = Decisions } : null,
                question = question is { } q ? new { id = q.RequestId, title = q.Title, fields = q.Fields.Select(f => new { key = f.Key, title = f.Title, description = f.Description, type = f.Type,
                    options = f.Options.Select(o => new { value = o.Value, label = o.Label, description = o.Description }).ToArray(), custom = f.Custom, required = f.Required }).ToArray() } : null,
            };
    }

    // Panel actions, called on the UI thread.

    public void Connect(string repository)
    {
        if (repositoryKey is { } previous && previous != repository)
        {
            var existing = OpenHosts().LastOrDefault(h => h != this && h.View() is { } v && v.AgentId == agentId && v.Repository == repository);
            if (existing is not null) { existing.Activate(); existing.Connect(repository); }
            else OpenConversation(repository).Connect(repository);
            return;
        }
        lock (gate)
        {
            repositoryKey = repository;
            if (status is "starting" or "working" || status == "ready" && sessionCwd == repository) return;
            status = "starting";
        }
        Run(async () =>
        {
            var found = Harnesses.Installed(PathFor);
            lock (gate) installed = found;
            await EnsureStarted(repository);
            await ListConversations();
        });
    }

    public void SelectAgent(string repository, string id)
    {
        if (!Harnesses.All.Any(h => h.Id == id)) return;
        if (agentId == id) return;
        var existing = OpenHosts().LastOrDefault(h => h.View() is { } v && v.AgentId == id && v.Repository == repository);
        if (existing is not null) { existing.Activate(); existing.Connect(repository); }
        else OpenConversation(repository, id).Connect(repository);
    }

    /// <summary>
    /// Sends a prompt. While a turn runs it is queued for after the turn, or with <paramref name="steer"/> held for the running turn.
    /// <paramref name="context"/> and <paramref name="files"/> come from the panel's chips and mentions.
    /// </summary>
    public void Prompt(string repository, string text, IReadOnlyList<(string Name, string Text)> context, IReadOnlyList<string> files, string[] chips, bool steer = false)
    {
        lock (gate)
        {
            var images = new List<AgentImage>();
            var paths = files.ToList();
            var sent = new List<int>();
            foreach (var a in attachments)
                if (a.Kind == "image" && a.Data is not null) { images.Add(new AgentImage(a.MimeType ?? "image/png", a.Data, a.Name)); sent.Add(a.Id); }
                else if (a.Path is not null) { paths.Add(a.Path); chips = [.. chips, a.Name]; }
            attachments.Clear();
            var prompt = new AgentPrompt(text, context, paths, images, Mode: mode);
            var slash = text.StartsWith('/') ? text[1..].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) : [];
            var rest = slash.Length > 1 ? slash[1] : "";
            if (slash is ["compact", ..] && (Harnesses.Get(agentId).Features.Compact || catalog.Commands.Any(c => c.Name == "compact"))) prompt = prompt with { Compact = true };
            else if (slash.Length > 0 && catalog.Commands.FirstOrDefault(c => c.Name == slash[0]) is { } command) prompt = prompt with { Command = command.Name, Text = rest };
            else if (slash.Length > 0 && catalog.Skills.FirstOrDefault(k => k.Id.Equals(slash[0], StringComparison.OrdinalIgnoreCase) || k.Name.Equals(slash[0], StringComparison.OrdinalIgnoreCase)) is { } skill)
                prompt = prompt with { Skill = skill.Id, Text = rest };
            var queued = new Queued(NextId(), repository, text, prompt, chips, [.. sent],
                steer && Harnesses.Get(agentId).Features.Steer && status == "working" && sessionId is not null && !prompt.Compact && prompt.Command is null);
            if (status is "starting" or "working") { queue.Add(queued); Changed(); return; }
            Send(queued);
        }
    }

    /// <summary>Moves a queued prompt into the running turn instead of waiting for the turn to end.</summary>
    public void SteerQueued(int id)
    {
        lock (gate)
        {
            var index = queue.FindIndex(q => q.Id == id);
            if (!Harnesses.Get(agentId).Features.Steer || status != "working" || sessionId is null || index < 0 || queue[index].Prompt is not { Compact: false, Command: null }) return;
            queue[index] = queue[index] with { Steer = true };
        }
        Changed();
    }

    // Callers hold the gate. Called when a tool call finishes; the agent reads the messages at the turn's next step.
    private void SteerPending()
    {
        if (status != "working" || sessionId is null) return;
        foreach (var pending in queue.Where(q => q.Steer).ToArray()) { queue.Remove(pending); SteerNow(pending); }
    }

    // Callers hold the gate.
    private void SteerNow(Queued prompt)
    {
        var item = new Item { Id = NextId(), Kind = "user", Text = Trim(prompt.Text, MaxText), Chips = prompt.Chips, Images = prompt.Images };
        Add(item);
        var id = sessionId!;
        if (harness is { } h) Background(async () =>
        {
            var before = RepositoryCheckpoint.Capture(prompt.Repository);
            lock (gate) checkpoints["item:" + item.Id] = before;
            await h.SteerAsync(prompt.Repository, id, prompt.Prompt with { Mode = mode });
        });
        Changed();
    }

    // Callers hold the gate.
    private void Send(Queued prompt)
    {
        var item = new Item { Id = NextId(), Kind = "user", Text = Trim(prompt.Text, MaxText), Chips = prompt.Chips, Images = prompt.Images };
        Add(item);
        status = "working";
        error = null;
        Changed();
        Run(async () =>
        {
            var agent = await EnsureStarted(prompt.Repository);
            lock (gate)
            {
                if (!items.Contains(item)) Add(item);
                for (var i = 0; i < item.Images.Length; i++) Remember(item.Images[i], prompt.Prompt.Images[i].MimeType, prompt.Prompt.Images[i].Data);
            }
            string? id;
            lock (gate) id = sessionCwd == prompt.Repository ? sessionId : null;
            // The conversation is created with the first prompt, so starting or choosing New leaves nothing empty behind.
            var created = id is null;
            if (id is null)
            {
                id = await agent.CreateAsync(prompt.Repository);
                lock (gate) { sessionId = id; sessionCwd = prompt.Repository; }
            }
            var checkpoint = RepositoryCheckpoint.Capture(prompt.Repository);
            lock (gate)
            {
                checkpoints["item:" + item.Id] = checkpoint;
                baseline ??= checkpoint;
                rewound = null;
                redo = null;
                plan = [];
                turn = null;
                elapsedMs = null;
                turnStop = null;
                status = "working";
            }
            Changed();
            var timer = Stopwatch.StartNew();
            TurnResult result;
            try
            {
                string sendingMode;
                string? nativeMode;
                lock (gate) { sendingMode = mode; nativeMode = NativeMode(catalog.Options.FirstOrDefault(o => o.Id == "mode"), sendingMode); }
                if (nativeMode is not null) await agent.SetOptionAsync(prompt.Repository, id, "mode", nativeMode);
                result = await agent.PromptAsync(prompt.Repository, id, prompt.Prompt with { Mode = sendingMode });
            }
            finally { lock (gate) { elapsedMs = timer.Elapsed.TotalMilliseconds; item.ElapsedMs = elapsedMs; permission = null; question = null; permissions.Clear(); questions.Clear(); } }
            bool shown;
            lock (hostsGate) shown = current == this;
            lock (gate) { turnStop = result.Stop; unread = !shown && result.Stop == "end_turn"; }
            if (result.Stop == "cancelled") AddNotice("Cancelled.");
            else if (result.Stop == "error") AddNotice($"Stopped: {result.Message ?? "the turn failed"}");
            RefreshChanges();
            await ListConversations();
            if (created && result.Stop == "end_turn" && prompt.Prompt is { Compact: false, Command: null }) NameNew(agent, prompt.Repository, id, prompt.Prompt.Text);
        });
    }

    // A conversation created here that the agent left unnamed after its first turn gets one title from the agent (ConversationTitle),
    // which the agent stores, so it is never asked again. A failure keeps the first-prompt title and shows nothing.
    private void NameNew(IAgentHarness agent, string repository, string id, string message)
    {
        lock (gate) if (title is not null || message.Trim().Length == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await agent.TitleAsync(repository, id, message) is not { } named) return;
                lock (gate) if (sessionId == id) title ??= named;
                Changed();
                await ListConversations();
            }
            catch (Exception ex) { Log("title " + ex.Message); }
        });
    }

    public void Unqueue(int id)
    {
        lock (gate) queue.RemoveAll(q => q.Id == id);
        Changed();
    }

    public void Cancel()
    {
        string? id;
        lock (gate) { id = sessionId; queue.Clear(); permission = null; question = null; permissions.Clear(); questions.Clear(); }
        Changed();
        if (harness is { } h && id is not null) Background(() => h.CancelAsync(id));
    }

    public void NewConversation(string repository)
    {
        lock (gate) if (sessionId is null && items.Count == 0) return;
        OpenConversation(repository).Connect(repository);
    }

    public void Load(string repository, string id)
    {
        var existing = OpenHosts().LastOrDefault(h => h.View() is { } v && v.AgentId == agentId && v.Repository == repository && (v.SessionId == id || "local:" + v.Id == id));
        if (existing is not null) { existing.Activate(); return; }
        lock (gate) if (!catalog.LoadsConversations) return;
        var next = OpenConversation(repository);
        lock (next.gate) { next.conversations = conversations; next.status = "starting"; }
        next.LoadStored(repository, id);
    }

    private void LoadStored(string repository, string id)
    {
        Run(async () =>
        {
            var agent = await EnsureStarted(repository);
            lock (gate) { ResetConversation(); sessionId = id; sessionCwd = repository; title = conversations.FirstOrDefault(c => c.Id == id)?.Title; }
            Changed();
            await agent.LoadAsync(repository, id);
            await RefreshCatalog(repository);
        });
    }

    // Every agent gets the same buttons; the adapter maps the decision.
    private static readonly object[] Decisions =
        [new { id = "AllowOnce", name = "Allow once", kind = "AllowOnce" }, new { id = "AllowAlways", name = "Always allow", kind = "AllowAlways" }, new { id = "RejectOnce", name = "Reject", kind = "RejectOnce" }];

    /// <summary>Answers the open permission request with AllowOnce, AllowAlways, or RejectOnce; null rejects it.</summary>
    public void Answer(string? choiceId)
    {
        PermissionAsked? open;
        lock (gate) { open = permission; permission = permissions.FirstOrDefault(); if (permission is not null) permissions.RemoveAt(0); }
        Changed();
        if (open is not null && harness is { } h) Background(() => h.ReplyPermissionAsync(open.SessionId, open.RequestId, choiceId));
    }

    /// <summary>Answers the open question by field key; an undefined or null answer dismisses it.</summary>
    public void AnswerQuestion(JsonElement answers)
    {
        QuestionAsked? open;
        lock (gate) { open = question; question = questions.FirstOrDefault(); if (question is not null) questions.RemoveAt(0); }
        Changed();
        if (open is null || harness is not { } h) return;
        var values = answers.ValueKind == JsonValueKind.Object
            ? answers.EnumerateObject().ToDictionary(p => p.Name, p => JsonNode.Parse(p.Value.GetRawText()))
            : null;
        Background(() => h.ReplyQuestionAsync(open.SessionId, open.RequestId, values));
    }

    public void SetOption(string configId, string value)
    {
        string? id, cwd;
        lock (gate) (id, cwd) = (sessionId, sessionCwd);
        if (harness is not { } h || cwd is null) return;
        Background(async () =>
        {
            await h.SetOptionAsync(cwd, id, configId, value);
            await RefreshCatalog(cwd);
        });
    }

    /// <summary>Ask, Accept edits, Plan, or Allow all. Plan also switches the agent to its plan mode when it has one.</summary>
    public void SetMode(string value)
    {
        if (value is not ("ask" or "edits" or "plan" or "all")) return;
        string? agentMode;
        lock (gate)
        {
            mode = value;
            var option = catalog.Options.FirstOrDefault(o => o.Id == "mode");
            agentMode = NativeMode(option, value);
            if (agentMode == option?.Current) agentMode = null;
        }
        if (agentMode is not null) SetOption("mode", agentMode);
        Changed();
    }

    private static string? NativeMode(ConfigOption? option, string value) => option?.Choices.Any(c => c.Value == "plan") == true
        ? value == "plan" ? "plan" : option.Choices.FirstOrDefault(c => c.Value == "build")?.Value ?? option.Choices.FirstOrDefault(c => c.Value != "plan")?.Value
        : null;

    public void SetPath(string? path)
    {
        RhinoDockPlugin.Instance?.Settings.SetString("AgentPath." + agentId, path?.Trim().Trim('"') ?? "");
        Stop();
        lock (gate) { disposed = false; status = "idle"; error = null; }
        Changed();
    }

    public void WantFiles(string repository, bool wanted)
    {
        lock (gate) filesWanted = wanted;
        if (wanted) _ = Task.Run(() =>
        {
            var list = RepositoryCheckpoint.List(repository).Take(MaxFiles).ToArray();
            lock (gate) files = list;
            Changed();
        });
        else Changed();
    }

    public void Attach(string name, string? mimeType, string? data, string? path, string? thumb)
    {
        lock (gate)
        {
            if (attachments.Count >= MaxAttachments) { error = $"At most {MaxAttachments} attachments per message."; Changed(); return; }
            if (data is { Length: > MaxImageBase64 }) { error = "Images over 6 MB cannot be attached."; Changed(); return; }
            var id = NextId();
            attachments.Add(new Attachment(id, name, data is not null ? "image" : "file", mimeType, data, path, thumb));
            if (data is not null) Remember(id, mimeType ?? "image/png", data);
        }
        Changed();
    }

    /// <summary>Attaches a file from disk; common images are sent as images, anything else as a file the agent reads.</summary>
    public void AttachFile(string path)
    {
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null,
        };
        if (mime is not null && new FileInfo(path).Length * 4 / 3 < MaxImageBase64)
        {
            var data = Convert.ToBase64String(File.ReadAllBytes(path));
            Attach(Path.GetFileName(path), mime, data, path, data.Length < 60_000 ? $"data:{mime};base64,{data}" : null);
        }
        else Attach(Path.GetFileName(path), null, null, path, null);
    }

    public void Detach(int id)
    {
        lock (gate) attachments.RemoveAll(a => a.Id == id);
        Changed();
    }

    /// <summary>Accepts every change since the review baseline.</summary>
    public void Keep(string repository)
    {
        _ = Task.Run(() =>
        {
            var next = RepositoryCheckpoint.Capture(repository);
            lock (gate) { baseline = next; changes = []; }
            Changed();
        });
    }

    public void Revert(string path)
    {
        RepositoryCheckpoint? from;
        lock (gate) from = baseline;
        if (from is null) return;
        _ = Task.Run(() =>
        {
            try { from.Restore(path); }
            catch (Exception ex) { lock (gate) error = ex.Message; }
            RefreshChanges();
        });
    }

    /// <summary>Rewinds to before a prompt with the agent's own snapshots: its files go back and the conversation continues from there.</summary>
    public void Restore(int itemId)
    {
        string? repo, id, message;
        lock (gate)
        {
            if (!Harnesses.Get(agentId).Features.Rewind || status is "starting" or "working") return;
            (repo, id, message) = (sessionCwd, sessionId, items.FirstOrDefault(i => i.Id == itemId)?.MessageId);
            if (repo is null || id is null || message is null) return;
            status = "starting";
        }
        Run(async () =>
        {
            var current = RepositoryCheckpoint.Capture(repo);
            var restored = await harness!.RewindAsync(repo, id, message);
            lock (gate) { redo = current; filesRestored = restored; }
            RepositoryCheckpoint? files;
            lock (gate) checkpoints.TryGetValue(message, out files);
            if (!restored)
            {
                if (files is not null)
                {
                    files.Restore();
                    lock (gate) filesRestored = true;
                }
                else AddNotice("The conversation is rewound, but files were not restored: the agent has no file snapshot and rhDock kept no copy from before this prompt.");
            }
            await Reload(repo, id);
        });
    }


    /// <summary>Brings back what the last rewind removed, files included.</summary>
    public void UndoRewind()
    {
        string? repo, id;
        lock (gate) { if (status is "starting" or "working" || rewound is null) return; (repo, id) = (sessionCwd, sessionId); status = "starting"; }
        if (repo is null || id is null) return;
        Run(async () =>
        {
            await harness!.UndoRewindAsync(repo, id);
            RepositoryCheckpoint? files;
            lock (gate) { files = redo; redo = null; }
            files?.Restore();
            await Reload(repo, id);
        });
    }

    // Replays the same conversation after a rewind or Undo. Its title, checkpoints, and context use survive: most agents report usage only during a turn.
    private async Task Reload(string repository, string id)
    {
        lock (gate)
        {
            var kept = (title, usage, checkpoints: checkpoints.ToArray());
            ResetConversation();
            (title, usage) = (kept.title, kept.usage);
            foreach (var (k, v) in kept.checkpoints) checkpoints[k] = v;
        }
        Changed();
        await harness!.LoadAsync(repository, id);
        RefreshChanges();
    }

    public void Stop()
    {
        IAgentHarness? old;
        lock (gate)
        {
            disposed = true;
            old = harness;
            harness = null;
            info = null;
            sessionId = null;
            queue.Clear();
            permissions.Clear();
            questions.Clear();
            permission = null;
            question = null;
            if (status is not ("missing" or "error")) status = "idle";
        }
        if (old is not null) { old.Event -= OnEvent; old.Dispose(); }
    }

    // Work on a background thread, one operation at a time; failures become the tab's error state.

    private void Run(Func<Task> operation) => _ = Task.Run(async () =>
    {
        await work.WaitAsync();
        try
        {
            lock (gate) if (disposed) return;
            await operation();
            lock (gate) if (!disposed && status is "starting" or "working") status = "ready";
        }
        catch (FileNotFoundException ex) { lock (gate) { status = "missing"; error = ex.Message; queue.Clear(); } }
        catch (Exception ex) { Log("error " + ex); lock (gate) { status = "error"; error = ex.Message; queue.Clear(); } }
        finally { work.Release(); Changed(); }
        lock (gate)
            if (status == "ready" && queue.Count > 0) { var next = queue.Find(q => q.Steer) ?? queue[0]; queue.Remove(next); Send(next); }
    });

    // A side call that must not wait for a running turn, such as cancel or a permission answer; a failure shows as the error line.
    private void Background(Func<Task> call) => _ = Task.Run(async () =>
    {
        try { await call(); }
        catch (Exception ex) { Log("error " + ex); lock (gate) error = ex.Message; Changed(); }
    });

    private async Task<IAgentHarness> EnsureStarted(string repository)
    {
        IAgentHarness agent;
        bool fresh;
        lock (gate)
        {
            if (disposed) throw new OperationCanceledException("The conversation was closed.");
            fresh = harness is null || sessionCwd != repository;
            if (harness is null)
            {
                var selected = Harnesses.Get(agentId);
                if (factory is not null) harness = factory(agentId);
                else
                {
                    var executable = Harnesses.Find(selected, UserPath, AgentProcess.ExtendedPath())
                        ?? throw new FileNotFoundException($"{selected.Name} was not found. {selected.Install}");
                    harness = selected.Create(executable, text => Log($"[{agentId}/{viewId}] {text}"));
                }
                harness.Event += OnEvent;
            }
            agent = harness;
            error = null;
        }
        var started = await agent.StartAsync(repository);
        lock (gate)
        {
            info = started;
            if (sessionCwd != repository) { sessionId = null; ResetConversation(); }
            sessionCwd = repository;
        }
        if (fresh || catalog.Options.Count == 0) await RefreshCatalog(repository);
        foreach (var (id, value) in initialOptions)
            if (catalog.Options.Any(o => o.Id == id && o.Choices.Any(c => c.Value == value))) await agent.SetOptionAsync(repository, null, id, value);
        if (initialOptions.Length > 0) { initialOptions = []; await RefreshCatalog(repository); }
        return agent;
    }

    private async Task RefreshCatalog(string repository)
    {
        if (harness is not { } agent) return;
        string? id;
        lock (gate) id = sessionId;
        var next = await agent.CatalogAsync(repository, id);
        lock (gate) catalog = next;
        Changed();
    }

    // Callers hold the gate. Images stay out of snapshots; the panel asks for each one it shows.
    private void Remember(int id, string mimeType, string data)
    {
        pictures[id] = $"data:{mimeType};base64,{data}";
        // ponytail: drops the oldest beyond a fixed count; a size budget would suit very large screenshots better.
        foreach (var old in pictures.Keys.Order().Take(Math.Max(0, pictures.Count - MaxPictures)).ToArray()) pictures.Remove(old);
    }

    /// <summary>The data URL of an image shown in the conversation or attached to the next message.</summary>
    public string? Picture(int id)
    {
        var own = OwnPicture(id);
        return own ?? OpenHosts().Select(h => h.OwnPicture(id)).FirstOrDefault(p => p is not null);
    }

    private string? OwnPicture(int id) { lock (gate) return pictures.GetValueOrDefault(id); }

    // Callers hold the gate.
    private void ResetConversation()
    {
        foreach (var id in pictures.Keys.Where(id => attachments.All(a => a.Id != id)).ToArray()) pictures.Remove(id);
        items.Clear();
        checkpoints.Clear();
        rewound = null;
        baseline = null;
        changes = [];
        title = null;
        usage = null;
        turn = null;
        elapsedMs = null;
        turnStop = null;
        plan = [];
        permissions.Clear();
        questions.Clear();
        permission = null;
        question = null;
    }

    private async Task ListConversations()
    {
        string? cwd;
        lock (gate) cwd = sessionCwd;
        if (cwd is null || harness is not { } agent) return;
        var all = await agent.ListAsync(cwd);
        lock (gate)
        {
            conversations = all.Take(MaxConversations).Select(c => new Conversation(c.Id, c.Title, c.Updated?.ToString("o"), c.Preview)).ToArray();
            if (conversations.FirstOrDefault(c => c.Id == sessionId)?.Title is { Length: > 0 } latest) title = latest;
        }
    }

    // Compares the repository with the review baseline off the UI thread; overlapping requests run once more at the end.
    private void RefreshChanges()
    {
        lock (gate) changesDirty = true;
        if (Interlocked.Exchange(ref changesRunning, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    RepositoryCheckpoint? from;
                    lock (gate) { if (!changesDirty) break; changesDirty = false; from = baseline; }
                    var list = from?.Compare() ?? [];
                    lock (gate) if (baseline == from) changes = list;
                    Changed();
                }
            }
            finally { Interlocked.Exchange(ref changesRunning, 0); }
        });
    }

    // Harness events (background threads).

    private void OnEvent(AgentEvent e)
    {
        var refreshChanges = false;
        string? allow = null;
        lock (gate)
        {
            switch (e)
            {
                case Exited exited:
                    status = "error";
                    error = $"{exited.Message} Details are in {LogPath}.";
                    queue.Clear();
                    permissions.Clear();
                    questions.Clear();
                    permission = null;
                    question = null;
                    break;
                case CatalogChanged:
                    if (sessionCwd is { } cwd && !catalogQueued)
                    {
                        catalogQueued = true;
                        Background(async () => { await Task.Delay(300); lock (gate) catalogQueued = false; await RefreshCatalog(cwd); });
                    }
                    break;
                case { SessionId: var sid } when sid != sessionId: return;
                case UserMessage user: Replay(user); break;
                case PromptAccepted accepted:
                    if (items.FirstOrDefault(i => i.Kind == "user" && i.MessageId is null) is { } sent)
                    {
                        sent.MessageId = accepted.MessageId;
                        if (checkpoints.Remove("item:" + sent.Id, out var before)) checkpoints[accepted.MessageId] = before;
                        foreach (var old in checkpoints.Keys.Where(k => !k.StartsWith("item:", StringComparison.Ordinal)).SkipLast(MaxCheckpoints).ToArray()) checkpoints.Remove(old);
                    }
                    break;
                case Rewound back: rewound = back with { Text = AgentPrompt.Parse(back.Text).Text.Trim() }; break;
                case TextDelta text: Append("assistant", text.Text); break;
                case ReasoningDelta thought: Append("thought", thought.Text); break;
                case ToolCall call:
                    var item = items.FindLast(i => i.ToolCallId == call.CallId);
                    if (item is null) Add(item = new Item { Id = NextId(), Kind = "tool", ToolCallId = call.CallId });
                    if (call.Title is { Length: > 0 }) item.Title = call.Title;
                    if (call.Kind is not null) item.ToolKind = call.Kind;
                    if (call.Status is not null) item.Status = call.Status;
                    if (call.Detail is not null) item.Detail = Trim(call.Detail, MaxDetail);
                    if (call.Parent is not null) item.Parent = call.Parent;
                    if (call.Files is { Count: > 0 }) item.Files = [.. call.Files.Distinct()];
                    refreshChanges = call.Status is "Completed" or "Failed" && baseline is not null;
                    if (call.Status is "Completed" or "Failed") SteerPending();
                    break;
                case PlanProposed proposed: Add(new Item { Id = NextId(), Kind = "proposal", Text = Trim(proposed.Text, MaxText) }); break;
                case PlanUpdate plan:
                    this.plan = plan.Entries.Take(100).Select(p => (p.Status, Trim(p.Content, MaxDetail))).ToArray();
                    break;
                case PermissionAsked request:
                    // A file inside the Loaded Repository reads relative to it on the card, whichever agent asks.
                    var shown = Relative(request.Title) is { } relative ? request with { Title = relative } : request;
                    if (shown.Kind is "Read" or "Search" or "Think" || mode == "all" || mode == "edits" && shown.Kind is "Edit" or "Delete" or "Move") allow = "AllowOnce";
                    else if (permission is null) permission = shown;
                    else permissions.Add(shown);
                    break;
                case QuestionAsked asked:
                    if (question is null) question = asked; else questions.Add(asked);
                    break;
                case RequestClosed closed:
                    permissions.RemoveAll(p => p.RequestId == closed.RequestId);
                    questions.RemoveAll(q => q.RequestId == closed.RequestId);
                    if (permission?.RequestId == closed.RequestId) { permission = permissions.FirstOrDefault(); if (permission is not null) permissions.RemoveAt(0); }
                    if (question?.RequestId == closed.RequestId) { question = questions.FirstOrDefault(); if (question is not null) questions.RemoveAt(0); }
                    break;
                case ContextUsage context: usage = new { used = context.Used, size = context.Size, cost = context.Cost }; break;
                case TurnUsage spent:
                    turn = spent.Tokens;
                    break;
                case Renamed renamed: title = renamed.Title; break;
                case Notice notice:
                    // Provider retries repeat; keep the latest.
                    if (items.Count > 0 && items[^1] is { Kind: "notice" } last && last.Text.StartsWith("Retrying", StringComparison.Ordinal) && notice.Text.StartsWith("Retrying", StringComparison.Ordinal))
                        last.Text = notice.Text;
                    else Add(new Item { Id = NextId(), Kind = "notice", Text = notice.Text });
                    break;
            }
        }
        if (allow is not null && harness is { } h) Background(() => h.ReplyPermissionAsync(e.SessionId, ((PermissionAsked)e).RequestId, allow));
        if (refreshChanges) RefreshChanges();
        Changed();
    }

    // State helpers; callers hold the gate.

    private void Add(Item item)
    {
        items.Add(item);
        if (items.Count > MaxItems) items.RemoveRange(0, items.Count - MaxItems);
    }

    private void Append(string kind, string text)
    {
        if (items.Count > 0 && items[^1].Kind == kind) items[^1].Text = Trim(items[^1].Text + text, MaxText);
        else if (text.Trim().Length > 0) Add(new Item { Id = NextId(), Kind = kind, Text = Trim(text, MaxText) });
    }

    // A replayed user message: rhDock's context and attached files become chips again.
    private void Replay(UserMessage user)
    {
        var (text, context, files) = AgentPrompt.Parse(user.Text);
        var item = new Item { Id = NextId(), Kind = "user", MessageId = user.MessageId, Text = Trim(text, MaxText),
            Chips = [.. context.Select(c => c == "selection" ? "Rhino selection" : "Script context"), .. user.Files.Concat(files).Select(Path.GetFileName).OfType<string>().Distinct()] };
        foreach (var image in user.Images) { var id = NextId(); Remember(id, image.MimeType, image.Data); item.Images = [.. item.Images, id]; }
        Add(item);
    }

    private void AddNotice(string text)
    {
        lock (gate) Add(new Item { Id = NextId(), Kind = "notice", Text = text });
        Changed();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private void Changed()
    {
        lock (gate) { if (refreshQueued) return; refreshQueued = true; }
        // InvokeOnUiThread may wait for Rhino. Never call it on a caller holding gate: the UI snapshot needs that lock.
        // Coalesce rapid deltas so the native page receives bounded updates rather than one refresh per token.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                lock (gate) refreshQueued = false;
                SingleScriptHost.Current.RefreshPanel();
            }));
        });
    }

    // Pasted images and attached PDFs travel as base64 lines of several MB; the log keeps the start of each line.
    private static void Log(string text)
    {
        if (text.Length > MaxLogLine) text = text[..MaxLogLine] + $"… [{text.Length} chars]";
        lock (LogGate) File.AppendAllText(LogPath, text + "\n");
    }

    /// <summary>Records an error the panel's page reported, so one log file shows both sides.</summary>
    public static void LogClient(string text) => Log("panel " + text);

    /// <summary>Records an exception that is about to end Rhino, which otherwise leaves no trace.</summary>
    public static void LogFatal(object error) => Log("fatal " + error);
}
