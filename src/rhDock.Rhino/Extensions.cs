using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Eto.Forms;
using Rhino;
using rhDock.Core;
using rhDock.Rhino;

namespace rhDock.Extensions;

/// <summary>
/// The entry class of an Extension. The host creates it for each generation and calls <see cref="Activate"/>. An
/// Extension may use all of RhinoCommon, Eto, and .NET: static Rhino events, display conduits, timers, and threads
/// are fine when <see cref="IDisposable.Dispose"/> undoes them, so the retired generation can be unloaded. The host's
/// leak report names what was left behind.
/// </summary>
public interface IRhDockExtension
{
    void Activate(ExtensionContext rh);
}

/// <summary>What one Extension generation gets from the host. Everything registered here is dropped when the generation is retired.</summary>
public sealed class ExtensionContext
{
    private readonly ExtensionHost host;
    internal readonly List<ExtensionView> Views = [];
    // A per-generation serializer, so its type metadata cache dies with the generation.
    internal readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool retired;

    private readonly List<Process> processes = [];

    internal ExtensionContext(ExtensionHost host, string folder, string name)
    {
        this.host = host;
        Folder = folder;
        Name = name;
        Scripts = new(this);
    }

    public string Folder { get; }
    public string Name { get; }

    /// <summary>The panel's Script Session: list, select, set inputs, Run, read output copies, and register input editors.</summary>
    public ExtensionScripts Scripts { get; }

    /// <summary>
    /// Starts a process that rhDock ends, with its child processes, when this generation is retired or Rhino closes.
    /// Use it for renderers or servers written in other languages: a crash there cannot take Rhino down.
    /// </summary>
    public Process StartProcess(ProcessStartInfo info)
    {
        ObjectDisposedException.ThrowIf(retired, this);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start: " + info.FileName);
        processes.Add(process);
        HostLog.Write($"extension {Name} started process {process.Id} {info.FileName}");
        return process;
    }

    /// <summary>
    /// Opens a host-owned web window, or reuses the window this Extension opened with the same title in an earlier
    /// generation. <paramref name="page"/> is a file in the Extension folder or an http(s) URL such as a Vite dev server.
    /// </summary>
    public ExtensionView OpenView(string title, string page)
    {
        // A timer a retired generation left running must not open windows that no generation owns.
        ObjectDisposedException.ThrowIf(retired, this);
        var view = new ExtensionView(this, host.Window(this, title, page));
        Views.Add(view);
        return view;
    }

    /// <summary>Writes to the Rhino command line and the rhDock log.</summary>
    public void Log(string message)
    {
        RhinoApp.WriteLine($"rhDock extension {Name}: {message}");
        HostLog.Write($"extension {Name} log {message}");
    }

    internal void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"rhDock extension {Name}: {what} failed: {ex.Message}");
            HostLog.Write($"extension {Name} {what} failed: {ex}");
        }
    }

    internal void Retire()
    {
        retired = true;
        Scripts.Retire();
        foreach (var view in Views) view.Detach();
        Views.Clear();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); HostLog.Write($"extension {Name} ended process {process.Id}"); }
            }
            catch (Exception ex) { HostLog.Write($"extension {Name} could not end process: {ex.Message}"); }
            process.Dispose();
        }
        processes.Clear();
    }
}

/// <summary>One generation's handle on a host-owned web window. The page talks to it through <c>window.rhdock</c> (rhdock.js).</summary>
public sealed class ExtensionView
{
    private readonly ExtensionContext owner;
    private readonly Dictionary<string, Action<JsonElement>> handlers = new(StringComparer.Ordinal);
    internal ExtensionWindow? Window { get; private set; }

    internal ExtensionView(ExtensionContext owner, ExtensionWindow window)
    {
        this.owner = owner;
        Window = window;
    }

    public string Title => Window?.Title ?? "";

    /// <summary>Handles messages the page sends with <c>rhdock.send(type, payload)</c>.</summary>
    public void On(string type, Action<JsonElement> handler) => handlers[type] = handler;

    /// <summary>Sends a JSON value to the page's <c>rhdock.on(type, …)</c> handler. Only the latest value per type is queued.</summary>
    public void Post(string type, object? payload) => Window?.Post(type, JsonSerializer.Serialize(payload, owner.Json));

