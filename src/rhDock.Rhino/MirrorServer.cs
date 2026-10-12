using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Rhino;
using rhDock.Core;
using rhDock.Extensions;

namespace rhDock.Rhino;

/// <summary>
/// The rhDock Mirror: a loopback web server that shows the live rhDock panel and every open Extension window in a browser.
/// Browser clients act like the Rhino-hosted page: panel actions go to the panel's handler and Extension page messages to
/// the window's view, both on the Rhino UI thread. Every URL carries a random per-session token, so other web pages in the
/// browser cannot drive Rhino through it.
/// </summary>
internal static class MirrorServer
{
    private const int FirstPort = 5170;
    private static readonly ConcurrentDictionary<string, ExtensionWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    private static HttpListener? listener;
    private static string token = "";
    private static int port;
    private static volatile string? panelSnapshot;
    private static Action<string>? panelHandler;

    public static bool Running => listener is not null;
    public static string Url => $"http://localhost:{port}/{token}/";

    public static bool Start()
    {
        if (listener is not null) return true;
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        for (var candidate = FirstPort; candidate < FirstPort + 20; candidate++)
        {
            // On Windows only the localhost prefix may be registered without administrator rights; on macOS it listens on ::1.
            var next = new HttpListener();
            next.Prefixes.Add($"http://localhost:{candidate}/");
            try { next.Start(); }
            catch (HttpListenerException) { next.Close(); continue; }
            listener = next;
            port = candidate;
            _ = Task.Run(() => Accept(next));
            HostLog.Write($"mirror listening on port {port}");
            return true;
        }
        return false;
    }

    public static void Stop()
    {
        var current = listener;
        listener = null;
        if (current is null) return;
        try { current.Close(); } catch (ObjectDisposedException) { }
        foreach (var window in windows.Values) window.MirrorClients.Clear();
        HostLog.Write("mirror stopped");
    }

    // The panel publishes every snapshot here whether or not a browser is connected, so a new tab starts from the latest state.
    public static void PublishPanel(string snapshot) => panelSnapshot = snapshot;
    public static void AttachPanel(Action<string>? handler) => panelHandler = handler;

    public static void Register(ExtensionWindow window) => windows[Key(window)] = window;
    public static void Unregister(ExtensionWindow window)
    {
        if (windows.TryGetValue(Key(window), out var open) && ReferenceEquals(open, window)) windows.TryRemove(Key(window), out _);
        window.Broadcast("{\"type\":\"rhdock.closed\"}");
    }

    private static string Key(ExtensionWindow window) => Path.GetFileName(window.Folder) + "/" + window.Name;

    /// <summary>The browser URL of an Extension window's page, or null for a page that is not a file in its Extension.</summary>
    public static string? PageUrl(ExtensionWindow window) =>
        window.Page.Contains("://", StringComparison.Ordinal) ? null
        : $"/{token}/ext/{Uri.EscapeDataString(Path.GetFileName(window.Folder))}/{string.Join('/', window.Page.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString))}?rhdock-window={Uri.EscapeDataString(window.Name)}";

