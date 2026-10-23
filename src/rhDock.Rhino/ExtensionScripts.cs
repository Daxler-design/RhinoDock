using Rhino.Geometry;
using rhDock.Core;
using rhDock.Rhino;

namespace rhDock.Extensions;

/// <summary>
/// One input or output of a Script. <c>Kind</c> says how the panel edits an input: number, int, text, boolean, list,
/// color, pick, extension (an enabled Extension's editor), or unsupported.
/// </summary>
public sealed record ScriptPort(string Name, string Type, string Kind, bool IsOutput, bool IsOptional, string? Tooltip);

/// <summary>A Script in the open Working Repo. <c>Path</c> is its entry file relative to the repo, as the panel uses it.</summary>
public sealed record ScriptInfo(string Path, string Name, string Category, IReadOnlyList<ScriptPort> Ports);

/// <summary>
/// The panel's Script Session, for an Extension. Calls take the same path as the panel, so the panel, the Workspace,
/// and Preview follow them. Values cross as .NET or RhinoCommon types only, and geometry is copied both ways.
/// Call from the Rhino UI thread, for example from a view handler.
/// </summary>
public sealed class ExtensionScripts
{
    private readonly ExtensionContext owner;
    private readonly Dictionary<string, Action<string, int?>> editors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Kind, Func<object?, string> Format, Func<string, object?> Parse)> inlineEditors = new(StringComparer.Ordinal);
    private readonly HashSet<string> listEditors = new(StringComparer.Ordinal);
    private bool retired;

    internal ExtensionScripts(ExtensionContext owner) => this.owner = owner;

    private static SingleScriptHost Host => SingleScriptHost.Current;

    /// <summary>Raised on the UI queue after the Session changed: Script list, selection, inputs, a Run, or its result.</summary>
    public event Action? Changed;

    /// <summary>Raised synchronously after an explicit list move; matching other inputs is the Extension's choice.</summary>
    public event Action<ScriptInputReorder>? Reordered;

    /// <summary>The input row active in the panel or viewport; null when no row is active.</summary>
    public ScriptInputItem? ActiveItem => Host.ActiveItem;

    /// <summary>False after document closure or while another document is active.</summary>
    public bool HasDocument => Host.HasDocument;

    /// <summary>Changes when the repository, selected Script, or compiled source generation changes.</summary>
    public int Revision => Host.Revision;

    /// <summary>Activates a row without selecting geometry. Null clears the active row.</summary>
    public void SetActiveItem(string? name, int? index = null) { Check(); Host.SetActiveItem(name, index); }

    /// <summary>Edits one element through SetInput, retaining the whole typed list and adaptive Auto scheduling.</summary>
    public void SetInputItem(string name, int index, object? value)
    {
        Check();
        if (Host.Session.Selected?.Ports.Any(p => p.Name == name && !p.IsOutput && p.Access == PortAccess.List) != true
            || Input(name) is not System.Collections.IEnumerable items || items is string)
            throw new ArgumentException("No list input named " + name, nameof(name));
        if (!BoundaryValues.TryCopy(value, Duplicate, out var copy))
            throw new ArgumentException("Extension and Script types cannot cross.", nameof(value));
        SetInput(name, InputItems.Replace(items, index, copy));
    }

    /// <summary>Moves an input list row and raises Reordered. No other input is matched automatically.</summary>
    public void ReorderInput(string name, int from, int to) { Check(); Host.ReorderInput(name, from, to); Host.Refresh(); }

    /// <summary>The open Working Repo, or null.</summary>
    public string? Repository => Host.Session.Repository;

    public IReadOnlyList<ScriptInfo> List() => Host.Session.Scripts.Select(Info).ToArray();

    public ScriptInfo? Selected => Host.Session.Selected is { } selected ? Info(selected) : null;

    /// <summary>pending, running, ok, error, or compile_error.</summary>
    public string Status => Host.Running ? "running" : Host.Session.Dirty ? "pending" : Host.Session.Result.Status;

    public string? Error => Host.Session.Result.Error;

    /// <summary>Makes the Script with this entry path the active one, as choosing it in the panel does.</summary>
    public void Select(string path)
    {
        Check();
        if (Host.Session.Scripts.All(script => script.EntryFile != path)) throw new ArgumentException($"The Working Repo has no Script at {path}.", nameof(path));
        Host.Select(path);
    }

    /// <summary>A copy of the active Script's input value, or null.</summary>
    public object? Input(string name) => Copy(Host.Session.Inputs.GetValueOrDefault(name));

    /// <summary>
    /// Sets an input of the active Script. The value must fit what the panel accepts for that port: a double, int,
    /// string, bool, Color, an array of these for list inputs, RhinoCommon geometry or Point3d for pick inputs, or the
    /// type an Extension editor handles. Null clears the input.
    /// </summary>
    public void SetInput(string name, object? value)
    {
        Check();
        var port = Host.Session.Selected?.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput)
            ?? throw new ArgumentException($"The active Script has no input named {name}.", nameof(name));
        if (SingleScriptHost.PortKind(port) == "unsupported")
            throw new InvalidOperationException($"rhDock cannot set {name} ({port.TypeName}): the panel does not support this type and no enabled Extension edits it.");
        if (!BoundaryValues.TryCopy(value, Duplicate, out var copy))
            throw new ArgumentException($"{name} takes .NET or RhinoCommon values; Extension and Script types cannot cross.", nameof(value));
        Host.SetInput(name, copy is null ? null : Fit(port, copy));
        Host.Refresh();
    }

    /// <summary>Runs the active Script, as Run in the panel does.</summary>
    public void Run()
    {
        Check();
        Host.Run();
        Host.Refresh();
    }

    /// <summary>
    /// A copy of an output from the latest current Run, or null when there is none or its type is defined by the Script.
    /// </summary>
    public object? Output(string name) => Host.Session.HasCurrentOutputs ? Copy(Host.Session.Outputs.GetValueOrDefault(name)) : null;

    /// <summary>
    /// Lets the panel edit inputs of <paramref name="type"/> through this Extension: the panel shows an Edit button, and
    /// <paramref name="edit"/> receives the input name, typically opens a view, and calls <see cref="SetInput"/>.
    /// The type must come from .NET or RhinoCommon. The registration ends when this generation is retired.
    /// </summary>
    public void RegisterEditor(Type type, Action<string> edit)
    {
        Check();
        if (!BoundaryValues.CanCross(type))
            throw new ArgumentException("Editors handle .NET or RhinoCommon types; a Script cannot see Extension types.", nameof(type));
        editors[type.FullName!] = (name, _) => edit(name);
        inlineEditors.Remove(type.FullName!);
        listEditors.Remove(type.FullName!);
        Host.RefreshPanel();
    }

    /// <summary>An Edit action for a single value (null index) or one list element. Use SetInputItem for list edits.</summary>
    public void RegisterEditor(Type type, Action<string, int?> edit)
    {
        Check();
        if (!BoundaryValues.CanCross(type)) throw new ArgumentException("Extension and Script types cannot be edited.", nameof(type));
        editors[type.FullName!] = edit;
        inlineEditors.Remove(type.FullName!);
        listEditors.Add(type.FullName!);
        Host.RefreshPanel();
    }

    /// <summary>
    /// Embeds a host control (number, int, text, boolean, or color) for single values and list elements.
    /// Format and parse use invariant text; parsed values must match the registered .NET or RhinoCommon type.
    /// No Extension objects or callbacks are saved. Retirement restores the ordinary panel control.
    /// </summary>
    public void RegisterEditor(Type type, string control, Func<object?, string> format, Func<string, object?> parse)
    {
        Check();
        if (!BoundaryValues.CanCross(type)) throw new ArgumentException("Extension and Script types cannot be edited.", nameof(type));
        if (control is not ("number" or "int" or "text" or "boolean" or "color")) throw new ArgumentException("Unknown input control.", nameof(control));
        inlineEditors[type.FullName!] = (control, format, parse);
        editors.Remove(type.FullName!);
        listEditors.Add(type.FullName!);
        Host.RefreshPanel();
    }

    internal bool Edits(string typeName, bool list = false) => (!list || listEditors.Contains(typeName)) && (editors.ContainsKey(typeName) || inlineEditors.ContainsKey(typeName));
    internal string? Control(string typeName) => inlineEditors.TryGetValue(typeName, out var editor) ? editor.Kind : null;
    internal string? Format(string typeName, object? value)
    {
        string? text = null;
        if (inlineEditors.TryGetValue(typeName, out var editor)) owner.Guard("input format", () => text = editor.Format(value));
        return text;
    }

    internal void Parse(string typeName, string input, int? index, string text)
    {
        if (inlineEditors.TryGetValue(typeName, out var editor)) owner.Guard("input parse", () =>
        {
            var value = editor.Parse(text);
            if (index is { } i) SetInputItem(input, i, value); else SetInput(input, value);
        });
    }

    internal void ParseList(string typeName, string input, string[] text)
    {
        if (inlineEditors.TryGetValue(typeName, out var editor))
            owner.Guard("input parse", () => SetInput(input, text.Select(editor.Parse).ToArray()));
    }

    internal void Edit(string typeName, string input, int? index)
    {
        if (editors.TryGetValue(typeName, out var edit)) owner.Guard("input editor", () => edit(input, index));
    }

    internal void RaiseReordered(ScriptInputReorder move) => owner.Guard("Scripts.Reordered", () => Reordered?.Invoke(move));

    internal bool Wanted => Changed is not null;
    internal void Raise() => owner.Guard("Scripts.Changed", () => Changed?.Invoke());

    internal void Retire()
    {
        retired = true;
        Changed = null;
        Reordered = null;
        editors.Clear();
        inlineEditors.Clear();
        listEditors.Clear();
    }

    private void Check() => ObjectDisposedException.ThrowIf(retired, this);

    private static ScriptInfo Info(OperationInfo script) => new(script.EntryFile, script.Name, script.Category,
        script.Ports.Select(p => new ScriptPort(p.Name, p.TypeName, p.IsOutput ? "output" : SingleScriptHost.PortKind(p), p.IsOutput, p.IsOptional, p.Tooltip)).ToArray());

    private static object? Copy(object? value) => BoundaryValues.TryCopy(value, Duplicate, out var copy) ? copy : null;

    private static object? Duplicate(object value) => value switch
    {
        GeometryBase geometry => geometry.Duplicate(),
        Polyline polyline => new Polyline(polyline),
        _ => null,
    };

    // The same types the panel produces for each kind; an int is accepted where a double is expected.
    private static object Fit(PortDescriptor port, object value)
    {
        var element = PortTypeName.Element(port.TypeName);
        var geometry = GeometryVocabulary.Resolve(element);
        bool Fits(object? item) => item is not null && (item.GetType().FullName == element || geometry?.IsInstanceOfType(item) == true);
        if (port.Access == PortAccess.Item)
        {
            if (value is int number && element == "System.Double") return (double)number;
            if (Fits(value)) return value;
        }
        else if (value is Array items && items.Cast<object?>().All(Fits)) return value;
        throw new ArgumentException($"{port.Name} takes {(port.Access == PortAccess.Item ? element : "an array of " + element)}, not {value.GetType().FullName}.");
    }
}
