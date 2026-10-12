using System.Collections;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using rhDock.Core;

namespace rhDock.Rhino;

internal sealed class SingleScriptHost : IReloadable, IDisposable
{
    private static SingleScriptHost? current;
    public static SingleScriptHost Current => current ??= new();
    public static void Shutdown() { current?.Dispose(); current = null; }

    private readonly ScriptCompilerService compiler;
    private readonly ProjectWatchService watcher;
    private readonly SingleScriptSession session;
    private readonly DockConduit conduit = new() { Enabled = true };
    private readonly Dictionary<string, PickBinding> picks = new(StringComparer.Ordinal);
    private readonly HashSet<string> changedPicks = new(StringComparer.Ordinal);
    private readonly HashSet<string> hiddenPreview = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumericRange> numericRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> booleanStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewStyle> previewStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<PickEntry>> hiddenElements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SavedScriptState> scriptStates = new(StringComparer.Ordinal);
    private SingleScriptPanel? panel;
    private RhinoDoc? document;
    private DateTime? autoDue;
    private DateTime? saveDue;
    private SingleScriptRunRecord? lastRun;
    private bool historicalRun;
    private string? restoreNotice;
    private bool restoring;
    private bool running;
    private bool disposed;
    private int revision;
    private PickRequest? activePick;
    private PickEntry[]? pendingSelect;
    private bool playing;
    private bool stalled;
    private int unchangedFrames;
    private double frameRate;
    private Eto.Forms.UITimer? solveTimer;
    private readonly Queue<DateTime> frameTimes = new();
    private DateTime lastSolvePublish;

    private SingleScriptHost()
    {
        compiler = new(HostScriptReferences());
        watcher = new(compiler);
        session = new(compiler);
        RhinoApp.Idle += OnIdle;
        RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject += OnObjectChanged;
        RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
        RhinoDoc.AddRhinoObject += OnObjectChanged;
        RhinoDoc.CloseDocument += OnCloseDocument;
    }

    private static string[] HostScriptReferences()
    {
        var paths = new List<string> { typeof(IPortBag).Assembly.Location, typeof(RhinoDoc).Assembly.Location };
        var plugins = Path.GetFullPath(Path.Combine(RhinoApp.GetExecutableDirectory().FullName, "..", "Plug-ins", "Grasshopper"));
        foreach (var name in new[] { "Grasshopper.dll", "GH_IO.dll" })
            if (File.Exists(Path.Combine(plugins, name))) paths.Add(Path.Combine(plugins, name));
        return paths.ToArray();
    }

    public SingleScriptSession Session => session;
    public bool MirrorActive => panel?.MirrorActive == true;
    public bool StartMirror(int port)
    {
        if (panel is null) { RhinoApp.WriteLine("Open the rhDock panel before starting the development mirror."); return false; }
        panel.StartMirror(port);
        RhinoApp.WriteLine($"rhDock development mirror connecting to http://127.0.0.1:{port}/?mirror=1");
        return true;
    }
    public void StopMirror() { panel?.StopMirror(); RhinoApp.WriteLine("rhDock development mirror stopped."); }
    public bool Auto { get; private set; }
    public SingleScriptRunRecord? HistoricalRecord => historicalRun ? lastRun : null;
    public bool Running => running;
    public int Revision => revision;
    public IReadOnlyDictionary<string, NumericRange> NumericRanges => numericRanges;
    public IReadOnlyDictionary<string, string> BooleanStyles => booleanStyles;
    internal PickRequest? ActivePick => activePick;

    /// <summary>Solver Run Bar state; null for one-shot Scripts.</summary>
    internal SolveState? Solve => session.IsSolver
        ? new(playing, session.Frame, session.Done, stalled, frameRate, playing ? FramesPerSecond() : null, playing && !SolveEligible)
        : null;

    /// <summary>Picked Rhino objects for one input, in Script order, with element descriptions for the panel.</summary>
    internal PickedList? Picked(string name)
    {
        if (!picks.TryGetValue(name, out var pick) || !session.Inputs.TryGetValue(name, out var value)) return null;
        hiddenElements.TryGetValue(name, out var hidden);
        var leaves = Leaves(value).ToArray();
        var items = pick.Entries.Select((entry, i) => new PickedElement(i < leaves.Length ? GeometryVocabulary.Describe(leaves[i]) : "Unavailable",
            hidden?.Contains(entry) != true)).ToArray();
        return new(GeometryVocabulary.CountLabel(PortTypeName.Element(pick.TypeName), pick.Entries.Length), items);
    }

    /// <summary>The Preview Style the host draws with, defaults filled in, for panel glyphs and controls.</summary>
    internal (string Color, bool Custom, int Weight, int Size, int Shading) ResolvedStyle(string name, string? family, bool input)
    {
        var style = previewStyles.GetValueOrDefault(name);
        var color = style?.Color ?? (input ? InputPreviewColor : family == "point" ? PointPreviewColor : OutputPreviewColor);
        return (color, style?.Color is not null, style?.Weight ?? (family == "curve" ? 2 : 1), style?.Size ?? 5, style?.Shading ?? 30);
    }

    internal string? OutputFamily(string name)
    {
        if (!session.Outputs.TryGetValue(name, out var value)) return null;
        var families = Leaves(value).Take(1000).Select(GeometryVocabulary.Family).Where(f => f is not null).Distinct().ToArray();
        return families.Length switch { 0 => null, 1 => families[0], _ => "mixed" };
    }

    private const string InputPreviewColor = "#9AA0F2";
    private const string OutputPreviewColor = "#40E0D0";
    private const string PointPreviewColor = "#DAA520";
    public string? HistoricalRun => historicalRun && lastRun is { } run ? FormatHistoricalRun(run) : null;
    public string? RestoreNotice => restoreNotice;

    private static string FormatHistoricalRun(SingleScriptRunRecord run)
    {
        var lines = new List<string>
        {
            $"Last Run (historical, not valid for Preview/Bake): {run.Status} · {run.Utc:u}",
            "Script: " + run.Script,
            "Inputs:"
        };
        foreach (var (name, input) in run.Inputs)
            lines.Add(name + ": " + (input.Items is { } items ? string.Join(", ", items) : input.Value ?? "null"));
        foreach (var (name, pick) in run.Picks)
            lines.Add(name + ": " + (pick.Kind == "objects" ? string.Join(", ", pick.Ids ?? [])
                : string.Join("; ", (pick.Points ?? []).Select(point => string.Join(", ", point)))));
        if (run.Error is not null) lines.Add(run.Error);
        lines.AddRange(run.Diagnostics);
        lines.Add("Outputs:");
        foreach (var (name, output) in run.Outputs)
            lines.Add(name + ": " + output.Type + " ×" + output.Count
                + (output.Value is null ? "" : " = " + output.Value));
        return string.Join("\n", lines);
    }