    private static async Task Accept(HttpListener server)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = await server.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(context));
        }
    }

    private static async Task Serve(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Rejects other host names (DNS rebinding) and cross-site posts; the token rejects everything else.
            var host = request.Headers["Host"];
            var origin = request.Headers["Origin"];
            var segments = request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (host != $"localhost:{port}" || segments.Length == 0 || segments[0] != token
                || origin is not null && origin != $"http://{host}")
            {
                Send(response, 403, "text/plain", "Forbidden");
                return;
            }
            var route = segments.Skip(1).Select(Uri.UnescapeDataString).ToArray();
            switch (route.FirstOrDefault())
            {
                case null: Send(response, 200, "text/html", IndexPage); return;
                case "windows": Send(response, 200, "application/json", WindowList()); return;
                case "panel": await ServePanel(request, response, route); return;
                case "ext": await ServeExtension(request, response, route); return;
                default: Send(response, 404, "text/plain", "Not found"); return;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            HostLog.Write("mirror request failed " + ex.Message);
            try { Send(response, 500, "text/plain", "Error"); } catch (Exception) { }
        }
    }

    private static async Task ServePanel(HttpListenerRequest request, HttpListenerResponse response, string[] route)
    {
        var path = string.Join('/', route.Skip(1));
        if (path is "" or "index.html")
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("rhDock.Frontend.index.html");
            using var reader = new StreamReader(stream!);
            Send(response, 200, "text/html", reader.ReadToEnd());
        }
        else if (path == "__rhdock/mirror/state")
        {
            var snapshot = panelSnapshot;
            Send(response, 200, "application/json", snapshot is null || panelHandler is null
                ? "{\"connected\":false}" : "{\"connected\":true,\"snapshot\":" + snapshot + "}");
        }
        else if (path == "__rhdock/mirror/action" && request.HttpMethod == "POST")
        {
            var json = await ReadBody(request);
            using (var document = JsonDocument.Parse(json))
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("action", out var action)
                    || action.ValueKind != JsonValueKind.String || action.GetString() == "ready")
                {
                    Send(response, 400, "text/plain", "Invalid action");
                    return;
                }
            RhinoApp.InvokeOnUiThread((Action)(() => panelHandler?.Invoke(json)));
            Send(response, 200, "application/json", "{\"ok\":true}");
        }
        else Send(response, 404, "text/plain", "Not found");
    }

    private static async Task ServeExtension(HttpListenerRequest request, HttpListenerResponse response, string[] route)
    {
        if (route.Length < 2) { Send(response, 404, "text/plain", "Not found"); return; }
        var path = string.Join('/', route.Skip(2));
        if (path == "__rhdock/window")
        {
            if (!windows.TryGetValue(route[1] + "/" + request.QueryString["window"], out var window))
            {
                Send(response, 404, "text/plain", "The window is closed");
                return;
            }
            if (request.HttpMethod == "POST")
            {
                var json = await ReadBody(request);
                RhinoApp.InvokeOnUiThread((Action)(() => window.Receive(json)));
                Send(response, 200, "application/json", "{\"ok\":true}");
            }
            else await Stream(response, window);
            return;
        }
        // Files come from the Extension folder of an open window; rhdock.js is always the host's, which speaks HTTP here.
        var folder = windows.Values.FirstOrDefault(w => Path.GetFileName(w.Folder).Equals(route[1], StringComparison.OrdinalIgnoreCase))?.Folder;
        if (folder is null) { Send(response, 404, "text/plain", "No open window of this Extension"); return; }
        if (Path.GetFileName(path) == "rhdock.js")
        {
            using var stream = typeof(ExtensionRepositoryTemplate).Assembly.GetManifestResourceStream("rhDock.Core.ExtensionTemplate.MyExtension.rhdock.js");
            using var reader = new StreamReader(stream!);
            Send(response, 200, "text/javascript", reader.ReadToEnd());
            return;
        }
        var file = Path.GetFullPath(Path.Combine(folder, path));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            Send(response, 404, "text/plain", "Not found");
            return;
        }
        response.StatusCode = 200;
        response.ContentType = ContentType(file);
        response.Headers["Cache-Control"] = "no-store";
        await using (var source = File.OpenRead(file))
        {
            response.ContentLength64 = source.Length;
            await source.CopyToAsync(response.OutputStream);
        }
        response.Close();
    }

    // Server-sent events: the latest message of each type first, then every new one until the browser leaves.
    private static async Task Stream(HttpListenerResponse response, ExtensionWindow window)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-store";
        response.SendChunked = true;
        var channel = Channel.CreateUnbounded<string>();
        var client = new object();
        window.MirrorClients[client] = channel.Writer;
        try
        {
            foreach (var message in window.Latest.Values) channel.Writer.TryWrite(message);
            var output = response.OutputStream;
            while (listener is not null)
            {
                using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string text;
                try { text = "data: " + await channel.Reader.ReadAsync(heartbeat.Token) + "\n\n"; }
                catch (OperationCanceledException) { text = ": keep-alive\n\n"; }
                catch (ChannelClosedException) { break; }
                var bytes = Encoding.UTF8.GetBytes(text);
                await output.WriteAsync(bytes);
                await output.FlushAsync();
            }
        }
        finally
        {
            window.MirrorClients.TryRemove(client, out _);
            try { response.Close(); } catch (Exception) { }
        }
    }

    private static string WindowList() => JsonSerializer.Serialize(windows.Values
        .Select(w => new { extension = Path.GetFileName(w.Folder), title = w.Name, url = PageUrl(w) })
        .OrderBy(w => w.extension).ThenBy(w => w.title).ToArray());

    private static async Task<string> ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > 12_000_000) throw new IOException("Message too large");
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static void Send(HttpListenerResponse response, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = type + "; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".wasm" => "application/wasm",
        ".wgsl" or ".txt" => "text/plain; charset=utf-8",
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    public static void OpenInBrowser() => Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });

    private const string IndexPage = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>rhDock Mirror</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; background: #16181d; color: #c9ccd3; font: 14px system-ui, sans-serif; }
  main { max-width: 640px; margin: 0 auto; padding: 32px 16px; }
  h1 { font-size: 18px; margin: 0 0 4px; } p { color: #8a8f99; margin: 0 0 24px; }
  h2 { font-size: 12px; text-transform: uppercase; letter-spacing: .05em; color: #8a8f99; margin: 24px 0 8px; }
  a { display: block; padding: 10px 12px; border: 1px solid #2c3039; border-radius: 6px; margin-bottom: 6px; color: inherit; text-decoration: none; }
  a:hover { background: #22252c; } small { color: #8a8f99; margin-left: 8px; } .none { color: #8a8f99; }
</style>
</head>
<body>
<main>
  <h1>rhDock Mirror</h1>
  <p>The live rhDock panel and Extension windows of this Rhino. Changes here act in Rhino.</p>
  <h2>Panel</h2>
  <a href="panel/?mirror=1" target="_blank">rhDock panel</a>
  <h2>Extension windows</h2>
  <div id="windows" class="none">No Extension window is open.</div>
</main>
<script>
const list = document.getElementById('windows');
async function refresh() {
  try {
    const response = await fetch('windows', { cache: 'no-store' });
    if (!response.ok) { list.textContent = `Rhino could not list the windows (${response.status}); see rhdock-agent.log.`; return; }
    const windows = await response.json();
    list.className = windows.length ? '' : 'none';
    list.replaceChildren(...(windows.length ? windows.map(w => {
      const a = document.createElement(w.url ? 'a' : 'div');
      if (w.url) { a.href = w.url; a.target = '_blank'; }
      a.textContent = w.title;
      const small = document.createElement('small');
      small.textContent = w.extension + (w.url ? '' : ' · web page, open it in Rhino');
      a.append(small);
      return a;
    }) : [document.createTextNode('No Extension window is open.')]));
  } catch { list.textContent = 'Rhino is not reachable. Run RhinoDockMirror again.'; }
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