    internal void Dispatch(string type, JsonElement payload)
    {
        if (handlers.TryGetValue(type, out var handler)) owner.Guard($"message '{type}'", () => handler(payload));
    }

    internal void Detach()
    {
        handlers.Clear();
        Window = null;
    }
}

/// <summary>A floating web window that outlives Extension generations; each reload rebinds it to the new generation's view.</summary>
internal sealed class ExtensionWindow : Form
{
    private readonly WebView web = new();
    private readonly Dictionary<string, SnapshotSender> senders = new(StringComparer.Ordinal);
    private bool reportedError;

    public ExtensionWindow(string folder, string title, string page)
    {
        Folder = folder;
        Title = title;
        Name = title;
        ClientSize = new(720, 480);
        Resizable = true;
        Owner = global::Rhino.UI.RhinoEtoApp.MainWindow;
        Content = web;
        web.MessageReceived += (_, e) =>
        {
            var json = e.Message;
            RhinoApp.InvokeOnUiThread((Action)(() => Receive(json)));
        };
        web.DocumentLoading += (_, _) => senders.Clear();
        Closed += (_, _) => { IsClosed = true; MirrorServer.Unregister(this); Log("closed"); };
        Log($"opened, inspector {WebInspector.Enable(web)}");
        Navigate(page);
        MirrorServer.Register(this);
    }

    private void Log(string text) => HostLog.Write($"extension {Path.GetFileName(Folder)} window '{Title}' {text}");

    public string Folder { get; }
    // The title as a plain string: the rhDock Mirror reads it off the UI thread, where Eto controls must not be touched.
    public string Name { get; }
    public string Page { get; private set; } = "";
    public bool IsClosed { get; private set; }
    public ExtensionView? View { get; set; }

    // Browser copies of this window through the rhDock Mirror, and the latest message of each type for new copies.
    internal readonly ConcurrentDictionary<object, ChannelWriter<string>> MirrorClients = new();
    internal readonly ConcurrentDictionary<string, string> Latest = new(StringComparer.Ordinal);

    internal void Broadcast(string message)
    {
        foreach (var client in MirrorClients.Values) client.TryWrite(message);
    }

    private void Restart(string message)
    {
        Latest.Clear();
        Broadcast(message);
    }

    public void Navigate(string page)
    {
        Page = page;
        Log("page " + page);
        if (MirrorServer.PageUrl(this) is { } url) Restart("{\"type\":\"rhdock.navigate\",\"payload\":" + JsonSerializer.Serialize(url) + "}");
        web.Url = page.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || page.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(page) : new Uri(Path.GetFullPath(page, Folder));
    }

    public void ReloadPage()
    {
        Restart("{\"type\":\"rhdock.reload\"}");
        web.Reload();
    }

    public void Post(string type, string json)
    {
        if (IsClosed) return;
        var message = "{\"type\":" + JsonSerializer.Serialize(type) + ",\"payload\":" + json + "}";
        Latest[type] = message;
        Broadcast(message);
        if (!senders.TryGetValue(type, out var sender))
            senders[type] = sender = new(script => web.ExecuteScriptAsync(script), ReportError, action => Application.Instance.AsyncInvoke(action));
        _ = sender.Send($"window.rhdock && window.rhdock.receive({JsonSerializer.Serialize(type)}, {json});");
    }

    internal void Receive(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var value) ? value.Clone() : default;
            // Reserved types: page errors and the page environment go to the log, not to the Extension.
            if (type == "rhdock.error") Log("page error " + payload);
            else if (type == "rhdock.env") Log("page env " + payload.GetRawText());
            else View?.Dispatch(type, payload);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"rhDock extension window {Title}: ignored a malformed message ({ex.Message}).");
            Log("malformed message " + ex.Message);
        }
    }

    private void ReportError(Exception ex)
    {
        Log("delivery failed " + ex.Message);
        if (reportedError) return;
        reportedError = true;
        RhinoApp.WriteLine($"rhDock extension window {Title}: delivery failed: {ex.Message}");
    }
}