    public void SetAuto(bool enabled)
    {
        Auto = enabled;
        autoDue = null;
        if (enabled && session.Dirty) ScheduleAuto();
        ScheduleSave();
        Changed();
    }

    public void Toggle(RhinoDoc doc)
    {
        if (global::Rhino.UI.Panels.IsPanelVisible(typeof(SingleScriptPanel)))
        { SaveNow(); global::Rhino.UI.Panels.ClosePanel(typeof(SingleScriptPanel)); return; }
        if (document is not null && document.RuntimeSerialNumber != doc.RuntimeSerialNumber)
        { SaveNow(); Halt(); ClearPreview(); ClearPickedInputs(); }
        document = doc;
        conduit.DocumentSerial = doc.RuntimeSerialNumber;
        global::Rhino.UI.Panels.OpenPanel(typeof(SingleScriptPanel));
        RebuildPreview();
        if (Auto && session.Dirty) ScheduleAuto();
    }

    public void Attach(SingleScriptPanel view)
    {
        panel = view;
        if (document is null && RhinoDoc.ActiveDoc is { } active)
        {
            document = active;
            conduit.DocumentSerial = active.RuntimeSerialNumber;
            RebuildPreview();
        }
        panel.RefreshSession();
        if (Auto && session.Dirty) ScheduleAuto();
    }
    public bool IsAttached(SingleScriptPanel view) => ReferenceEquals(panel, view);

    public void Detach(SingleScriptPanel view)
    {
        if (!ReferenceEquals(panel, view)) return;
        SaveNow(); autoDue = null; panel = null;
    }

