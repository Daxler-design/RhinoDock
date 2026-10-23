using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace rhDock.Agents;

public sealed record HostRequest(string Method, string? Name = null, JsonElement Arguments = default, string? Id = null);

/// <summary>A revocable, per-chat loopback capability. No credential enters the repository or model prompt.</summary>
public sealed class HostConnection : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly Dictionary<string, (string Fingerprint, Lazy<Task<string>> Result)> calls = new(StringComparer.Ordinal);
    private readonly Func<HostRequest, CancellationToken, Task<string>> dispatch;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private int disposed;
    private string OutcomesPath => FilePath + ".outcomes";
    private sealed record Outcome(string Fingerprint, string? Result = null, string? Error = null, bool Complete = false);
    private const int MaxRequestBytes = 4 * 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    public string FilePath { get; }
    public string Endpoint { get; }
    public bool Connected => Volatile.Read(ref disposed) == 0;
    public CancellationToken Cancellation => stopped.Token;

    public HostConnection(string directory, Func<HostRequest, CancellationToken, Task<string>> dispatch)
    {
        this.dispatch = dispatch;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Reserve a free port, then retry the listener bind if another process wins the gap.
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"http://localhost:{port}/";
            listener.Prefixes.Clear();
            listener.Prefixes.Add(Endpoint);
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }
        FilePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(FilePath, options)) JsonSerializer.Serialize(file, new { endpoint = Endpoint, token }, Json);
            _ = Listen();
        }
        catch { listener.Close(); throw; }
    }

    private async Task Listen()
    {
        while (Connected)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            _ = Respond(context);
        }
    }

    private async Task Respond(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (!Connected || request.HttpMethod != "POST" || request.RawUrl != "/"
                || request.Headers["Host"] != new Uri(Endpoint).Authority || request.Headers["Origin"] is not null
                || request.Headers["Authorization"] != "Bearer " + token)
            { context.Response.StatusCode = 403; return; }
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer, Cancellation).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxRequestBytes) { context.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, read);
            }
            var message = JsonSerializer.Deserialize<HostRequest>(body.ToArray(), Json) ?? throw new InvalidDataException("Missing request.");
            string json;
            if (message.Method == "call")
            {
                if (!Guid.TryParse(message.Id, out var requestId)) throw new InvalidDataException("A call requires a UUID request id. Never retry an uncertain mutation with a new id.");
                message = message with { Id = requestId.ToString("N") };
                // Persist deduplication for the connection lifetime; only concurrent calls stay in memory.
                var fingerprint = JsonSerializer.Serialize(message, Json);
                (string Fingerprint, Lazy<Task<string>> Result) call;
                lock (calls)
                {
                    Cancellation.ThrowIfCancellationRequested();
                    if (!calls.TryGetValue(message.Id!, out call))
                    {
                        if (calls.Count >= 64) throw new InvalidOperationException("Too many concurrent requests; retry with the same request id after a call completes.");
                        call = (fingerprint, new Lazy<Task<string>>(() => ExecuteRecorded(message, fingerprint)));
                        calls.Add(message.Id!, call);
                    }
                }
                if (call.Fingerprint != fingerprint) throw new InvalidDataException("Request id was already used with different arguments.");
                json = await call.Result.Value.ConfigureAwait(false);
            }
            else json = await dispatch(message, Cancellation).ConfigureAwait(false);
            Cancellation.ThrowIfCancellationRequested();
            context.Response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(json);
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                context.Response.StatusCode = ex is OperationCanceledException ? 410 : 400;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { error = ex is OperationCanceledException ? "rhDock connection was revoked." : ex.Message }, Json);
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception writeError) when (writeError is IOException or HttpListenerException or ObjectDisposedException) { }
        }
        finally { context.Response.Close(); }
    }

    private async Task<string> Execute(HostRequest request)
    {
        Cancellation.ThrowIfCancellationRequested();
        return await dispatch(request, Cancellation).ConfigureAwait(false);
    }

    private async Task<string> ExecuteRecorded(HostRequest request, string fingerprint)
    {
        var path = Path.Combine(OutcomesPath, request.Id! + ".json");
        try
        {
            Directory.CreateDirectory(OutcomesPath);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(OutcomesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<Outcome>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Json)
                    ?? throw new InvalidDataException("Unreadable request outcome; the request will not be repeated.");
                if (saved.Fingerprint != fingerprint) throw new InvalidDataException("Request id was already used with different arguments.");
                if (!saved.Complete) throw new InvalidOperationException("Request outcome is uncertain; inspect the document before taking further action. The request will not be repeated.");
                if (saved.Error is not null) throw new InvalidOperationException(saved.Error);
                return saved.Result!;
            }
            // Reserve the id before dispatch. An incomplete record must never cause a mutation to run twice.
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Outcome(fingerprint), Json)).ConfigureAwait(false);
            Outcome outcome;
            try { outcome = new(fingerprint, await Execute(request).ConfigureAwait(false), Complete: true); }
            catch (Exception ex) { outcome = new(fingerprint, Error: ex.Message, Complete: true); }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(outcome, Json)).ConfigureAwait(false);
            if (outcome.Error is not null) throw new InvalidOperationException(outcome.Error);
            return outcome.Result!;
        }
        finally { lock (calls) calls.Remove(request.Id!); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopped.Cancel();
        listener.Close();
        try { File.Delete(FilePath); }
        catch (IOException) { }
        Task[] pending;
        lock (calls) pending = calls.Values.ToArray().Select(c => (Task)c.Result.Value).ToArray();
        _ = Task.WhenAll(pending).ContinueWith(_ =>
        {
            try { if (Directory.Exists(OutcomesPath)) Directory.Delete(OutcomesPath, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