/// <summary>
/// Owns the Extension Repo, loads the Extensions the user enabled in it (for this session, or at startup), reloads them on source changes, and owns
/// everything they register. Each top-level folder of the repo that holds a <c>.csproj</c> is one Extension.
/// </summary>
internal sealed class ExtensionHost : IDisposable
{
    private const string RepositoryKey = "ExtensionRepository";
    // Unchanged key: before 0.6.5 every enabled Extension was saved here, so those now load at startup.
    private const string StartupKey = "Extensions";
    private static ExtensionHost? current;
    public static ExtensionHost Current => current ??= new();
    public static void Shutdown() { current?.Dispose(); current = null; }

    private readonly ScriptCompilerService compiler = new(typeof(IPortBag).Assembly.Location, typeof(RhinoDoc).Assembly.Location);
    private readonly ProjectWatchService watcher;
    private readonly List<Entry> entries = [];
    // Extension folders across repos, so switching repos keeps each repo's choices. Enabled lasts the session;
    // startup is saved and seeds enabled when Rhino starts.
    private readonly HashSet<string> enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> startup = new(StringComparer.OrdinalIgnoreCase);
    private string? error;

    private bool scriptsPending;

    private ExtensionHost()
    {
        watcher = new(compiler);
        SingleScriptHost.Current.StateChanged += OnScriptsChanged;
        SingleScriptHost.Current.InputReordered += OnInputReordered;
    }

    // Session changes reach Extensions once per UI turn, outside the call that changed the Session, so a handler that
    // sets an input cannot re-enter itself.
    private void OnScriptsChanged()
    {
        if (scriptsPending || !entries.Any(e => e.Context?.Scripts.Wanted == true)) return;
        scriptsPending = true;
        Application.Instance.AsyncInvoke(() =>
        {
            scriptsPending = false;
            foreach (var entry in entries.ToArray()) entry.Context?.Scripts.Raise();
        });
    }

    private void OnInputReordered(ScriptInputReorder move)
    {
        foreach (var entry in entries.ToArray()) entry.Context?.Scripts.RaiseReordered(move);
    }

    internal static ExtensionScripts? Editor(string typeName, bool list = false) =>
        current?.entries.FirstOrDefault(e => e.Context?.Scripts.Edits(typeName, list) == true)?.Context?.Scripts;

    /// <summary>The enabled Extension that edits inputs of this type, or null.</summary>
    public static string? EditorFor(string typeName, bool list = false) =>
        current?.entries.FirstOrDefault(e => e.Context?.Scripts.Edits(typeName, list) == true)?.Name;

    /// <summary>Hands an input of the active Script to the Extension that edits its type.</summary>
    public void Edit(string input, int? index) => Try(() =>
    {
        var port = SingleScriptHost.Current.Session.Selected?.Ports.FirstOrDefault(p => p.Name == input && !p.IsOutput);
        var type = port is null ? null : PortTypeName.Element(port.TypeName);
        if (type is not null && entries.FirstOrDefault(e => e.Context?.Scripts.Edits(type, port!.Access == PortAccess.List) == true)?.Context is { } context) context.Scripts.Edit(type, input, index);
    });

    public string? Repository { get; private set; }

    /// <summary>Opens the last Extension Repo and enables its Extensions marked Load at startup.</summary>
    public void LoadAtStartup()
    {
        try { startup.UnionWith(JsonSerializer.Deserialize<string[]>(Setting(StartupKey, "[]")) ?? []); }
        catch (JsonException) { }
        enabled.UnionWith(startup);
        var repository = Setting(RepositoryKey, "");
        if (repository.Length > 0 && Directory.Exists(repository)) Try(() => Open(repository));
    }

    public void OpenRepository(string folder) => Try(() => Open(folder));

    public void NewRepository(string folder) => Try(() =>
    {
        ExtensionRepositoryTemplate.CreateRepository(folder);
        Open(folder);
        Enable(ExtensionRepositoryTemplate.StarterName, true);
    });

    public void SetEnabled(string name, bool on) => Try(() => Enable(name, on));

    public void SetStartup(string name, bool on) => Try(() =>
    {
        var folder = Path.Combine(Repository ?? throw new InvalidOperationException("Open an Extension Repo first."), name);
        if (on ? !startup.Add(folder) : !startup.Remove(folder)) return;
        RhinoDockPlugin.Instance?.Settings.SetString(StartupKey, JsonSerializer.Serialize(startup.Order().ToArray()));
        HostLog.Write($"extension {name} load at startup {(on ? "on" : "off")}");
        RefreshPanel();
    });

