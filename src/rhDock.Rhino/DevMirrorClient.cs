using System.Net.Http;
using System.Text;
using System.Text.Json;
using Rhino;

namespace rhDock.Rhino;

internal sealed class DevMirrorClient : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<string> onAction;
    private string? latestSnapshot;
    private bool disposed;
    private bool? connected;

    public DevMirrorClient(int port, Action<string> onAction)
    {
        this.onAction = onAction;
        http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        http.DefaultRequestHeaders.Add("X-RhDock-Mirror", "rhino");
        _ = Task.Run(() => Poll(cancellation.Token));
    }

    public void Publish(string snapshot) => Volatile.Write(ref latestSnapshot, snapshot);

    private async Task Poll(CancellationToken token)
    {
        string? sent = null;
        var refresh = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = Volatile.Read(ref latestSnapshot);
                if (snapshot is not null && (snapshot != sent || DateTime.UtcNow >= refresh))
                {
                    using var body = new StringContent(snapshot, Encoding.UTF8, "application/json");
                    using var posted = await http.PostAsync("__rhdock/mirror/snapshot", body, token);
                    posted.EnsureSuccessStatusCode();
                    sent = snapshot;
                    refresh = DateTime.UtcNow.AddSeconds(1);
                }
                using var response = await http.GetAsync("__rhdock/mirror/actions", token);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                foreach (var action in document.RootElement.EnumerateArray())
                {
                    var json = action.GetRawText();
                    RhinoApp.InvokeOnUiThread((Action)(() => { if (!disposed) onAction(json); }));
                }
                Report(true);
                await Task.Delay(200, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception)
            {
                if (token.IsCancellationRequested) break;
                Report(false);
                await Task.Delay(500);
            }
        }
    }

    // Writes one Rhino command-line line when the connection state changes, so a wrong port or stopped server is visible.
    private void Report(bool ok)
    {
        if (ok == connected) return;
        connected = ok;
        var line = ok ? $"rhDock development mirror connected to {http.BaseAddress}?mirror=1"
            : $"rhDock development mirror: no dev server at {http.BaseAddress}. Start `npm run dev` in frontend/ and check the port; retrying.";
        RhinoApp.InvokeOnUiThread((Action)(() => { if (!disposed) RhinoApp.WriteLine(line); }));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        http.Dispose();
    }
}
