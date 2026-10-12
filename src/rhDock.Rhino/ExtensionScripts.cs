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
    private readonly Dictionary<string, Action<string>> editors = new(StringComparer.Ordinal);
    private bool retired;

    internal ExtensionScripts(ExtensionContext owner) => this.owner = owner;

    private static SingleScriptHost Host => SingleScriptHost.Current;

    /// <summary>Raised on the UI queue after the Session changed: Script list, selection, inputs, a Run, or its result.</summary>
    public event Action? Changed;

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
        editors[type.FullName!] = edit;
        Host.RefreshPanel();
    }

    internal bool Edits(string typeName) => editors.ContainsKey(typeName);

    internal void Edit(string typeName, string input)
    {
        if (editors.TryGetValue(typeName, out var edit)) owner.Guard("input editor", () => edit(input));
    }

    internal bool Wanted => Changed is not null;
    internal void Raise() => owner.Guard("Scripts.Changed", () => Changed?.Invoke());

    internal void Retire()
    {
        retired = true;
        Changed = null;
        editors.Clear();
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
