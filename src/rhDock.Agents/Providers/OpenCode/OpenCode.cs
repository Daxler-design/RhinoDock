using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// OpenCode 2 through its own local server, <c>opencode serve</c> (ADR 0008): one server per Rhino process on a loopback port
/// with a random password, the <c>/api</c> routes for calls, and <c>/api/event</c> for everything that happens.
/// Route and event names were read from OpenCode 2.0.19's <c>/openapi.json</c> and its event stream; <c>OpenCodeProtocol.cs</c> translates them.
/// </summary>
public sealed partial class OpenCode(string executable, Action<string> log, IReadOnlyDictionary<string, string>? environment = null) : IAgentHarness
{

    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TurnResult>> turns = new();
    private readonly ConcurrentDictionary<string, Live> live = new();
    private readonly ConcurrentDictionary<string, Tool> tools = new();
    private Process? process;
    private HttpClient? http;
    private AgentInfo? info;
    private string? streamRepository;
    private CancellationTokenSource? stream;
    private Model[] models = [];
    private Model? defaultModel;
    private ModelRef? standardModel;
    private Pending pending = new(null, null);
    private bool disposed;

    public event Action<AgentEvent>? Event;

    private sealed record Model(string Id, string Provider, string Name, double Context, bool Images, string[] Variants);
    private sealed record ModelRef(string Id, string Provider, string? Variant);
    private sealed record Pending(string? Agent, ModelRef? Model);
    private sealed class Tool { public string Name = ""; public JsonElement? Input; }

    // Per-conversation state for events and options.
    private sealed class Live
    {
        public TokenUsage Turn = new(0, 0, 0, 0, 0);
        public string? Agent, Last;
        public ModelRef? Model;
        public double? Cost;
    }

