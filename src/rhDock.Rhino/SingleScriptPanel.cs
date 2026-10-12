using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Geometry;
using rhDock.Agents;
using rhDock.Core;

namespace rhDock.Rhino;

[Guid("09B56F0C-2E1D-4D49-ACD4-32B741C0978E")]
public sealed class SingleScriptPanel : Panel, global::Rhino.UI.IPanel
{
    private const uint EventSystemForeground = 3;
    private readonly SingleScriptHost host;
    private readonly WebView web;
    private readonly SnapshotSender snapshots;
    private DevMirrorClient? mirror;
    private bool loaded;
    private string? message;
    private bool messageIsError;
    private IntPtr foregroundHook;
    private WinEventCallback? foregroundCallback;
    private bool rhinoWasForeground;
    private bool reportedSurfaceRefresh;
    private bool reportedSurfaceError;
    private bool reportedBridgeError;

    public SingleScriptPanel()
    {
        RhinoApp.WriteLine($"rhDock [{Environment.ProcessId}]: opening panel.");
        web = new();
        HostLog.Write($"panel opened, inspector {WebInspector.Enable(web)}");
        host = SingleScriptHost.Current;
        snapshots = new(script => web.ExecuteScriptAsync(script), ex =>
        {
            lastSnapshot = null;
            if (IsDisposed || reportedBridgeError) return;
            reportedBridgeError = true;
            RhinoApp.WriteLine($"rhDock [{Environment.ProcessId}]: WebView snapshot delivery failed: {ex.Message}");
        }, action => Application.Instance.AsyncInvoke(action));
        Content = web;
        web.MessageReceived += OnMessage;
        web.DocumentLoading += (_, _) => { loaded = false; lastSnapshot = null; snapshots.ClearPending(); };
        host.Attach(this);
        MirrorServer.AttachPanel(Handle);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("rhDock.Frontend.index.html");
        if (stream is null) { Content = new Label { Text = "rhDock frontend is missing. Build frontend assets and rebuild the plugin." }; return; }
        using var reader = new StreamReader(stream);
        web.LoadHtml(reader.ReadToEnd());
    }

    public void RefreshSession() { message = null; messageIsError = false; Publish(); }
    public void RefreshResult() => Publish();
    public void ShowError(string value) { message = value; messageIsError = true; Publish(); }
    public void ShowMessage(string value) { message = value; messageIsError = false; Publish(); }
    public bool MirrorActive => mirror is not null;
    public void StartMirror(int port)
    {
        StopMirror();
        mirror = new DevMirrorClient(port, Handle);
        Publish(true);
    }
    public void StopMirror() { mirror?.Dispose(); mirror = null; }

    public void PanelShown(uint documentSerialNumber, global::Rhino.UI.ShowPanelReason reason)
    {
        host.Attach(this);
        WatchForeground();
    }
    public void PanelHidden(uint documentSerialNumber, global::Rhino.UI.ShowPanelReason reason)
    {
        StopWatchingForeground();
        snapshots.ClearPending();
        StopMirror();
        host.Detach(this);
    }
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
    {
        StopWatchingForeground();
        snapshots.ClearPending();
        StopMirror();
        host.Detach(this);
    }