    public void Open(string folder)
    {
        try
        {
            SaveNow();
            // A failed write is reported by SaveNow; it must not trap the user in this Workspace.
            if (saveDue is not null)
                RhinoApp.WriteLine("Opening another repository with unsaved Workspace changes: " + session.Repository);
            autoDue = null;
            saveDue = null;
            if (session.Repository is { } previous) watcher.Unregister(previous);
            Halt();
            stalled = false;
            frameRate = 0;
            ClearPreview();
            ClearPickedInputs();
            hiddenPreview.Clear();
            numericRanges.Clear();
            booleanStyles.Clear();
            previewStyles.Clear();
            hiddenElements.Clear();
            scriptStates.Clear();
            lastRun = null;
            historicalRun = false;
            restoreNotice = null;
            Auto = false;
            session.Open(folder);
            revision++;
            watcher.Register(session.Repository!, this);
            try
            {
                if (SingleScriptWorkspaceStore.LoadHistory(session.Repository!) is { Revisions: { Length: > 0 } revisions })
                    RestoreWorkspace(revisions[^1]);
            }
            catch (Exception ex) { restoreNotice = "Workspace not restored: " + ex.Message; }
            Changed(session: true);
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
    }

    public void New(string folder)
    {
        try
        {
            WorkingRepositoryTemplate.Create(folder);
            Open(folder);
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
    }

    public void Select(string entry)
    {
        try
        {
            if (session.Selected?.EntryFile == entry) return;
            RememberActive();
            Auto = false;
            autoDue = null;
            Halt();
            stalled = false;
            frameRate = 0;
            ClearPreview();
            ClearPickedInputs();
            hiddenPreview.Clear();
            numericRanges.Clear();
            booleanStyles.Clear();
            previewStyles.Clear();
            hiddenElements.Clear();
            lastRun = null;
            historicalRun = false;
            restoreNotice = null;
            session.Select(entry);
            revision++;
            if (scriptStates.TryGetValue(entry, out var saved)) RestoreScriptState(saved);
            RebuildPreview();
            Changed(session: true);
            ScheduleSave();
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
    }

    /// <summary>Republishes the panel without a viewport redraw, for Agent tab updates.</summary>
    internal void RefreshPanel() => panel?.RefreshResult();

    /// <summary>Raised after every Session change the panel shows; Extensions hear it through <c>rh.Scripts.Changed</c>.</summary>
    internal event Action? StateChanged;

    private void Changed(bool session = false)
    {
        if (session) panel?.RefreshSession();
        else panel?.RefreshResult();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// How the panel edits an input port. The panel, <c>rh.Scripts.SetInput</c>, and Extension input editors all use this,
    /// so an Extension can set exactly what the panel can, plus types an enabled Extension registered an editor for.
    /// </summary>
    internal static string PortKind(PortDescriptor p)
    {
        var type = PortTypeName.Element(p.TypeName);
        return p.Access == PortAccess.List && type is "System.Double" or "System.Int32" or "System.String" or "System.Boolean" ? "list"
            : p.Access == PortAccess.Item && type == "System.Double" ? "number"
            : p.Access == PortAccess.Item && type == "System.Int32" ? "int"
            : p.Access == PortAccess.Item && type == "System.String" ? "text"
            : p.Access == PortAccess.Item && type == "System.Boolean" ? "boolean"
            : p.Access != PortAccess.Tree && IsColor(type) ? "color"
            : GeometryVocabulary.CanPick(type) ? "pick"
            : p.Access == PortAccess.Item && Extensions.ExtensionHost.EditorFor(type) is not null ? "extension"
            : "unsupported";
    }

    /// <summary>The one exit of every user-driven operation: repaint Rhino's viewports and republish the panel state.</summary>
    internal void Refresh()
    {
        document?.Views.Redraw();
        Changed();
    }

    public void SetInput(string name, object? value) => ApplyInput(name, value, null);

    private void ApplyInput(string name, object? value, PickBinding? pick)
    {
        try
        {
            var old = picks.ContainsKey(name) ? session.Inputs.GetValueOrDefault(name) : null;
            session.SetInput(name, value);
            stalled = false;
            unchangedFrames = 0;
            if (!Auto) Halt();
            if (value is not null) ClearAttention(name);
            picks.Remove(name);
            changedPicks.Remove(name);
            if (pick is not null) picks[name] = pick;
            if (pick is null) hiddenElements.Remove(name);
            else if (hiddenElements.TryGetValue(name, out var hidden)) hidden.IntersectWith(pick.Entries);
            RebuildPreview();
            if (!ReferenceEquals(old, value)) DisposePickedValue(old);
            Changed();
            ScheduleAuto();
            ScheduleSave();
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
    }

    /// <summary>Runs a one-shot Script once; for a Solver Script, plays Frames until Done or Pause.</summary>
    public void Run()
    {
        if (session.IsSolver) Play();
        else RunCore(null);
    }

    public void Play()
    {
        if (!session.IsSolver || session.Selected is null) return;
        autoDue = null;
        if (session.Done || stalled) { session.ResetSolve(); RebuildPreview(); }
        stalled = false;
        unchangedFrames = 0;
        frameTimes.Clear();
        playing = true;
        StartSolveTimer();
        Changed();
    }

    public void Pause()
    {
        if (!playing) return;
        Halt();
        RecordSolve();
        Changed();
    }

    /// <summary>Runs exactly one Frame and leaves the Solve paused.</summary>
    public void Step()
    {
        if (!session.IsSolver || playing) return;
        if (session.Done || stalled) { session.ResetSolve(); stalled = false; unchangedFrames = 0; }
        RunCore(null);
    }

    public void ResetSolve()
    {
        if (!session.IsSolver) return;
        Halt();
        session.ResetSolve();
        stalled = false;
        unchangedFrames = 0;
        RebuildPreview();
        if (Auto) Play();
        Changed();
    }

    public void SetFrameRate(double rate)
    {
        if (rate is not (0 or 1 or 10 or 30)) return;
        frameRate = rate;
        if (playing) StartSolveTimer();
        ScheduleSave();
        Changed();
    }

    private bool SolveEligible => !disposed && activePick is null && !Command.InCommand() && document is not null
        && global::Rhino.UI.Panels.IsPanelVisible(typeof(SingleScriptPanel))
        && RhinoDoc.ActiveDoc?.RuntimeSerialNumber == document.RuntimeSerialNumber;

    private void StartSolveTimer()
    {
        if (solveTimer is null) { solveTimer = new Eto.Forms.UITimer(); solveTimer.Elapsed += (_, _) => SolveTick(); }
        solveTimer.Stop();
        solveTimer.Interval = frameRate > 0 ? 1.0 / frameRate : 0.01;
        solveTimer.Start();
    }

    private void Halt()
    {
        playing = false;
        solveTimer?.Stop();
    }

    private void RecordSolve()
    {
        if (session.Selected is null || session.Result.Status == "pending") return;
        var captured = CaptureInputs();
        lastRun = SingleScriptWorkspaceStore.CaptureRun(session, captured.Inputs, captured.Picks);
        historicalRun = false;
        ScheduleSave();
    }

    /// <summary>
    /// Runs Frames on the UI thread: one per tick when a rate is set, otherwise as many as fit in a short budget so
    /// Rhino stays responsive. Rhino commands, picks, a hidden panel, or another active document hold the Solve.
    /// </summary>
    private void SolveTick()
    {
        if (!playing || running) { if (!playing) solveTimer?.Stop(); return; }
        if (!SolveEligible) { PublishSolve(false); return; }
        running = true;
        try
        {
            RefreshPickedGeometry();
            if (!playing) return;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var previous = session.HasCurrentOutputs ? session.Outputs.ToArray() : null;
                var result = session.Run();
                frameTimes.Enqueue(DateTime.UtcNow);
                if (result.Status != "ok" || session.Done) { Halt(); break; }
                if (previous is not null && SameOutputs(previous, session.Outputs)) { if (++unchangedFrames >= 3) { stalled = true; Halt(); break; } }
                else unchangedFrames = 0;
            } while (frameRate == 0 && budget.ElapsedMilliseconds < 16);
            if (!playing) RecordSolve();
        }
        catch (Exception ex) { Halt(); panel?.ShowError(ex.Message); }
        finally { running = false; RebuildPreview(); PublishSolve(!playing); }
    }

    private void PublishSolve(bool force)
    {
        if (!force && DateTime.UtcNow - lastSolvePublish < TimeSpan.FromMilliseconds(100)) return;
        lastSolvePublish = DateTime.UtcNow;
        Changed();
    }

    private int FramesPerSecond()
    {
        while (frameTimes.Count > 0 && DateTime.UtcNow - frameTimes.Peek() > TimeSpan.FromSeconds(1)) frameTimes.Dequeue();
        return frameTimes.Count;
    }

    /// <summary>
    /// True when every output except Info equals the previous Frame's value. The same mutable object returned twice
    /// is treated as changed, because the Script may have modified it in place.
    /// </summary>
    private static bool SameOutputs(KeyValuePair<string, object?>[] previous, IReadOnlyDictionary<string, object?> current)
    {
        if (previous.Length != current.Count) return false;
        var budget = 20000;
        foreach (var (name, value) in previous)
            if (!name.Equals("Info", StringComparison.OrdinalIgnoreCase)
                && (!current.TryGetValue(name, out var next) || !Same(value, next, ref budget))) return false;
        return true;
    }

    private static bool Same(object? a, object? b, ref int budget)
    {
        if (--budget < 0) return false;
        if (a is null || b is null) return a is null && b is null;
        if (a is string || a.GetType().IsValueType) return a.Equals(b);
        if (ReferenceEquals(a, b)) return false;
        if (a is GeometryBase first && b is GeometryBase second) return GeometryBase.GeometryEquals(first, second);
        if (a is IEnumerable left && b is IEnumerable right)
        {
            var l = left.GetEnumerator();
            var r = right.GetEnumerator();
            while (true)
            {
                var hasLeft = l.MoveNext();
                if (hasLeft != r.MoveNext()) return false;
                if (!hasLeft) return true;
                if (!Same(l.Current, r.Current, ref budget)) return false;
            }
        }
        return false;
    }

    public void Pulse(string name) => RunCore(name);

    private void RunCore(string? pulse)
    {
        autoDue = null;
        if (running) return;
        running = true;
        Changed();
        try
        {
            RefreshPickedGeometry();
            // A manual Run consumes any Auto request created while refreshing a picked object.
            autoDue = null;
            if (pulse is null) session.Run();
            else session.RunPulse(pulse);
            if (changedPicks.Count > 0)
            {
                foreach (var name in changedPicks)
                    session.SetInput(name, session.Inputs.GetValueOrDefault(name));
                Changed();
                return;
            }
            var captured = CaptureInputs();
            if (pulse is not null) captured.Inputs[pulse] = SavedScalar.Capture(true)!;
            lastRun = SingleScriptWorkspaceStore.CaptureRun(session, captured.Inputs, captured.Picks);
            historicalRun = false;
            ScheduleSave();
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
        finally { running = false; RebuildPreview(); Changed(); }
    }

    private void ScheduleAuto()
    {
        if (Auto && global::Rhino.UI.Panels.IsPanelVisible(typeof(SingleScriptPanel)) && session.Selected is not null && session.Result.Status != "compile_error")
            autoDue = DateTime.UtcNow.AddMilliseconds(300);
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (disposed || running) return;
        RefreshPickedGeometry();
        if (Auto && autoDue is { } due && DateTime.UtcNow >= due
            && global::Rhino.UI.Panels.IsPanelVisible(typeof(SingleScriptPanel)) && document is not null
            && RhinoDoc.ActiveDoc?.RuntimeSerialNumber == document.RuntimeSerialNumber) Run();
        if (saveDue is { } save && DateTime.UtcNow >= save) SaveNow();
    }

    private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e)
    {
        if (document?.RuntimeSerialNumber == e.Document.RuntimeSerialNumber) MarkPickedChanged(e.ObjectId);
    }

    private void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
        if (document is null || e.TheObject.Document is { } source && source.RuntimeSerialNumber != document.RuntimeSerialNumber) return;
        MarkPickedChanged(e.ObjectId);
    }

    private void MarkPickedChanged(Guid id)
    {
        var affected = false;
        foreach (var (name, pick) in picks)
            if (pick.Entries.Any(entry => entry.Id == id))
            {
                affected = true;
                changedPicks.Add(name);
                session.SetInput(name, session.Inputs.GetValueOrDefault(name));
            }
        if (!affected) return;
        ClearPreview(false);
        Changed();
    }

    private void RefreshPickedGeometry()
    {
        if (document is null || changedPicks.Count == 0) return;
        foreach (var name in changedPicks.ToArray())
        {
            if (!picks.TryGetValue(name, out var pick)) continue;
            var element = PortTypeName.Element(pick.TypeName);
            if (!GeometryVocabulary.CanPick(element)) continue;
            var values = ExtractPicks(pick.Entries, element);
            ApplyInput(name, values is null ? null : GeometryVocabulary.PickValue(values, element, pick.Access == PortAccess.List), pick);
            if (values is null) panel?.ShowError("Picked geometry is unavailable for " + name + "; pick again.");
        }
        changedPicks.Clear();
        document.Views.Redraw();
    }

    private void OnCloseDocument(object? sender, DocumentEventArgs e)
    {
        if (document?.RuntimeSerialNumber != e.Document.RuntimeSerialNumber) return;
        SaveNow();
        autoDue = null;
        Halt();
        document = null;
        ClearPreview();
        ClearPickedInputs();
        global::Rhino.UI.Panels.ClosePanel(typeof(SingleScriptPanel));
    }

    public bool CanBake(string name) => session.HasCurrentOutputs
        && session.Outputs.TryGetValue(name, out var value)
        && Leaves(value).Any(IsBakeable);

    public bool CanPreview(string name) => session.HasCurrentOutputs
        && session.Outputs.TryGetValue(name, out var value)
        && Leaves(value).Any(IsPreviewable);

    public bool PreviewVisible(string name) => !hiddenPreview.Contains(name);

    public void SetPreviewVisible(string name, bool visible)
    {
        if (!CanPreview(name) && PickPort(name) is null) return;
        if (visible) hiddenPreview.Remove(name);
        else hiddenPreview.Add(name);
        RebuildPreview();
        ScheduleSave();
    }

    public void SetRange(string name, NumericRange? range)
    {
        if (range is null) { if (numericRanges.Remove(name)) ScheduleSave(); return; }
        if (session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput)
            is not { TypeName: "System.Double" or "System.Int32", Access: PortAccess.Item }
            || !double.IsFinite(range.Min) || !double.IsFinite(range.Max) || !double.IsFinite(range.Step)
            || range.Min >= range.Max || range.Step <= 0) return;
        numericRanges[name] = range;
        ScheduleSave();
    }

    public void SetBooleanStyle(string name, string style)
    {
        if (style is not ("press" or "switch") || session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput)
            is not { TypeName: "System.Boolean", Access: PortAccess.Item }) return;
        booleanStyles[name] = style;
        ScheduleSave();
    }

    public void SetPreviewStyle(string name, PreviewStyle style)
    {
        if (!style.IsValid || !CanPreview(name) && PickPort(name) is null) return;
        if (style == new PreviewStyle(null, null, null, null)) previewStyles.Remove(name); else previewStyles[name] = style;
        RebuildPreview(); ScheduleSave();
    }

    /// <summary>Color inputs take System.Drawing.Color values; the runner converts them for Color4f ports.</summary>
    public static bool IsColor(string elementTypeName) => elementTypeName is "System.Drawing.Color" or "Rhino.Display.Color4f";

    private PortDescriptor? ColorPort(string name) =>
        session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput && port.Access != PortAccess.Tree && IsColor(PortTypeName.Element(port.TypeName)));