    public void Reload(string name) => Try(() => { if (Find(name) is { } entry) Reload(entry, reloadPages: true); });

    public void Reveal() => Try(() =>
    {
        if (Repository is null) return;
        Process.Start(new ProcessStartInfo(Repository) { UseShellExecute = true });
    });

    /// <summary>The Extensions tab state: the repo, its Extensions, and the last action error.</summary>
    public object Snapshot() => new
    {
        repository = Repository,
        error,
        items = Folders().Select(folder =>
        {
            var entry = entries.FirstOrDefault(e => e.Slot.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            return new { name = Path.GetFileName(folder), enabled = entry is not null, startup = startup.Contains(folder), generation = entry?.Slot.Generation ?? 0,
                error = entry?.Slot.Error, leaked = entry?.Leaked ?? 0 };
        }).ToArray(),
    };

    private void Open(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Extension Repo not found: " + folder);
        foreach (var entry in entries.ToArray()) Unload(entry);
        Repository = folder;
        RhinoDockPlugin.Instance?.Settings.SetString(RepositoryKey, folder);
        HostLog.Write("extension repo " + folder);
        WriteBuildProps(folder);
        foreach (var extension in Folders().Where(enabled.Contains)) Load(extension);
    }

    private IEnumerable<string> Folders() => Repository is null || !Directory.Exists(Repository) ? []
        : Directory.GetDirectories(Repository)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && Directory.EnumerateFiles(d, "*.csproj").Any())
            .Order(StringComparer.OrdinalIgnoreCase);

    private void Enable(string name, bool on)
    {
        if (Repository is null) throw new InvalidOperationException("Open an Extension Repo first.");
        var folder = Path.Combine(Repository, name);
        if (on ? !enabled.Add(folder) : !enabled.Remove(folder)) return;
        HostLog.Write($"extension {name} {(on ? "enabled" : "disabled")}");
        if (on) Load(folder);
        else if (Find(name) is { } entry) Unload(entry);
    }

    private Entry? Find(string name) => entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void Load(string folder)
    {
        var entry = new Entry(this, new ExtensionSlot(folder, compiler));
        entries.Add(entry);
        watcher.Register(folder, entry);
        Reload(entry);
    }

    private void Unload(Entry entry)
    {
        watcher.Unregister(entry.Slot.Folder);
        entries.Remove(entry);
        entry.Slot.Unload();
        entry.Context = null;
        CloseWindows(entry, _ => true);
        CheckReleased(entry);
    }

    private void Reload(Entry entry, bool reloadPages = false)
    {
        ExtensionContext? next = null;
        var ok = entry.Slot.Reload(assembly => Activate(entry, assembly, out next));
        RefreshPanel();
        if (!ok)
        {
            // Windows a failed generation opened belong to nobody; windows of the running generation stay bound.
            CloseWindows(entry, w => w.View is null);
            RhinoApp.WriteLine($"rhDock extension {entry.Name}: reload failed; generation {entry.Slot.Generation} keeps running.{Environment.NewLine}{entry.Slot.Error}");
            return;
        }
        entry.Context = next;
        foreach (var window in entry.Windows.Values)
            window.View = next!.Views.LastOrDefault(v => ReferenceEquals(v.Window, window));
        CloseWindows(entry, w => w.View is null);
        if (reloadPages) foreach (var window in entry.Windows.Values) window.ReloadPage();
        RhinoApp.WriteLine($"rhDock extension {entry.Name}: generation {entry.Slot.Generation} loaded.");
        CheckReleased(entry);
    }

