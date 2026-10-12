using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// A child agent that speaks one JSON object per line on stdin and stdout (MonoCode's core/child.ts). Claude Code and Pi use it
/// directly; <see cref="JsonRpc"/> adds JSON-RPC on top for Codex and ACP agents. Disposing it closes stdin, then kills the process tree after a grace period.
/// </summary>
internal sealed class JsonLines : IDisposable
{
    private readonly Process process;
    private readonly Action<string> log;
    private readonly SemaphoreSlim writing = new(1, 1);
    private volatile bool disposed;

    /// <summary>Raised on a background thread for each line, in order, until disposed. A handler must not block on a later line.</summary>
    public event Action<JsonElement>? Received;
    /// <summary>Raised once when stdout closes, which is when the process has ended or is ending.</summary>
    public event Action? Exited;

    public JsonLines(Process process, Action<string> log)
    {
        this.process = process;
        this.log = log;
        _ = Task.Run(Read);
    }

    public bool Running => !process.HasExited;

    public async Task Send(JsonNode message)
    {
        var line = message.ToJsonString();
        log("-> " + line);
        await writing.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
        }
        // A process that ended closes its stdin; callers handle a stopped agent as InvalidOperationException.
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException) { throw new InvalidOperationException("The agent stopped. Details are in the agent log.", ex); }
        finally { writing.Release(); }
    }

    // StreamReader splits only on CR and LF, so U+2028 inside a JSON string is safe (Pi's RPC framing rule).
    private async Task Read()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                log("<- " + line);
                if (disposed || !line.TrimStart().StartsWith('{')) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    Received?.Invoke(json.RootElement.Clone());
                }
                catch (JsonException) { }
                catch (Exception ex) { log("handler " + ex); }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException) { }
        log("exit");
        Exited?.Invoke();
    }

    // Closing stdin ends an agent as its own client would; the tree is killed only if it outlives the grace period.
    // Killing at once (SIGSTOP, a scan of every process, SIGKILL) preceded each reported Rhino exit on a Claude Code restart.
    public void Dispose()
    {
        disposed = true;
        try { process.StandardInput.Close(); }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or InvalidOperationException) { }
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => AgentProcess.Kill(process), TaskScheduler.Default);
    }
}

/// <summary>JSON-RPC 2.0 over <see cref="JsonLines"/> (MonoCode's core/jsonRpc.ts). Codex leaves out the "jsonrpc" member; ACP agents expect it.</summary>
internal sealed class JsonRpc
{
    private readonly JsonLines lines;
    private readonly bool tagged;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private long nextId;

    /// <summary>A request from the agent; the result is sent back, and an exception becomes a JSON-RPC error.</summary>
    public Func<string, JsonElement, Task<JsonNode?>>? OnRequest;
    public Action<string, JsonElement>? OnNotification;

    public JsonRpc(JsonLines lines, bool tagged)
    {
        this.lines = lines;
        this.tagged = tagged;
        lines.Received += Route;
        lines.Exited += () => { foreach (var call in pending.Values) call.TrySetException(new InvalidOperationException("The agent stopped.")); };
    }

    public async Task<JsonElement> Request(string method, JsonNode? parameters, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref nextId);
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = done;
        try
        {
            await lines.Send(Message(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }));
            return await done.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
        }
        finally { pending.TryRemove(id, out _); }
    }

    public Task Notify(string method, JsonNode? parameters = null) =>
        lines.Send(Message(parameters is null ? new JsonObject { ["method"] = method } : new JsonObject { ["method"] = method, ["params"] = parameters }));

    private JsonObject Message(JsonObject message)
    {
        if (tagged) message["jsonrpc"] = "2.0";
        return message;
    }

    private void Route(JsonElement message)
    {
        var method = Str(message, "method");
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        if (method is not null && hasId) _ = Answer(JsonNode.Parse(id.GetRawText()), method, parameters);
        else if (method is not null) OnNotification?.Invoke(method, parameters);
        else if (hasId && id.ValueKind == JsonValueKind.Number && pending.TryGetValue(id.GetInt64(), out var call))
        {
            if (message.TryGetProperty("error", out var error)) call.TrySetException(new InvalidOperationException(Str(error, "message") ?? error.GetRawText()));
            else call.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
        }
    }

    private async Task Answer(JsonNode? id, string method, JsonElement parameters)
    {
        JsonObject reply;
        try
        {
            if (OnRequest is null) throw new NotSupportedException($"rhDock does not handle {method}.");
            reply = new JsonObject { ["id"] = id, ["result"] = await OnRequest(method, parameters) ?? new JsonObject() };
        }
        catch (Exception ex) { reply = new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = ex.Message } }; }
        try { await lines.Send(Message(reply)); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.IO.IOException) { }
    }
}