    private void WatchForeground()
    {
        if (!OperatingSystem.IsWindows() || foregroundHook != IntPtr.Zero) return;
        rhinoWasForeground = IsRhinoWindow(GetForegroundWindow());
        foregroundCallback = OnForegroundChanged;
        foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, foregroundCallback, 0, 0, 0);
        if (foregroundHook == IntPtr.Zero) foregroundCallback = null;
    }

    private void StopWatchingForeground()
    {
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        foregroundHook = IntPtr.Zero;
        foregroundCallback = null;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time)
    {
        try
        {
            var rhinoIsForeground = IsRhinoWindow(window);
            if (rhinoIsForeground && !rhinoWasForeground)
                Application.Instance.AsyncInvoke(RefreshWebViewSurface);
            rhinoWasForeground = rhinoIsForeground;
        }
        catch (Exception ex) { ReportSurfaceError(ex); }
    }

    private static bool IsRhinoWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void RefreshWebViewSurface()
    {
        try
        {
            if (foregroundHook == IntPtr.Zero || !loaded || !web.Visible) return;
            var size = web.Size;
            if (size.Width < 2 || size.Height < 2) return;
            // A real size change restores the Windows WebView surface after floating-panel activation.
            try
            {
                web.Size = new Size(size.Width - 1, size.Height);
                web.UpdateLayout();
            }
            finally
            {
                web.Size = new Size(-1, -1);
                web.UpdateLayout();
            }
            if (reportedSurfaceRefresh) return;
            RhinoApp.WriteLine("rhDock: refreshed the WebView surface after returning to Rhino.");
            reportedSurfaceRefresh = true;
        }
        catch (Exception ex) { ReportSurfaceError(ex); }
    }

    private void ReportSurfaceError(Exception ex)
    {
        if (reportedSurfaceError) return;
        reportedSurfaceError = true;
        RhinoApp.WriteLine("rhDock: WebView surface refresh failed: " + ex.Message);
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private void OnMessage(object? sender, WebViewMessageEventArgs e)
    {
        var json = e.Message;
        RhinoApp.InvokeOnUiThread((Action)(() => Handle(json)));
    }

    private void Handle(string json)
    {
        var refresh = true;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString();
            if (action == "ready")
            {
                refresh = false;
                if (!loaded) RhinoApp.WriteLine($"rhDock [{Environment.ProcessId}]: panel bridge ready.");
                loaded = true; Publish(true); return;
            }
            // Opening a file closes the panel's document and detaches it, but Windows Rhino keeps the panel on screen without
            // calling PanelShown again; without this, Open repository ran with no panel to show the result.
            if (!host.IsAttached(this)) { host.Attach(this); WatchForeground(); }
            if (root.TryGetProperty("revision", out var rev) && rev.GetInt32() != host.Revision) return;
            // Ignore late draft/approval actions from a conversation the user has just switched away from.
            if (action?.StartsWith("agent", StringComparison.Ordinal) == true && action != "agentImage" && root.TryGetProperty("viewId", out var view)
                && view.ValueKind == JsonValueKind.String && view.GetString() != AgentHost.Current.ViewId) return;
            message = null;
            messageIsError = false;
            string Name() => root.GetProperty("name").GetString() ?? "";
            switch (action)
            {
                case "open": ChooseRepository(false); break;
                case "new": ChooseRepository(true, root.TryGetProperty("value", out var name) ? name.GetString() : null); break;
                case "export":
                    using (var dialog = new SaveFileDialog { FileName = "rhDock-workspace.json" })
                        if (dialog.ShowDialog(this) == DialogResult.Ok) host.ExportWorkspace(dialog.FileName);
                    break;
                case "agentSelect": if (host.Session.Repository is { } switchIn) AgentHost.Current.SelectAgent(switchIn, root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "agentConnect": if (host.Session.Repository is { } connectTo) AgentHost.Current.Connect(connectTo); refresh = false; break;
                case "agentPrompt":
                    if (host.Session.Repository is { } promptIn)
                    {
                        var (blocks, linked, chips) = AgentContext(promptIn, root.TryGetProperty("context", out var context) && context.GetBoolean(),
                            root.TryGetProperty("mentions", out var mentions) ? mentions : default);
                        AgentHost.Current.Prompt(promptIn, root.GetProperty("value").GetString() ?? "", blocks, linked, chips,
                            root.TryGetProperty("steer", out var steer) && steer.ValueKind == JsonValueKind.True);
                    }
                    refresh = false; break;
                case "agentMode": AgentHost.Current.SetMode(root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "agentFiles": if (host.Session.Repository is { } filesIn) AgentHost.Current.WantFiles(filesIn, root.GetProperty("value").GetBoolean()); refresh = false; break;
                case "agentAttach":
                    AgentHost.Current.Attach(Name(), root.GetProperty("mimeType").GetString(), root.GetProperty("value").GetString(), null,
                        root.TryGetProperty("thumb", out var thumb) ? thumb.GetString() : null);
                    refresh = false; break;
                case "agentAttachFile":
                    using (var dialog = new OpenFileDialog { Title = "Attach a file", MultiSelect = true })
                        if (dialog.ShowDialog(this) == DialogResult.Ok) foreach (var file in dialog.Filenames) AgentHost.Current.AttachFile(file);
                    refresh = false; break;
                case "agentCapture": CaptureViewport(); refresh = false; break;
                case "agentDetach": AgentHost.Current.Detach(root.GetProperty("index").GetInt32()); refresh = false; break;
                case "agentUnqueue": AgentHost.Current.Unqueue(root.GetProperty("index").GetInt32()); refresh = false; break;
                case "agentSteer": AgentHost.Current.SteerQueued(root.GetProperty("index").GetInt32()); refresh = false; break;
                case "agentKeep": if (host.Session.Repository is { } keepIn) AgentHost.Current.Keep(keepIn); refresh = false; break;
                case "agentRevert": AgentHost.Current.Revert(root.GetProperty("path").GetString() ?? ""); refresh = false; break;
                case "agentRestore": AgentHost.Current.Restore(root.GetProperty("index").GetInt32()); refresh = false; break;
                case "agentUndoRewind": AgentHost.Current.UndoRewind(); refresh = false; break;
                case "agentOpenUrl":
                    if (Uri.TryCreate(root.GetProperty("value").GetString(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                    refresh = false; break;
                case "agentImage":
                    var imageId = root.GetProperty("value").GetInt32();
                    if (AgentHost.Current.Picture(imageId) is { } picture)
                        _ = web.ExecuteScriptAsync($"window.rhDockImage && window.rhDockImage({imageId}, {JsonSerializer.Serialize(picture)});");
                    refresh = false; break;
                case "clientError": HostLog.Write("panel " + root.GetProperty("value").GetString()); refresh = false; break;
                case "extOpen":
                    using (var dialog = new SelectFolderDialog { Title = "Open an Extension Repo" })
                        if (dialog.ShowDialog(this) == DialogResult.Ok) Extensions.ExtensionHost.Current.OpenRepository(dialog.Directory);
                    refresh = false; break;
                case "extNewRepo":
                    if (ValidName(root.GetProperty("value").GetString()) is not { } repoName) break;
                    using (var dialog = new SelectFolderDialog { Title = "Choose where to create the Extension Repo" })
                        if (dialog.ShowDialog(this) == DialogResult.Ok) Extensions.ExtensionHost.Current.NewRepository(Path.Combine(dialog.Directory, repoName));
                    refresh = false; break;
                case "extNew": Extensions.ExtensionHost.Current.NewExtension(root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "extEnable": Extensions.ExtensionHost.Current.SetEnabled(Name(), root.GetProperty("value").GetBoolean()); refresh = false; break;
                case "extStartup": Extensions.ExtensionHost.Current.SetStartup(Name(), root.GetProperty("value").GetBoolean()); refresh = false; break;
                case "extReload": Extensions.ExtensionHost.Current.Reload(Name()); refresh = false; break;
                case "extEdit": Extensions.ExtensionHost.Current.Edit(Name()); refresh = false; break;
                case "extReveal": Extensions.ExtensionHost.Current.Reveal(root.TryGetProperty("name", out var revealName) ? revealName.GetString() : null); refresh = false; break;
                case "agentCancel": AgentHost.Current.Cancel(); refresh = false; break;
                case "agentNew": if (host.Session.Repository is { } newIn) AgentHost.Current.NewConversation(newIn); refresh = false; break;
                case "agentLoad": if (host.Session.Repository is { } loadIn) AgentHost.Current.Load(loadIn, root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "agentPermission": AgentHost.Current.Answer(root.GetProperty("value").GetString()); refresh = false; break;
                case "agentQuestion": AgentHost.Current.AnswerQuestion(root.TryGetProperty("answers", out var answers) ? answers : default); refresh = false; break;
                case "agentFavorite": AgentHost.Current.Favorite(root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "agentOption": AgentHost.Current.SetOption(Name(), root.GetProperty("value").GetString() ?? ""); refresh = false; break;
                case "agentPath": AgentHost.Current.SetPath(root.GetProperty("value").GetString()); refresh = false; break;
                case "agentBrowse":
                    using (var dialog = new OpenFileDialog { Title = "Choose the OpenCode executable" })
                        if (dialog.ShowDialog(this) == DialogResult.Ok) AgentHost.Current.SetPath(dialog.FileName);
                    refresh = false; break;
                case "select": host.Select(root.GetProperty("path").GetString() ?? ""); break;
                case "input":
                    if (root.TryGetProperty("range", out var inputRange) && inputRange.ValueKind == JsonValueKind.Object)
                        host.SetRange(Name(), new NumericRange(inputRange.GetProperty("min").GetDouble(), inputRange.GetProperty("max").GetDouble(), inputRange.GetProperty("step").GetDouble()));
                    SetInput(Name(), root.GetProperty("value")); break;
                case "pulse": host.Pulse(Name()); break;
                case "run":
                    if (root.TryGetProperty("values", out var values))
                        foreach (var item in values.EnumerateObject()) SetInput(item.Name, item.Value);
                    host.Run(); break;
                case "auto": host.SetAuto(root.GetProperty("value").GetBoolean()); break;
                case "play": host.Play(); break;
                case "pause": host.Pause(); break;
                case "step": host.Step(); break;
                case "resetSolve": host.ResetSolve(); break;
                case "rate": host.SetFrameRate(root.GetProperty("value").GetDouble()); break;
                case "pick":
                    host.Pick(Name(), root.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "pick" : "pick",
                        root.TryGetProperty("index", out var pickIndex) ? pickIndex.GetInt32() : -1);
                    break;
                case "cancelPick": host.CancelPick(); break;
                case "reorder": host.ReorderPicked(Name(), root.GetProperty("from").GetInt32(), root.GetProperty("to").GetInt32()); break;
                case "selectPicked":
                    host.SelectPicked(Name(), root.TryGetProperty("index", out var selectIndex) && selectIndex.ValueKind == JsonValueKind.Number ? selectIndex.GetInt32() : null);
                    break;
                case "element": host.SetElementVisible(Name(), root.GetProperty("index").GetInt32(), root.GetProperty("visible").GetBoolean()); break;
                case "preview": host.SetPreviewVisible(Name(), root.GetProperty("visible").GetBoolean()); break;
                case "bake": host.Bake(root.GetProperty("names").EnumerateArray().Select(item => item.GetString() ?? "")); break;
                case "range":
                    var range = root.GetProperty("range");
                    host.SetRange(Name(), range.ValueKind == JsonValueKind.Null ? null
                        : new NumericRange(range.GetProperty("min").GetDouble(), range.GetProperty("max").GetDouble(), range.GetProperty("step").GetDouble()));
                    break;
                case "style": host.SetBooleanStyle(Name(), root.GetProperty("style").GetString() ?? ""); break;
                case "pstyle":
                    var style = root.GetProperty("style");
                    int? Int(string key) => style.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt32() : null;
                    host.SetPreviewStyle(Name(), new PreviewStyle(style.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String ? color.GetString() : null,
                        Int("weight"), Int("size"), Int("shading")));
                    break;
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        // Every panel action ends here: repaint the viewports and republish, so no action has to remember to.
        finally { if (refresh) host.Refresh(); }
    }

    private void ChooseRepository(bool create, string? name = null)
    {
        if (create && ValidName(name) is null) return;
        using var dialog = new SelectFolderDialog();
        if (dialog.ShowDialog(this) != DialogResult.Ok) return;
        if (create) host.New(Path.Combine(dialog.Directory, name!));
        else host.Open(dialog.Directory);
    }

    private string? ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\')) { ShowError("Enter a valid repository name."); return null; }
        return name;
    }

    private void SetInput(string name, JsonElement raw)
    {
        var port = host.Session.Selected?.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput);
        if (port is null) return;
        if (raw.ValueKind == JsonValueKind.Null) { host.SetInput(name, null); return; }
        if (port.TypeName == "System.Boolean" && raw.ValueKind is JsonValueKind.True or JsonValueKind.False)
        { host.SetInput(name, raw.GetBoolean()); return; }
        var text = raw.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) { host.SetInput(name, null); return; }
        var element = PortTypeName.Element(port.TypeName);
        if (SingleScriptHost.IsColor(element))
        {
            var colors = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ColorText.Parse).ToArray();
            if (colors.Length > 0 && colors.All(c => c is not null))
                host.SetInput(name, port.Access == PortAccess.List ? colors.Select(c => c!.Value).ToArray() : colors[0]);
            return;
        }
        if (port.Access == PortAccess.List)
        {
            var parts = text.Split([',', '，', '、', ';', '；'], StringSplitOptions.TrimEntries);
            if (element == "System.String") host.SetInput(name, parts);
            else if (element == "System.Boolean" && parts.All(p => bool.TryParse(p, out _))) host.SetInput(name, parts.Select(bool.Parse).ToArray());
            else if (element == "System.Int32" && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                host.SetInput(name, parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray());
            else if (element == "System.Double" && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                host.SetInput(name, parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray());
            else host.SetInput(name, text);
        }
        else if (port.TypeName == "System.Int32" && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) host.SetInput(name, integer);
        else if (port.TypeName == "System.Double" && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) host.SetInput(name, number);
        else host.SetInput(name, text);
    }

    private string? lastSnapshot;

    /// <summary>Sends the current state to the panel and mirror; an unchanged state is skipped unless forced (a new page needs it).</summary>
    private void Publish(bool force = false)
    {
        if (!loaded && mirror is null && !MirrorServer.Running) return;
        var session = host.Session;
        var selected = session.Selected;
        var inputs = selected?.Ports.Where(p => !p.IsOutput).Select(p =>
        {
            var type = PortTypeName.Element(p.TypeName);
            var kind = SingleScriptHost.PortKind(p);
            var value = session.Inputs.GetValueOrDefault(p.Name);
            var picked = kind == "pick" ? host.Picked(p.Name) : null;
            var family = kind == "pick" ? GeometryVocabulary.FamilyOf(type) : null;
            return new { name = p.Name, type = p.TypeName, access = p.Access.ToString(), optional = p.IsOptional,
                tooltip = p.Tooltip, kind, value = kind == "pick" ? null
                    : kind == "extension" ? (value as IFormattable)?.ToString(null, CultureInfo.InvariantCulture) ?? value?.ToString() : FormatInput(value),
                picked = picked?.Label, items = picked?.Items.Take(200).Select(item => new { label = item.Label, visible = item.Visible }).ToArray(),
                count = picked?.Items.Length ?? 0, family, preview = kind == "pick" ? (object?)new { visible = host.PreviewVisible(p.Name), style = StyleSnapshot(p.Name, family, true) } : null,
                range = host.NumericRanges.GetValueOrDefault(p.Name), boolStyle = host.BooleanStyles.GetValueOrDefault(p.Name) ?? "press",
                editor = kind == "extension" ? Extensions.ExtensionHost.EditorFor(type) : null };
        }).ToArray() ?? [];
        var outputs = session.HasCurrentOutputs ? session.Outputs.Select(pair =>
        {
            var port = selected?.Ports.FirstOrDefault(p => p.IsOutput && p.Name == pair.Key);
            var value = pair.Value;
            var count = value is ICollection collection && value is not string and not Polyline and not GeometryBase ? collection.Count : value is null ? 0 : 1;
            var items = value is IEnumerable list && value is not string and not Polyline and not GeometryBase
                ? list.Cast<object?>().Take(20).ToArray() : value is null ? [] : new object?[] { value };
            var values = items.Select(OutputItem).Where(x => x is not null).Cast<string>().ToArray();
            var scalarValues = items.Select(Scalar).Where(x => x is not null).Cast<string>().ToArray();
            return new { name = pair.Key, type = port?.TypeName ?? value?.GetType().Name ?? "unknown", count,
                summary = scalarValues.Length > 0 ? string.Join(", ", scalarValues.Take(3)) + (count > 3 ? "…" : "") : count + " item" + (count == 1 ? "" : "s"),
                values, preview = host.CanPreview(pair.Key), visible = host.PreviewVisible(pair.Key), bake = host.CanBake(pair.Key), historical = false,
                family = host.OutputFamily(pair.Key), style = host.CanPreview(pair.Key) ? StyleSnapshot(pair.Key, host.OutputFamily(pair.Key), false) : null };
        }).ToArray() : host.HistoricalRecord is { } previous
            ? previous.Outputs.Select(pair => new { name = pair.Key, type = pair.Value.Type, count = pair.Value.Count,
                summary = pair.Value.Value ?? "Historical result", values = Array.Empty<string>(), preview = false, visible = false, bake = false, historical = true,
                family = (string?)null, style = (object?)null }).ToArray() : [];
        var status = host.Running ? "running" : messageIsError || session.Result.Status is "error" or "compile_error" ? "error"
            : selected is null ? "ready" : session.Dirty || session.Result.Status == "pending" ? "pending" : "completed";
        var snapshot = JsonSerializer.Serialize(new { pid = Environment.ProcessId, revision = host.Revision, repository = session.Repository,
            scripts = (session.Scripts.Count > 0 ? session.Scripts : selected is null ? [] : new[] { selected })
                .Select(script => new { name = script.Name, path = script.EntryFile, category = script.Category.Length > 0 ? script.Category : null }).ToArray(),
            selected = selected?.EntryFile, inputs, outputs, status, auto = host.Auto,
            error = messageIsError ? message : session.Result.Error,
            log = message is not null && !messageIsError ? session.Result.Log.Append(message).ToArray() : session.Result.Log,
            statusLine = session.Result.StatusLine,
            historical = host.HistoricalRun, notice = host.RestoreNotice,
            picking = host.ActivePick is { } pick ? new { name = pick.Name, mode = pick.Mode, index = pick.Index } : null,
            agent = AgentHost.Current.Snapshot(),
            extensions = Extensions.ExtensionHost.Current.Snapshot(),
            solve = host.Solve is { } solve ? new { playing = solve.Playing, frame = solve.Frame, done = solve.Done, stalled = solve.Stalled,
                rate = solve.Rate, fps = solve.FramesPerSecond, held = solve.Held } : null }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (!force && snapshot == lastSnapshot) return;
        lastSnapshot = snapshot;
        mirror?.Publish(snapshot);
        MirrorServer.PublishPanel(snapshot);
        if (loaded) _ = snapshots.Send("window.rhDockSnapshot && window.rhDockSnapshot(" + snapshot + ");");
    }

    // Prompt context the user can see and remove in the Agent tab. Text context goes as rhdock:// resources that end in a
    // marker line, because OpenCode flattens resources into the message text and the replay must strip them again.
    // The context rhDock adds to a prompt: named text blocks (the active Script, the Rhino selection) and repository files the agent reads.
    private (List<(string Name, string Text)> Blocks, List<string> Files, string[] Chips) AgentContext(string repository, bool script, JsonElement mentions)
    {
        var blocks = new List<(string Name, string Text)>();
        var linked = new List<string>();
        var chips = new List<string>();
        var session = host.Session;
        if (script && session.Selected is { } selected)
        {
            var inputs = selected.Ports.Where(p => !p.IsOutput).Select(p =>
                $"- {p.Name} ({PortTypeName.Element(p.TypeName).Split('.').Last()}{(p.Access == PortAccess.List ? " list" : "")}): "
                + (host.Picked(p.Name)?.Label ?? FormatInput(session.Inputs.GetValueOrDefault(p.Name)) ?? "not set"));
            var outputs = selected.Ports.Where(p => p.IsOutput).Select(p => $"- {p.Name} ({PortTypeName.Element(p.TypeName).Split('.').Last()})");
            blocks.Add(("active-script", $"rhDock active Script: {selected.Name} ({selected.EntryFile}). rhDock reloads it when the file changes.\n"
                + $"Inputs:\n{string.Join("\n", inputs)}\nOutputs:\n{string.Join("\n", outputs)}\n"
                + $"Last result: {session.Result.Status}{(session.Result.Error is { Length: > 0 } error ? "\nError: " + error : "")}"));
            linked.Add(Path.Combine(repository, selected.EntryFile));
            chips.Add(selected.Name);
        }
        if (mentions.ValueKind == JsonValueKind.Array)
            foreach (var mention in mentions.EnumerateArray())
            {
                var kind = mention.GetProperty("kind").GetString();
                var path = mention.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (kind is "file" or "script" && path is { Length: > 0 } && File.Exists(Path.Combine(repository, path)))
                {
                    linked.Add(Path.Combine(repository, path));
                    chips.Add(mention.TryGetProperty("label", out var label) ? label.GetString() ?? path : path);
                }
                else if (kind == "selection" && RhinoDoc.ActiveDoc is { } doc)
                {
                    var selectedObjects = doc.Objects.GetSelectedObjects(false, false).Take(50).Select(o =>
                        $"- {o.Id} on layer {doc.Layers[o.Attributes.LayerIndex].FullPath}: {GeometryVocabulary.Describe(o.Geometry)}").ToArray();
                    blocks.Add(("selection", selectedObjects.Length == 0 ? "The Rhino selection is empty."
                        : $"Objects selected in Rhino ({selectedObjects.Length}{(selectedObjects.Length == 50 ? " or more" : "")}):\n{string.Join("\n", selectedObjects)}"));
                    chips.Add("Rhino selection");
                }
            }
        return (blocks, linked, chips.ToArray());
    }

    // The active view as a PNG no larger than 1568 px, a size vision models accept without scaling.
    private void CaptureViewport()
    {
        if (RhinoDoc.ActiveDoc?.Views.ActiveView is not { } view) { ShowError("There is no active viewport to capture."); return; }
        var size = view.ActiveViewport.Size;
        var scale = Math.Min(1.0, 1568.0 / Math.Max(1, Math.Max(size.Width, size.Height)));
        using var bitmap = view.CaptureToBitmap(new System.Drawing.Size((int)(size.Width * scale), (int)(size.Height * scale)));
        if (bitmap is null) { ShowError("Rhino could not capture the viewport."); return; }
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var data = Convert.ToBase64String(png.ToArray());
        using var small = view.CaptureToBitmap(new System.Drawing.Size(160, Math.Max(1, 160 * bitmap.Height / Math.Max(1, bitmap.Width))));
        using var jpeg = new MemoryStream();
        small?.Save(jpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
        AgentHost.Current.Attach($"{view.ActiveViewport.Name} viewport.png", "image/png", data, null, jpeg.Length > 0 ? "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray()) : null);
    }

    private static string? Scalar(object? value) => value switch
    {
        null => null, string text => text, bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        System.Drawing.Color color => ColorText.Format(color), _ => null
    };

    private static string? OutputItem(object? value) => value switch
    {
        Polyline or TextDot or Point3d or GeometryBase => GeometryVocabulary.Describe(value),
        _ => Scalar(value)
    };

    private object StyleSnapshot(string name, string? family, bool input)
    {
        var style = host.ResolvedStyle(name, family, input);
        return new { color = style.Color, custom = style.Custom, weight = style.Weight, size = style.Size, shading = style.Shading };
    }

    private static string? FormatInput(object? value) => value is IEnumerable list and not string
        ? string.Join(", ", list.Cast<object?>().Select(Scalar)) : Scalar(value);
}