    private Action Activate(Entry entry, Assembly assembly, out ExtensionContext? context)
    {
        var types = assembly.GetTypes().Where(t => typeof(IRhDockExtension).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToArray();
        if (types.Length != 1)
            throw new InvalidOperationException($"An Extension needs exactly one class implementing {nameof(IRhDockExtension)}; found {types.Length}.");
        var created = new ExtensionContext(this, entry.Slot.Folder, entry.Name);
        IRhDockExtension? instance = null;
        try
        {
            instance = (IRhDockExtension)Activator.CreateInstance(types[0])!;
            instance.Activate(created);
        }
        catch
        {
            Retire(created, instance);
            throw;
        }
        context = created;
        return () => Retire(created, instance);
    }

    private static void Retire(ExtensionContext context, IRhDockExtension? instance)
    {
        if (instance is IDisposable disposable) context.Guard("Dispose", disposable.Dispose);
        context.Retire();
    }

    internal ExtensionWindow Window(ExtensionContext context, string title, string page)
    {
        var entry = entries.First(e => e.Slot.Folder == context.Folder);
        if (entry.Windows.TryGetValue(title, out var window) && !window.IsClosed)
        {
            HostLog.Write($"extension {entry.Name} window '{title}' reused");
            if (window.Page != page) window.Navigate(page);
            return window;
        }
        window = new ExtensionWindow(context.Folder, title, page);
        window.Closed += (_, _) => { if (entry.Windows.TryGetValue(title, out var open) && ReferenceEquals(open, window)) entry.Windows.Remove(title); };
        entry.Windows[title] = window;
        window.Show();
        return window;
    }

    private static void CloseWindows(Entry entry, Func<ExtensionWindow, bool> predicate)
    {
        foreach (var (title, window) in entry.Windows.ToArray())
            if (predicate(window))
            {
                entry.Windows.Remove(title);
                if (!window.IsClosed) window.Close();
            }
    }

    // A leaked generation is reported, not fixed: the host cannot release references an Extension keeps elsewhere.
    private static void CheckReleased(Entry entry) => Task.Delay(2000).ContinueWith(_ => RhinoApp.InvokeOnUiThread((Action)(() =>
    {
        entry.Leaked = entry.Slot.Leaked();
        RefreshPanel();
        if (entry.Leaked > 0)
            RhinoApp.WriteLine($"rhDock extension {entry.Name}: {entry.Leaked} retired generation(s) are still loaded. Look for static events, conduits, timers, threads, or caches that Dispose did not undo.");
    })));

    // For IDE completion only: rhDock compiles Extensions against the loaded plug-in, not through this file.
    private static void WriteBuildProps(string repository)
    {
        var path = Path.Combine(repository, "Directory.Build.props");
        var text = $"""
            <!-- Written by rhDock when it opens this Extension Repo, for IDE completion. Not version-controlled. -->
            <Project>
              <PropertyGroup>
                <RhDockPluginDir>{Path.GetDirectoryName(typeof(ExtensionHost).Assembly.Location)}</RhDockPluginDir>
              </PropertyGroup>
            </Project>

            """;
        try { if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text); }
        catch (IOException ex) { HostLog.Write("extension repo props not written: " + ex.Message); }
    }

    private void Try(Action action)
    {
        error = null;
        try { action(); }
        catch (Exception ex) { error = ex.Message; HostLog.Write("extension action failed: " + ex.Message); }
        RefreshPanel();
    }

    // After Shutdown a late leak check must not recreate the panel host.
    private static void RefreshPanel() { if (current is not null) SingleScriptHost.Current.RefreshPanel(); }

    private static string Setting(string key, string fallback) => RhinoDockPlugin.Instance?.Settings.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        SingleScriptHost.Current.StateChanged -= OnScriptsChanged;
        SingleScriptHost.Current.InputReordered -= OnInputReordered;
        foreach (var entry in entries)
        {
            watcher.Unregister(entry.Slot.Folder);
            entry.Slot.Unload();
            CloseWindows(entry, _ => true);
        }
        entries.Clear();
        watcher.Dispose();
        compiler.Dispose();
    }

    internal sealed class Entry(ExtensionHost host, ExtensionSlot slot) : IReloadable
    {
        public ExtensionSlot Slot { get; } = slot;
        public string Name => Slot.Name;
        public ExtensionContext? Context { get; set; }
        public int Leaked { get; set; }
        public Dictionary<string, ExtensionWindow> Windows { get; } = new(StringComparer.Ordinal);

        public void OnSourceChanged() => RhinoApp.InvokeOnUiThread((Action)(() => { if (host.entries.Contains(this)) host.Reload(this); }));
        public void OnEntryRenamed(string oldRel, string newRel) { }
    }
}