    public async Task<AgentInfo> StartAsync(string repository)
    {
        if (process is { HasExited: false } && http is not null && info is not null)
        {
            if (streamRepository != repository) await Listen(repository);
            return info;
        }
        Stop();
        var path = AgentProcess.ExtendedPath();
        var exe = executable;
        log($"PATH={path}");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var env = new Dictionary<string, string>(environment ?? new Dictionary<string, string>()) { ["OPENCODE_SERVER_PASSWORD"] = password };
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = AgentProcess.Launch(exe, ["serve", "--hostname", "127.0.0.1", "--port", FreePort().ToString()], repository, path, log, env);
        started.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            log("stdout " + (line.Contains("password", StringComparison.OrdinalIgnoreCase) ? "[password]" : line));
            var at = line.IndexOf("http://", StringComparison.Ordinal);
            if (line.Contains("listening", StringComparison.OrdinalIgnoreCase) && at >= 0) listening.TrySetResult(line[at..].Trim());
        };
        started.EnableRaisingEvents = true;
        started.Exited += (_, _) =>
        {
            listening.TrySetException(new InvalidOperationException("OpenCode stopped while starting."));
            if (process != started) return;
            Fail("OpenCode stopped.");
            Event?.Invoke(new Exited("OpenCode stopped."));
        };
        started.BeginOutputReadLine();
        process = started;
        var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(60));
        http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        // OpenCode 1.x serves its web app on unknown routes, so a 2.x route that does not answer JSON means the wrong version.
        string? version = null;
        using (var probe = await http.GetAsync("api/info"))
            if (probe.Content.Headers.ContentType?.MediaType == "application/json")
                using (var json = JsonDocument.Parse(await probe.Content.ReadAsStringAsync())) version = Str(json.RootElement, "version");
        if (version is null || !version.StartsWith("2.", StringComparison.Ordinal))
        {
            Stop();
            throw new InvalidOperationException($"rhDock needs OpenCode 2. The OpenCode at {exe} does not offer the version 2 server; update it and try again.");
        }
        await Listen(repository);
        info = new AgentInfo("OpenCode", version, exe);
        return info;
    }

    public async Task<IReadOnlyList<ConversationInfo>> ListAsync(string repository)
    {
        // ponytail: newest 50 root conversations; follow cursor.next if people keep more per repository.
        var result = await Call(HttpMethod.Get, $"api/session?limit=50&order=desc&parentID=null&directory={Uri.EscapeDataString(Real(repository))}&" + Location(repository));
        return Items(result).Select(s => new ConversationInfo(Str(s, "id")!, Str(s, "title"),
            s.TryGetProperty("time", out var t) && Num(t, "updated") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null)).ToArray();
    }

    public async Task<string> CreateAsync(string repository)
    {
        Pending next;
        lock (gate) next = pending;
        var body = new JsonObject { ["location"] = new JsonObject { ["directory"] = Real(repository) } };
        if (next.Agent is { } agent) body["agent"] = agent;
        if ((next.Model ?? standardModel) is { } model) body["model"] = Ref(model);
        var session = (await Call(HttpMethod.Post, "api/session?" + Location(repository), body)).GetProperty("data");
        var id = Str(session, "id")!;
        live[id] = new Live { Agent = next.Agent, Model = next.Model };
        return id;
    }

    // A fork is an ordinary session holding a copy of the messages; it is deleted when its side chat closes.
    public async Task<string?> ForkAsync(string repository, string sessionId)
    {
        var session = (await Call(HttpMethod.Post, $"api/session/{sessionId}/fork?" + Location(repository), new JsonObject())).GetProperty("data");
        var id = Str(session, "id")!;
        live[id] = new Live { Agent = Str(session, "agent"), Model = session.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? ModelOf(m) : null };
        return id;
    }

    public Task DiscardAsync(string repository, string sessionId) => Call(HttpMethod.Delete, $"api/session/{sessionId}?" + Location(repository));

    public async Task LoadAsync(string repository, string sessionId)
    {
        var state = live.GetOrAdd(sessionId, _ => new Live());
        var session = (await Call(HttpMethod.Get, $"api/session/{sessionId}?" + Location(repository))).GetProperty("data");
        state.Agent = Str(session, "agent");
        state.Model = session.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? ModelOf(m) : null;
        state.Cost = Num(session, "cost");
        // A rewound conversation keeps its later messages until the next prompt; they are not shown.
        var boundary = session.TryGetProperty("revert", out var revert) && revert.ValueKind == JsonValueKind.Object ? Str(revert, "messageID") : null;
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        TokenUsage? last = null;
        do
        {
            var page = await Call(HttpMethod.Get, $"api/session/{sessionId}/message?order=asc&limit=200{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}&" + Location(repository));
            var messages = Items(page).ToArray();
            var stop = false;
            foreach (var message in messages)
            {
                if (boundary is not null && Str(message, "id") == boundary)
                {
                    Emit(new Rewound(sessionId, boundary, Str(message, "text") ?? ""));
                    stop = true;
                    break;
                }
                last = Replay(sessionId, message) ?? last;
            }

            cursor = stop || messages.Length < 200 ? null : page.TryGetProperty("cursor", out var c) ? Str(c, "next") : null;
        }
        while (cursor is not null && seenCursors.Add(cursor));
        if (last is not null) Emit(new ContextUsage(sessionId, last.Total, ContextSize(state.Model), state.Cost));
    }

    public async Task<TurnResult> PromptAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        var state = live.GetOrAdd(sessionId, _ => new Live());
        state.Turn = new TokenUsage(0, 0, 0, 0, 0);
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        turns[sessionId] = done;
        try
        {
            if (await Send(repository, sessionId, prompt, null) is { } accepted) Emit(new PromptAccepted(sessionId, accepted));
            return await done.Task;
        }
        finally { turns.TryRemove(new KeyValuePair<string, TaskCompletionSource<TurnResult>>(sessionId, done)); }
    }

    public async Task SteerAsync(string repository, string sessionId, AgentPrompt prompt)
    {
        if (await Send(repository, sessionId, prompt with { Compact = false, Command = null }, "steer") is { } accepted) Emit(new PromptAccepted(sessionId, accepted));
    }

    // OpenCode snapshots files only in a Git repository with at least one commit; the answer then names the snapshot.
    public async Task<bool> RewindAsync(string repository, string sessionId, string messageId)
    {
        var result = await Call(HttpMethod.Post, $"api/session/{sessionId}/revert/stage?" + Location(repository), new JsonObject { ["messageID"] = messageId, ["files"] = true });
        return result.TryGetProperty("data", out var data) && Str(data, "snapshot") is not null;
    }

    public Task UndoRewindAsync(string repository, string sessionId) => Call(HttpMethod.Delete, $"api/session/{sessionId}/revert?" + Location(repository));

    // A "steer" message joins the running turn at its next step. The host sends other prompts only when no turn runs, with OpenCode's default delivery.
    // Returns the stored message id of a prompt (commands answer without one).
    private async Task<string?> Send(string repository, string sessionId, AgentPrompt prompt, string? delivery)
    {
        var files = new JsonArray();
        foreach (var file in prompt.Files) files.Add(new JsonObject { ["uri"] = new Uri(file).AbsoluteUri, ["name"] = Relative(repository, file) });
        foreach (var image in prompt.Images) files.Add(new JsonObject { ["uri"] = $"data:{image.MimeType};base64,{image.Data}", ["name"] = image.Name });
        var text = prompt.Tagged();
        JsonElement result;
        if (prompt.Compact) result = await Call(HttpMethod.Post, $"api/session/{sessionId}/compact?" + Location(repository), new JsonObject { ["delivery"] = delivery });
        else if (prompt.Command is { } command)
            result = await Call(HttpMethod.Post, $"api/session/{sessionId}/command?" + Location(repository), new JsonObject { ["name"] = command, ["text"] = text, ["files"] = files, ["delivery"] = delivery });
        else
        {
            var body = new JsonObject { ["text"] = text, ["files"] = files, ["delivery"] = delivery };
            if (prompt.Skill is { } skill) body["skills"] = new JsonArray(new JsonObject { ["id"] = skill });
            result = await Call(HttpMethod.Post, $"api/session/{sessionId}/prompt?" + Location(repository), body);
        }
        return !prompt.Compact && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) ? Str(data, "id") : null;
    }

    public Task CancelAsync(string sessionId) => Call(HttpMethod.Post, $"api/session/{sessionId}/interrupt");

    public Task ReplyPermissionAsync(string sessionId, string requestId, string? decision) =>
        Call(HttpMethod.Post, $"api/session/{Owner(sessionId, requestId)}/permission/{requestId}/reply", new JsonObject { ["decision"] = decision switch { "AllowOnce" => "once", "AllowAlways" => "always", _ => "reject" } });

    public Task ReplyQuestionAsync(string sessionId, string requestId, IReadOnlyDictionary<string, JsonNode?>? answer)
    {
        sessionId = Owner(sessionId, requestId);
        if (answer is null) return Call(HttpMethod.Delete, $"api/session/{sessionId}/form/{requestId}");
        var values = new JsonObject();
        foreach (var (key, value) in answer) values[key] = value?.DeepClone();
        return Call(HttpMethod.Post, $"api/session/{sessionId}/form/{requestId}/reply", new JsonObject { ["answer"] = values });
    }

    public async Task<AgentCatalog> CatalogAsync(string repository, string? sessionId)
    {
        var at = Location(repository);
        var modelList = Call(HttpMethod.Get, "api/model?" + at);
        var fallback = Call(HttpMethod.Get, "api/model/default?" + at);
        var config = Call(HttpMethod.Get, "api/config?" + at);
        var agents = Call(HttpMethod.Get, "api/agent?" + at);
        var commands = Call(HttpMethod.Get, "api/command?" + at);
        var skills = Call(HttpMethod.Get, "api/skill?" + at);
        var all = Items(await modelList).Where(m => m.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True).Select(m => new Model(
            Str(m, "id")!, Str(m, "providerID")!, Str(m, "name") ?? Str(m, "id")!,
            m.TryGetProperty("limit", out var limit) ? Num(limit, "context") ?? 0 : 0,
            m.TryGetProperty("capabilities", out var caps) && caps.TryGetProperty("input", out var input) && input.EnumerateArray().Any(i => i.GetString() == "image"),
            m.TryGetProperty("variants", out var variants) ? variants.EnumerateArray().Select(v => Str(v, "id")!).ToArray() : [])).ToArray();
        var standard = (await fallback).TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? ModelOf(d) : null;
        // OpenCode's own apps start with a configured model, else the one used last; the server's default ignores the latter.
        var configured = (await config).ValueKind == JsonValueKind.Array && (await config).EnumerateArray().Any(c => c.TryGetProperty("info", out var i) && Str(i, "model") is not null);
        var recent = Recent();
        if (!configured && recent.FirstOrDefault(r => all.Any(m => m.Id == r.Id && m.Provider == r.Provider)) is { } last) standard = last;
        all = [.. all.OrderBy(m => recent.FindIndex(r => r.Id == m.Id && r.Provider == m.Provider) is var i and >= 0 ? i : int.MaxValue)];
        lock (gate)
        {
            models = all;
            standardModel = standard;
            defaultModel = standard is null ? null : all.FirstOrDefault(m => m.Id == standard.Id && m.Provider == standard.Provider);
        }
        string? agent;
        ModelRef? current;
        if (sessionId is not null && live.TryGetValue(sessionId, out var state)) (agent, current) = (state.Agent, state.Model);
        else lock (gate) (agent, current) = (pending.Agent, pending.Model);
        current ??= standard;
        var model = current is null ? null : all.FirstOrDefault(m => m.Id == current.Id && m.Provider == current.Provider);
        var options = new List<ConfigOption>
        {
            new("model", "Model", "model", current is null ? null : $"{current.Provider}/{current.Id}",
                all.Select(m => new OptionChoice($"{m.Provider}/{m.Id}", m.Name, m.Provider, recent.Any(r => r.Id == m.Id && r.Provider == m.Provider))).ToArray()),
        };
        if (model is { Variants.Length: > 0 })
            options.Add(new("effort", "Effort", "thought_level", current?.Variant ?? "default", [new("default", "Default"), .. model.Variants.Select(OptionChoice.Effort)]));
        var primary = Items(await agents).Where(a => Str(a, "mode") is "primary" or "all" && !(a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True))
            .Select(a => new OptionChoice(Str(a, "id")!, Str(a, "name"))).ToArray();
        if (primary.Length > 1) options.Add(new("mode", "Mode", "mode", agent ?? primary[0].Value, primary));
        return new AgentCatalog(options,
            Items(await commands).Select(c => new CommandInfo(Str(c, "name")!, Str(c, "description"))).ToArray(),
            Items(await skills).Select(s => new SkillInfo(Str(s, "id")!, Str(s, "name") ?? Str(s, "id")!, Str(s, "description"))).ToArray(),
            model?.Images ?? true);
    }

    public async Task SetOptionAsync(string repository, string? sessionId, string optionId, string value)
    {
        Live? state = sessionId is null ? null : live.GetOrAdd(sessionId, _ => new Live());
        ModelRef? current;
        lock (gate) current = state is null ? pending.Model : state.Model;
        lock (gate) current ??= standardModel;
        if (optionId == "mode")
        {
            if (state is null) { lock (gate) pending = pending with { Agent = value }; return; }
            await Call(HttpMethod.Post, $"api/session/{sessionId}/agent?" + Location(repository), new JsonObject { ["agent"] = value });
            state.Agent = value;
            return;
        }
        ModelRef next;
        if (optionId == "model" && value.IndexOf('/') is > 0 and var slash) next = new ModelRef(value[(slash + 1)..], value[..slash], null);
        else if (optionId == "effort" && current is not null) next = current with { Variant = value == "default" ? null : value };
        else return;
        if (state is null) { lock (gate) pending = pending with { Model = next }; return; }
        await Call(HttpMethod.Post, $"api/session/{sessionId}/model?" + Location(repository), new JsonObject { ["model"] = Ref(next) });
        state.Model = next;
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
    }

    private void Stop()
    {
        Process? old;
        lock (gate) { old = process; process = null; stream?.Cancel(); stream = null; streamRepository = null; info = null; }
        http?.Dispose();
        http = null;
        if (old is not null) AgentProcess.Kill(old);
        Fail("OpenCode stopped.");
    }

    private void Fail(string message)
    {
        foreach (var turn in turns.Values) turn.TrySetResult(new TurnResult("error", message));
    }

    // The event stream for one repository; it reconnects after a drop while the server runs.
    private async Task Listen(string repository)
    {
        var cancel = new CancellationTokenSource();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { stream?.Cancel(); stream = cancel; streamRepository = repository; }
        var client = http!;
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested && !disposed)
            {
                try
                {
                    using var response = await client.GetAsync("api/event?" + Location(repository), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                    response.EnsureSuccessStatusCode();
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(cancel.Token) is { } line)
                    {
                        if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line.AsSpan(5).TrimStart());
                        else if (line.Length == 0 && data.Length > 0)
                        {
                            var text = data.ToString();
                            data.Clear();
                            try
                            {
                                using var json = JsonDocument.Parse(text);
                                if (Str(json.RootElement, "type") == "server.connected") connected.TrySetResult();
                                Handle(json.RootElement);
                            }
                            catch (Exception ex) { log($"event {ex.Message}: {text[..Math.Min(text.Length, 400)]}"); }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
                catch (Exception ex) { log("event stream " + ex.Message); }
                if (!cancel.IsCancellationRequested) await Task.Delay(1000);
            }
        });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task<JsonElement> Call(HttpMethod method, string path, JsonNode? body = null)
    {
        var client = http ?? throw new InvalidOperationException("OpenCode is not running.");
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var response = await client.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            log($"{method} {path} -> {(int)response.StatusCode} {text[..Math.Min(text.Length, 1000)]}");
            string? message = null;
            try { using var error = JsonDocument.Parse(text); message = Str(error.RootElement, "message") ?? (error.RootElement.TryGetProperty("data", out var d) ? Str(d, "message") : null); }
            catch (JsonException) { }
            throw new InvalidOperationException($"OpenCode answered {(int)response.StatusCode}{(message is null ? "" : ": " + message)}.");
        }
        if (text.Length == 0) return default;
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private double? ContextSize(ModelRef? model)
    {
        lock (gate)
        {
            var found = model is null ? defaultModel : models.FirstOrDefault(m => m.Id == model.Id && m.Provider == model.Provider);
            return found is { Context: > 0 } ? found.Context : null;
        }
    }

    // ponytail: reads the state file of OpenCode's own apps (2.0.19 layout); drop it if the server starts reporting the last-used model.
    private static List<ModelRef> Recent()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "opencode", "model.json")));
            var variants = json.RootElement.TryGetProperty("variant", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
            return json.RootElement.TryGetProperty("recent", out var recent) ? recent.EnumerateArray().Select(r =>
            {
                var (provider, id) = (Str(r, "providerID") ?? "", Str(r, "modelID") ?? "");
                var variant = variants.ValueKind == JsonValueKind.Object ? Str(variants, $"{provider}/{id}") : null;
                return new ModelRef(id, provider, variant is null or "default" ? null : variant);
            }).Where(r => r.Id.Length > 0 && r.Provider.Length > 0).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static string Location(string repository) => "location%5Bdirectory%5D=" + Uri.EscapeDataString(Real(repository));
    // OpenCode keeps no file snapshots for a directory reached through a symbolic link, so it gets the real path.
    private static string Real(string path) => AgentProcess.RealPath(path);
    private static IEnumerable<JsonElement> Items(JsonElement result) => result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray() : [];
    private static ModelRef ModelOf(JsonElement m) => new(Str(m, "id")!, Str(m, "providerID")!, Str(m, "variant"));
    private static JsonObject Ref(ModelRef m) { var o = new JsonObject { ["id"] = m.Id, ["providerID"] = m.Provider }; if (m.Variant is not null) o["variant"] = m.Variant; return o; }
    private static TokenUsage Tokens(JsonElement t) => new(Num(t, "input") ?? 0, Num(t, "output") ?? 0, Num(t, "reasoning") ?? 0,
        t.TryGetProperty("cache", out var c) ? Num(c, "read") ?? 0 : 0, t.TryGetProperty("cache", out var w) ? Num(w, "write") ?? 0 : 0);
    private static string Relative(string repository, string path) => Path.GetRelativePath(repository, path).Replace('\\', '/');

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