    private PortDescriptor? PickPort(string name) =>
        session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput
            && GeometryVocabulary.CanPick(PortTypeName.Element(port.TypeName)));

    private void ScheduleSave()
    {
        if (!restoring && session.Repository is not null && session.Selected is not null)
            saveDue = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void SaveNow()
    {
        if (saveDue is null || session.Repository is null || session.Selected is null) return;
        saveDue = null;
        try
        {
            SingleScriptWorkspaceStore.Append(session.Repository, CurrentRevision());
            if (restoreNotice?.StartsWith("Workspace auto-save failed:", StringComparison.Ordinal) == true)
                restoreNotice = null;
        }
        catch (Exception ex)
        {
            saveDue = DateTime.MaxValue; // Retry on the next edit or when the panel/document closes.
            var message = "Workspace auto-save failed: " + ex.Message;
            restoreNotice = message;
            RhinoApp.WriteLine(message);
            panel?.ShowError(message);
        }
    }

    public void ExportWorkspace(string path)
    {
        if (session.Repository is null || session.Selected is null)
        { panel?.ShowError("Open a repository and select a Script before Export."); return; }
        try
        {
            SingleScriptWorkspaceStore.Export(session.Repository, CurrentRevision(), path);
            panel?.ShowMessage("Exported current Workspace: " + path);
        }
        catch (Exception ex) { panel?.ShowError("Workspace export failed: " + ex.Message); }
    }

    private WorkspaceRevision CurrentRevision()
    {
        var script = session.Selected?.EntryFile ?? throw new InvalidOperationException("Select a Script first.");
        scriptStates[script] = CaptureScriptState();
        return new(DateTime.UtcNow, script, Auto ? "auto" : "manual",
            new(scriptStates, StringComparer.Ordinal)) { Pid = System.Environment.ProcessId };
    }

    private void RememberActive()
    {
        if (session.Selected is { } selected) scriptStates[selected.EntryFile] = CaptureScriptState();
    }

    private SavedScriptState CaptureScriptState()
    {
        var captured = CaptureInputs();
        return new(captured.Inputs, captured.Picks, hiddenPreview.ToArray(), captured.Omitted, lastRun)
        {
            NumericRanges = new(numericRanges, StringComparer.Ordinal),
            BooleanStyles = new(booleanStyles, StringComparer.Ordinal),
            PreviewStyles = new(previewStyles, StringComparer.Ordinal),
            FrameRate = frameRate > 0 ? frameRate : null
        };
    }

    private (Dictionary<string, SavedScalar> Inputs, Dictionary<string, SavedPick> Picks, string[] Omitted) CaptureInputs()
    {
        var values = new Dictionary<string, SavedScalar>(StringComparer.Ordinal);
        var savedPicks = new Dictionary<string, SavedPick>(StringComparer.Ordinal);
        var omitted = new List<string>();
        foreach (var (name, value) in session.Inputs)
        {
            if (picks.TryGetValue(name, out var binding))
            {
                var ids = binding.Entries.Select(entry => entry.Id.ToString()).ToArray();
                savedPicks[name] = binding.Entries.All(entry => entry.Component is not null)
                    ? new SavedPick("components", null, null, binding.Access == PortAccess.List)
                    { Ids = ids, Components = binding.Entries.Select(entry => new SavedComponent(entry.Component!.Value.ComponentIndexType.ToString(), entry.Component.Value.Index)).ToArray() }
                    : new SavedPick("objects", null, null, binding.Access == PortAccess.List) { Ids = ids };
                continue;
            }
            if (SavedScalar.Capture(value) is { } scalar) values[name] = scalar;
            else omitted.Add(name);
        }
        return (values, savedPicks, omitted.ToArray());
    }

    private void RestoreWorkspace(WorkspaceRevision revision)
    {
        restoring = true;
        try
        {
            Auto = false;
            autoDue = null;
            scriptStates.Clear();
            foreach (var (entry, state) in revision.Scripts) scriptStates[entry] = state;
            session.Select(revision.Script);
            RestoreScriptState(revision.Scripts[revision.Script]);
        }
        finally { restoring = false; }
    }

    private void RestoreScriptState(SavedScriptState saved)
    {
        numericRanges.Clear(); booleanStyles.Clear(); previewStyles.Clear(); hiddenElements.Clear();
        foreach (var (name, range) in saved.NumericRanges ?? [])
            if (session.Selected?.Ports.Any(port => port.Name == name && !port.IsOutput
                && port.Access == PortAccess.Item && port.TypeName is "System.Double" or "System.Int32") == true)
                numericRanges[name] = range;
        foreach (var (name, style) in saved.BooleanStyles ?? [])
            if (session.Selected?.Ports.Any(port => port.Name == name && !port.IsOutput
                && port.Access == PortAccess.Item && port.TypeName == "System.Boolean") == true)
                booleanStyles[name] = style;
        foreach (var (name, color) in saved.PreviewColors ?? []) previewStyles[name] = new(color, null, null, null);
        foreach (var (name, style) in saved.PreviewStyles ?? []) previewStyles[name] = style;
        frameRate = saved.FrameRate ?? 0;
        lastRun = saved.LastRun;
        historicalRun = lastRun is not null;
        var omitted = new List<string>(saved.OmittedInputs);
        foreach (var (name, scalar) in saved.Inputs)
        {
            var port = session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput);
            if (port is null || !Compatible(port, scalar)) { omitted.Add(name); continue; }
            session.SetInput(name, scalar.Restore());
        }
        foreach (var (name, pick) in saved.Picks)
        {
            var port = session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput);
            if (port is null || (port.Access == PortAccess.List) != pick.IsList)
            { omitted.Add(name); continue; }
            // Free clicked coordinates ("points") are no longer supported; Point3d inputs link Rhino point objects.
            var element = PortTypeName.Element(port.TypeName);
            var subObject = GeometryVocabulary.IsComponentIndex(element);
            if (pick.Kind == "objects" && document is not null && !subObject && GeometryVocabulary.CanPick(element))
            {
                picks[name] = new(pick.Ids!.Select(id => new PickEntry(Guid.Parse(id), null)).ToArray(), port.TypeName, port.Access);
                changedPicks.Add(name);
            }
            else if (pick.Kind == "components" && document is not null && subObject && ParseComponents(pick) is { } parts)
            {
                picks[name] = new(pick.Ids!.Select((id, i) => new PickEntry(Guid.Parse(id), parts[i])).ToArray(), port.TypeName, port.Access);
                changedPicks.Add(name);
            }
            else omitted.Add(name);
        }
        RefreshPickedGeometry();
        foreach (var name in saved.Picks.Keys)
            if (!session.Inputs.ContainsKey(name)) omitted.Add(name);
        hiddenPreview.UnionWith(saved.HiddenPreview);
        if (omitted.Count > 0) restoreNotice = AttentionPrefix + string.Join(", ", omitted.Distinct());
    }

    private static ComponentIndex[]? ParseComponents(SavedPick pick)
    {
        if (pick.Components is not { } saved || pick.Ids is null || saved.Length != pick.Ids.Length) return null;
        var parts = new ComponentIndex[saved.Length];
        for (var i = 0; i < saved.Length; i++)
        {
            if (!Enum.TryParse<ComponentIndexType>(saved[i].Type, out var type) || type == ComponentIndexType.InvalidType) return null;
            parts[i] = new ComponentIndex(type, saved[i].Index);
        }
        return parts;
    }

    private static bool Compatible(PortDescriptor port, SavedScalar value)
    {
        var type = PortTypeName.Element(port.TypeName);
        return (port.Access == PortAccess.List, value.Kind) switch
        {
            (true, "double[]") => type == "System.Double",
            (true, "int[]") => type == "System.Int32",
            (true, "string[]") => type == "System.String",
            (true, "bool[]") => type == "System.Boolean",
            (true, "color[]") => IsColor(type),
            (false, "double") => type == "System.Double",
            (false, "int") => type == "System.Int32",
            (false, "string") => type == "System.String",
            (false, "bool") => type == "System.Boolean",
            (false, "color") => IsColor(type),
            (false, "datetime") => type == "System.DateTime",
            (false, "guid") => type == "System.Guid",
            _ => false
        };
    }

    public void Bake(IEnumerable<string> names)
    {
        Pause();
        if (document is null || RhinoDoc.ActiveDoc?.RuntimeSerialNumber != document.RuntimeSerialNumber)
        { panel?.ShowError("Bake skipped: activate the panel's document first."); return; }
        if (!session.HasCurrentOutputs)
        { panel?.ShowError("Bake skipped: press Run to produce current outputs first."); return; }

        var selected = names.Distinct(StringComparer.Ordinal)
            .Where(CanBake)
            .Select(name => (Name: name, Value: session.Outputs[name]))
            .ToArray();
        if (selected.Length == 0) { panel?.ShowError("Bake skipped: select a geometry output."); return; }

        var undo = document.BeginUndoRecord("rhDock Bake");
        if (undo == 0) { panel?.ShowError("Bake could not start an undo record."); return; }
        var count = 0;
        try
        {
            foreach (var (name, value) in selected)
                foreach (var leaf in Leaves(value).Where(IsBakeable))
                {
                    var id = AddGeometry(document, leaf);
                    if (id == Guid.Empty) throw new InvalidOperationException("Could not Bake " + name + ".");
                    count++;
                }
            panel?.ShowMessage($"Baked {count} object(s). Undo with Rhino Undo.");
        }
        catch (Exception ex) { panel?.ShowError("Bake error: " + ex.Message); }
        finally { document.EndUndoRecord(undo); document.Views.Redraw(); }
    }

    /// <summary>Starts a pick in a registered Rhino command. Modes: pick (new set), add (append), one (replace index), color (sample).</summary>
    public void Pick(string name, string mode, int index)
    {
        if (activePick is not null) return;
        if (mode == "color" ? !CanPreview(name) && PickPort(name) is null && ColorPort(name) is null : PickPort(name) is null) return;
        if (mode is "add" or "one" && !picks.ContainsKey(name)) mode = "pick";
        if (document is null || RhinoDoc.ActiveDoc?.RuntimeSerialNumber != document.RuntimeSerialNumber)
        { panel?.ShowError("Activate the panel's document before picking."); return; }
        if (Command.InCommand()) { panel?.ShowError("Finish the current Rhino command before picking."); return; }
        activePick = new(name, mode, index);
        Changed();
        if (!RhinoApp.RunScript("_RhinoDockPick", false) && activePick is not null)
        {
            activePick = null;
            panel?.ShowError("Rhino could not start the pick command.");
        }
    }

    public void CancelPick()
    {
        if (activePick is null) return;
        if (Command.InCommand() && document is not null) RhinoApp.PostCancelEvent(document.RuntimeSerialNumber);
        else activePick = null;
    }

    internal Result RunPendingPick(RhinoDoc doc)
    {
        var request = activePick;
        if (request is null) return Result.Nothing;
        try
        {
            if (document is null || doc.RuntimeSerialNumber != document.RuntimeSerialNumber) return Result.Failure;
            if (request.Mode == "color") return SampleColor(doc, request.Name, request.Index);
            if (PickPort(request.Name) is not { } port) return Result.Failure;
            var element = PortTypeName.Element(port.TypeName);
            var list = port.Access == PortAccess.List;
            var subObject = GeometryVocabulary.IsComponentIndex(element);
            List<PickEntry> chosen;
            // Sub-objects already selected in Rhino are taken directly, as selected objects are for other ports.
            if (subObject && SelectedSubObjects(doc) is { Count: > 0 } selected) chosen = selected;
            else
            {
                using var picker = new GetObject();
                var noun = subObject ? "sub-objects (Ctrl/Cmd+Shift-click a vertex, edge, or face)" : "objects";
                picker.SetCommandPrompt(request.Mode switch
                {
                    "add" => $"Select {noun} to add to {request.Name}",
                    "one" => $"Select a replacement for {request.Name} element {request.Index}",
                    _ => "Select " + (list ? noun : subObject ? "a sub-object" : "an object") + " for " + request.Name
                });
                picker.GeometryFilter = GeometryVocabulary.PickFilter(element);
                picker.SubObjectSelect = subObject;
                // Selected objects are taken directly. A Sub-object port already took selected sub-objects above,
                // and a selected whole object must not end the prompt before the user reaches a part of it.
                picker.EnablePreSelect(!subObject, true);
                if (list && request.Mode != "one") picker.GetMultiple(1, 0);
                else picker.Get();
                if (picker.CommandResult() != Result.Success) return picker.CommandResult();
                // A whole-object pick carries no component; a Sub-object port ignores it.
                chosen = (subObject
                    ? picker.Objects().Where(reference => reference.GeometryComponentIndex.ComponentIndexType != ComponentIndexType.InvalidType)
                        .Select(reference => new PickEntry(reference.ObjectId, reference.GeometryComponentIndex)).Concat(SelectedSubObjects(doc))
                    : picker.Objects().Select(reference => new PickEntry(reference.ObjectId, null))).Distinct().ToList();
            }
            doc.Objects.UnselectAll();
            if (chosen.Count == 0)
            { panel?.ShowError("No sub-objects selected: Ctrl/Cmd+Shift-click a vertex, edge, or face, or select them in Rhino first."); return Result.Failure; }
            var current = picks.TryGetValue(request.Name, out var binding) ? binding.Entries.ToList() : [];
            var entries = request.Mode switch
            {
                "add" => current.Concat(chosen.Where(entry => !current.Contains(entry))).ToList(),
                "one" when request.Index >= 0 && request.Index < current.Count => current.Select((entry, i) => i == request.Index ? chosen[0] : entry).ToList(),
                _ => chosen
            };
            if (!list) entries = entries.Take(1).ToList();
            return SetPickEntries(port, entries) ? Result.Success : Result.Failure;
        }
        finally { activePick = null; }
    }

    /// <summary>The sub-objects (vertices, edges, faces) currently selected in Rhino, on any object that has them.</summary>
    private static List<PickEntry> SelectedSubObjects(RhinoDoc doc)
    {
        var found = new List<PickEntry>();
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true, LockedObjects = false, HiddenObjects = false, IncludeLights = false, IncludeGrips = false,
            ObjectTypeFilter = ObjectType.SubD | ObjectType.Mesh | ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion
        };
        foreach (var rhinoObject in doc.Objects.GetObjectList(settings))
            if (rhinoObject.IsSelected(true) != 0)
                foreach (var part in rhinoObject.GetSelectedSubObjects() ?? [])
                    if (part.ComponentIndexType != ComponentIndexType.InvalidType) found.Add(new(rhinoObject.Id, part));
        return found;
    }

    public void ReorderPicked(string name, int from, int to)
    {
        if (PickPort(name) is not { } port || !picks.TryGetValue(name, out var binding)
            || from < 0 || to < 0 || from >= binding.Entries.Length || to >= binding.Entries.Length || from == to) return;
        var entries = binding.Entries.ToList();
        var moved = entries[from];
        entries.RemoveAt(from);
        entries.Insert(to, moved);
        SetPickEntries(port, entries);
    }

    private const string AttentionPrefix = "Inputs needing attention: ";

    /// <summary>Removes an input from the restore notice once the user sets it again.</summary>
    private void ClearAttention(string name)
    {
        if (restoreNotice?.StartsWith(AttentionPrefix, StringComparison.Ordinal) != true) return;
        var names = restoreNotice[AttentionPrefix.Length..].Split(", ").Where(item => item != name).ToArray();
        restoreNotice = names.Length == 0 ? null : AttentionPrefix + string.Join(", ", names);
    }

    /// <summary>Selects picked objects, or for Sub-object picks the picked parts only, through the hidden RhinoDockSelect command so the Gumball and selection display update.</summary>
    public void SelectPicked(string name, int? index)
    {
        if (document is null || !picks.TryGetValue(name, out var binding)) return;
        var entries = binding.Entries;
        var chosen = index is { } i && i >= 0 && i < entries.Length ? new[] { entries[i] } : entries;
        if (Command.InCommand()) { panel?.ShowError("Finish the current Rhino command before selecting."); return; }
        pendingSelect = chosen;
        if (!RhinoApp.RunScript("_RhinoDockSelect", false) && pendingSelect is not null)
        {
            pendingSelect = null;
            panel?.ShowError("Rhino could not start the select command.");
        }
    }

    internal Result RunPendingSelect(RhinoDoc doc)
    {
        var entries = pendingSelect;
        pendingSelect = null;
        if (entries is null || document is null || doc.RuntimeSerialNumber != document.RuntimeSerialNumber) return Result.Nothing;
        doc.Objects.UnselectAll();
        var selected = 0;
        foreach (var entry in entries)
        {
            if (doc.Objects.FindId(entry.Id) is not { } rhinoObject) continue;
            if (entry.Component is not { } part) { if (doc.Objects.Select(entry.Id, true, true, true)) selected++; continue; }
            // Select and highlight only the picked part. Selecting through an ObjRef would select the whole object.
            rhinoObject.SelectSubObject(part, true, true, true);
            rhinoObject.HighlightSubObject(part, true);
            if (rhinoObject.IsSubObjectSelected(part)) selected++;
            else panel?.ShowError("Rhino could not select " + GeometryVocabulary.DescribeComponent(part) + "; the geometry may have changed, so pick again.");
        }
        doc.Views.Redraw();
        RhinoApp.SetFocusToMainWindow(doc);
        return selected > 0 ? Result.Success : Result.Nothing;
    }

    public void SetElementVisible(string name, int index, bool visible)
    {
        if (!picks.TryGetValue(name, out var binding) || index < 0 || index >= binding.Entries.Length) return;
        if (!hiddenElements.TryGetValue(name, out var hidden)) hiddenElements[name] = hidden = [];
        var entry = binding.Entries[index];
        if (visible) hidden.Remove(entry); else hidden.Add(entry);
        RebuildPreview();
    }

    private Result SampleColor(RhinoDoc doc, string name, int index)
    {
        using var picker = new GetObject();
        picker.SetCommandPrompt("Select an object to sample its display color");
        picker.SubObjectSelect = false;
        picker.EnablePreSelect(true, true);
        picker.Get();
        if (picker.CommandResult() != Result.Success) return picker.CommandResult();
        var sampled = picker.Object(0).Object()?.Attributes.DrawColor(doc);
        doc.Objects.UnselectAll();
        if (sampled is not { } color) return Result.Failure;
        if (ColorPort(name) is { } port)
        {
            var value = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
            if (port.Access == PortAccess.Item) SetInput(name, value);
            else
            {
                // A list sample replaces the element at index, or appends when index is past the end.
                var colors = (session.Inputs.GetValueOrDefault(name) as IEnumerable<System.Drawing.Color>)?.ToList() ?? [];
                if (index >= 0 && index < colors.Count) colors[index] = value; else colors.Add(value);
                SetInput(name, colors.ToArray());
            }
            return Result.Success;
        }
        var style = previewStyles.GetValueOrDefault(name) ?? new(null, null, null, null);
        SetPreviewStyle(name, style with { Color = System.Drawing.ColorTranslator.ToHtml(System.Drawing.Color.FromArgb(color.R, color.G, color.B)) });
        return Result.Success;
    }

    /// <summary>Links an input to Rhino objects, or to parts of them for a Sub-object port, in the given order.</summary>
    private bool SetPickEntries(PortDescriptor port, List<PickEntry> entries)
    {
        if (entries.Count == 0) { ApplyInput(port.Name, null, null); return true; }
        var element = PortTypeName.Element(port.TypeName);
        if (ExtractPicks(entries, element) is not { } values)
        { panel?.ShowError("A picked object is unavailable for " + port.Name + "; pick again."); return false; }
        ApplyInput(port.Name, GeometryVocabulary.PickValue(values, element, port.Access == PortAccess.List),
            new(entries.ToArray(), port.TypeName, port.Access));
        return true;
    }

    /// <summary>Fresh host-owned values for picked objects. Sub-object picks keep their stored ComponentIndex once the object is confirmed present.</summary>
    private List<object>? ExtractPicks(IEnumerable<PickEntry> entries, string element)
    {
        var values = new List<object>();
        foreach (var entry in entries)
        {
            if (document?.Objects.FindId(entry.Id) is not { } rhinoObject) { DisposePickedValue(values); return null; }
            if (entry.Component is { } part) { values.Add(part); continue; }
            using var reference = new ObjRef(rhinoObject);
            if (GeometryVocabulary.ExtractPick(reference, element) is not { } value) { DisposePickedValue(values); return null; }
            values.Add(value);
        }
        return values;
    }

    public void OnSourceChanged() => RhinoApp.InvokeOnUiThread((Action)(() =>
    {
        if (disposed) return;
        try
        {
            var held = picks.Keys.ToDictionary(name => name, name => session.Inputs.GetValueOrDefault(name));
            Halt();
            stalled = false;
            session.Refresh();
            revision++;
            ReconcileControlSettings();
            if (session.Result.Status == "compile_error" && session.Selected is not null)
            {
                var captured = CaptureInputs();
                lastRun = SingleScriptWorkspaceStore.CaptureRun(session, captured.Inputs, captured.Picks);
                historicalRun = false;
            }
            foreach (var (name, pick) in picks.ToArray())
                if (!session.Inputs.ContainsKey(name) || session.Selected?.Ports.FirstOrDefault(port => port.Name == name && !port.IsOutput) is not { } current
                    || current.TypeName != pick.TypeName || current.Access != pick.Access)
                {
                    picks.Remove(name);
                    changedPicks.Remove(name);
                    DisposePickedValue(held[name]);
                }
            RebuildPreview();
            Changed(session: true);
            ScheduleAuto();
            ScheduleSave();
        }
        catch (Exception ex) { panel?.ShowError(ex.Message); }
    }));

    public void OnEntryRenamed(string oldRel, string newRel) { }

    private void ReconcileControlSettings()
    {
        var ports = session.Selected?.Ports ?? [];
        foreach (var name in numericRanges.Keys.Where(name => !ports.Any(port => port.Name == name && !port.IsOutput
            && port.Access == PortAccess.Item && port.TypeName is "System.Double" or "System.Int32")).ToArray()) numericRanges.Remove(name);
        foreach (var name in booleanStyles.Keys.Where(name => !ports.Any(port => port.Name == name && !port.IsOutput
            && port.Access == PortAccess.Item && port.TypeName == "System.Boolean")).ToArray()) booleanStyles.Remove(name);
        foreach (var name in previewStyles.Keys.Where(name => !ports.Any(port => port.Name == name)).ToArray()) previewStyles.Remove(name);
    }

    private void ClearPreview(bool redraw = true)
    {
        var previous = conduit.Preview;
        conduit.Preview = Preview.Empty;
        previous.Dispose();
        if (redraw) document?.Views.Redraw();
    }

    private void ClearPickedInputs()
    {
        foreach (var name in picks.Keys.ToArray())
        {
            var old = session.Inputs.GetValueOrDefault(name);
            if (session.Selected?.Ports.Any(port => port.Name == name && !port.IsOutput) == true)
                session.SetInput(name, null);
            DisposePickedValue(old);
        }
        picks.Clear();
        changedPicks.Clear();
    }

    private static void DisposePickedValue(object? value)
    {
        if (value is GeometryBase geometry) geometry.Dispose();
        else if (value is IEnumerable sequence)
            foreach (var item in sequence.OfType<GeometryBase>()) item.Dispose();
    }

    /// <summary>Draws visible picked inputs and, when current, visible outputs, each with its Preview Style.</summary>
    private void RebuildPreview()
    {
        var items = new List<PreviewItem>();
        foreach (var (name, pick) in picks)
        {
            if (hiddenPreview.Contains(name) || !session.Inputs.TryGetValue(name, out var value)) continue;
            hiddenElements.TryGetValue(name, out var hidden);
            var family = GeometryVocabulary.FamilyOf(PortTypeName.Element(pick.TypeName));
            var leaves = Leaves(value).ToArray();
            for (var i = 0; i < leaves.Length && i < pick.Entries.Length; i++)
            {
                var entry = pick.Entries[i];
                if (hidden?.Contains(entry) == true) continue;
                if (entry.Component is { } part)
                {
                    // A Sub-object is drawn from its object's current geometry, so a stale index shows the wrong part or nothing.
                    var shape = SubObjectGeometry.Resolve(document?.Objects.FindId(entry.Id)?.Geometry, part);
                    if (shape is not null) AddPreview(items, shape, ResolvedStyle(name, GeometryVocabulary.Family(shape), true), entry.Id, part: true);
                }
                else AddPreview(items, leaves[i], ResolvedStyle(name, family, true), entry.Id);
            }
        }
        if (session.HasCurrentOutputs)
            foreach (var (name, value) in session.Outputs)
            {
                if (hiddenPreview.Contains(name)) continue;
                var family = OutputFamily(name);
                foreach (var leaf in Leaves(value))
                    AddPreview(items, leaf, ResolvedStyle(name, family == "mixed" ? GeometryVocabulary.Family(leaf) : family, false));
            }
        var previous = conduit.Preview;
        conduit.Preview = new(items);
        previous.Dispose();
        document?.Views.Redraw();
    }

    private static void AddPreview(List<PreviewItem> items, object leaf, (string Color, bool Custom, int Weight, int Size, int Shading) style, Guid source = default, bool part = false)
    {
        var color = System.Drawing.ColorTranslator.FromHtml(style.Color);
        object? geometry = leaf switch
        {
            Mesh mesh => mesh.DuplicateMesh(),
            Surface surface => surface.ToBrep(),
            global::Rhino.Geometry.Point point => point.Location,
            Line line => new LineCurve(line),
            Polyline polyline => new PolylineCurve(polyline),
            GeometryBase geometryBase => geometryBase.Duplicate(),
            Point3d point => point,
            _ => leaf.GetType().GetMethod("toRhinoMesh", Type.EmptyTypes)?.Invoke(leaf, null) is Mesh converted ? converted.DuplicateMesh() : null
        };
        if (geometry is not null) items.Add(new(geometry, color, style.Weight, style.Size, style.Shading, source, style.Custom, part));
    }

    private static IEnumerable<object> Leaves(object? value)
    {
        if (value is null) yield break;
        if (value is IEnumerable sequence && value is not string && value is not GeometryBase && value is not Polyline)
        {
            foreach (var item in sequence)
                foreach (var leaf in Leaves(item)) yield return leaf;
        }
        else yield return value;
    }

    private static bool IsBakeable(object leaf) => leaf is Mesh or Brep or Surface or Curve or SubD or TextDot
        or global::Rhino.Geometry.Point or Point3d or Line or Polyline
        || leaf.GetType().GetMethod("toRhinoMesh", Type.EmptyTypes)?.ReturnType == typeof(Mesh);

    private static bool IsPreviewable(object leaf) => leaf is GeometryBase or Point3d or Line or Polyline
        || leaf.GetType().GetMethod("toRhinoMesh", Type.EmptyTypes)?.ReturnType == typeof(Mesh);

    private static Guid AddGeometry(RhinoDoc doc, object leaf) => leaf switch
    {
        Mesh mesh => doc.Objects.AddMesh(mesh),
        Surface surface => AddSurface(doc, surface),
        Brep brep => doc.Objects.AddBrep(brep),
        Curve curve => doc.Objects.AddCurve(curve),
        SubD subd => doc.Objects.AddSubD(subd),
        TextDot dot => doc.Objects.AddTextDot(dot),
        global::Rhino.Geometry.Point point => doc.Objects.AddPoint(point.Location),
        Point3d point => doc.Objects.AddPoint(point),
        Line line => doc.Objects.AddLine(line),
        Polyline polyline => doc.Objects.AddPolyline(polyline),
        _ => AddConvertedMesh(doc, leaf)
    };

    private static Guid AddSurface(RhinoDoc doc, Surface surface)
    {
        using var brep = surface.ToBrep();
        return doc.Objects.AddBrep(brep);
    }

    private static Guid AddConvertedMesh(RhinoDoc doc, object value)
    {
        if (value.GetType().GetMethod("toRhinoMesh", Type.EmptyTypes)?.Invoke(value, null) is not Mesh mesh) return Guid.Empty;
        using (mesh) return doc.Objects.AddMesh(mesh);
    }

    public void Dispose()
    {
        if (disposed) return;
        SaveNow();
        disposed = true;
        Halt();
        panel?.StopMirror();
        RhinoApp.Idle -= OnIdle;
        RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
        RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
        RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
        RhinoDoc.AddRhinoObject -= OnObjectChanged;
        RhinoDoc.CloseDocument -= OnCloseDocument;
        autoDue = null;
        if (global::Rhino.UI.Panels.IsPanelVisible(typeof(SingleScriptPanel)))
            global::Rhino.UI.Panels.ClosePanel(typeof(SingleScriptPanel));
        ClearPreview();
        ClearPickedInputs();
        conduit.Enabled = false;
        watcher.Dispose();
        session.Dispose();
        compiler.Dispose();
    }

    /// <summary>Picked objects in Script order; a Sub-object pick also names the part of its object (ADR 0006).</summary>
    private sealed record PickBinding(PickEntry[] Entries, string TypeName, PortAccess Access);

    private readonly record struct PickEntry(Guid Id, ComponentIndex? Component);
}

internal sealed record PickRequest(string Name, string Mode, int Index);
internal sealed record SolveState(bool Playing, int Frame, bool Done, bool Stalled, double Rate, int? FramesPerSecond, bool Held);
internal sealed record PickedElement(string Label, bool Visible);
internal sealed record PickedList(string Label, PickedElement[] Items);
