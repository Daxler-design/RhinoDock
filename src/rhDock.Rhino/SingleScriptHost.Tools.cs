using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using Rhino;
using Rhino.Geometry;
using rhDock.Core;
using rhDock.RhinoAI.Server;
using rhDock.RhinoAI.Tools;
using static rhDock.RhinoAI.Tools.ToolResult;

namespace rhDock.Rhino;

internal sealed partial class SingleScriptHost
{
    internal RhinoDoc? BoundDocument => document;

    internal object ToolState() => new
    {
        pid = Environment.ProcessId, document = document?.RuntimeSerialNumber, repository = session.Repository,
        revision, script = session.Selected?.EntryFile, auto = Auto, running, solve = Solve,
        status = session.Dirty ? "pending" : session.Result.Status, error = session.Result.Error,
        diagnostics = session.Result.Log.Take(50).ToArray(),
        inputs = session.Inputs.Select(pair => new { name = pair.Key, value = Summarize(pair.Value), picks = picks.TryGetValue(pair.Key, out var binding)
            ? binding.Entries.Select(e => new { id = e.Id, component = e.Component is { } c ? new SavedComponent(c.ComponentIndexType.ToString(), c.Index) : null }).ToArray() : null }),
        ports = session.Selected?.Ports.Select(p => new { p.Name, type = p.TypeName, access = p.Access.ToString(), p.IsOutput, p.IsOptional, p.DefaultValue, p.EnumValues }),
        outputs = ToolOutputs(), previewAll = PreviewAll, inputPreviewAll = InputPreviewAll, outputPreviewAll = OutputPreviewAll, layout = CurrentLayout(), historical = historicalRun
    };

    internal object ToolOutputs() => new
    {
        current = session.HasCurrentOutputs,
        outputs = session.HasCurrentOutputs ? session.Outputs.Select(pair => new { name = pair.Key, value = Summarize(pair.Value), preview = PreviewVisible(pair.Key), canBake = CanBake(pair.Key) }).ToArray() : []
    };

    private static object Summarize(object? value)
    {
        static object? Scalar(object? item) => item switch
        {
            null => null, string text => text.Length > 2000 ? text[..2000] + "..." : text,
            bool or int => item, double number when double.IsFinite(number) => number,
            float number when float.IsFinite(number) => number,
            DateTime date => date.ToString("O"), Guid id => id.ToString(),
            System.Drawing.Color color => ColorText.Format(color),
            _ => GeometryVocabulary.Describe(item)
        };
        if (value is ICollection collection) return new { type = value.GetType().FullName, count = collection.Count, items = collection.Cast<object?>().Take(200).Select(Scalar).ToArray(), truncated = collection.Count > 200 };
        return new { type = value?.GetType().FullName, value = Scalar(value) };
    }

    internal void CheckToolRevision(int expected)
    {
        if (revision != expected) throw new InvalidOperationException("The Script changed. Read rhdock_state before requesting another mutation.");
        if (session.Selected is null) throw new InvalidOperationException("Select a Script first.");
    }

    internal void SetToolPicks(string name, string[] ids, SavedComponent[]? components)
    {
        var port = session.Selected?.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput)
            ?? throw new ArgumentException("Input not found: " + name);
        if (PortKind(port) != "pick") throw new ArgumentException("This input does not accept geometry picks.");
        if (components is not null && components.Length != ids.Length) throw new ArgumentException("Components must match the object IDs.");
        if (ids.Length > 0 && GeometryVocabulary.IsComponentIndex(PortTypeName.Element(port.TypeName)) != (components is not null))
            throw new ArgumentException("Supply components for sub-object Ports only.");
        var entries = ids.Select((id, i) =>
        {
            if (!Guid.TryParse(id, out var guid)) throw new ArgumentException("Invalid object GUID: " + id);
            ComponentIndex? part = null;
            if (components is not null)
            {
                if (components[i] is null || !Enum.TryParse<ComponentIndexType>(components[i].Type, out var type) || !Enum.IsDefined(type) || components[i].Index < 0) throw new ArgumentException("Invalid sub-object component.");
                part = new ComponentIndex(type, components[i].Index);
                var shape = SubObjectGeometry.Resolve(document?.Objects.FindId(guid)?.Geometry, part.Value)
                    ?? throw new ArgumentException("The sub-object does not exist in this document.");
                (shape as IDisposable)?.Dispose();
            }
            return new PickEntry(guid, part);
        }).ToList();
        if (ids.Length == 0) { SetInput(name, null); return; }
        if (port.Access == PortAccess.Item && ids.Length != 1) throw new ArgumentException("An item input requires exactly one object.");
        if (!SetPickEntries(port, entries)) throw new ArgumentException("A picked object is missing or incompatible with this input.");
    }
}

[McpServerToolType]
internal static class ScriptHostTools
{
    private static SingleScriptHost Host => SingleScriptHost.Current;

    [McpServerTool("rhdock_state", "Read rhDock State", true, false)]
    [Description("Current Script, revision, inputs, picks, Run diagnostics and bounded output summaries. Read before a mutation; use the returned revision.")]
    public static IToolResult State(RhinoDoc doc) => Success(Host.ToolState());

    [McpServerTool("rhdock_scripts", "List Scripts", true, false)]
    [Description("List discoverable Scripts and their declared Ports in the Loaded Repository.")]
    public static IToolResult Scripts(RhinoDoc doc) => Success(Host.Session.Scripts.Select(s => new { path = s.EntryFile, s.Name, s.Category, s.IsSolver, ports = s.Ports.Select(p => new { p.Name, type = p.TypeName, access = p.Access.ToString(), p.IsOutput, p.IsOptional }) }).ToArray());

    [McpServerTool("rhdock_select_script", "Select Script", false, false)]
    [Description("Select one Script by repository-relative path. Clears stale outputs and returns the new revision.")]
    public static IToolResult Select(RhinoDoc doc, string path, int revision)
    {
        if (Host.Revision != revision) throw new InvalidOperationException("Read fresh rhdock_state first.");
        if (!Host.Session.Scripts.Any(s => s.EntryFile == path)) throw new ArgumentException("Script not found: " + path);
        Host.Select(path);
        return State(doc);
    }

    [McpServerTool("rhdock_set_input", "Set Script Input", false, false)]
    [Description("Set a scalar or list from JSON; null clears. Types must match the Port. Geometry uses rhdock_set_picks. Auto retains its existing user-selected behavior.")]
    public static IToolResult SetInput(RhinoDoc doc, string name, JsonElement value, int revision)
    {
        Host.CheckToolRevision(revision);
        var port = Host.Session.Selected!.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput) ?? throw new ArgumentException("Unknown input: " + name);
        Host.SetInput(name, HostInput.Parse(port.EnumValues.Length > 0 ? "System.String" : PortTypeName.Element(port.TypeName), port.Access, value));
        return State(doc);
    }

    [McpServerTool("rhdock_set_picks", "Bind Rhino Objects", false, false)]
    [Description("Bind ordered existing Rhino object GUIDs to an input. Optional parallel components contain Type (ComponentIndexType name) and Index for sub-object Ports. Empty IDs clear the input.")]
    public static IToolResult SetPicks(RhinoDoc doc, string name, string[] ids, int revision, SavedComponent[]? components = null)
    {
        Host.CheckToolRevision(revision); Host.SetToolPicks(name, ids, components); return State(doc);
    }

    [McpServerTool("rhdock_run", "Run Script Once", false, true)]
    [Description("Run the active Script at most once; for a Solver Script, pause and execute one Frame. Returns actual status and diagnostics, not build-only success. Executes repository code.")]
    public static IToolResult Run(RhinoDoc doc, int revision)
    {
        Host.CheckToolRevision(revision);
        Host.Pause();
        if (Host.Session.IsSolver) Host.Step(); else Host.Run();
        return !Host.Session.HasCurrentOutputs
            ? Failure(ToolError.Failed, ContentBlock.CreateJson(Host.ToolState()), Host.Session.Result.Error)
            : State(doc);
    }

    [McpServerTool("rhdock_reload", "Reload Script Sources", false, true)]
    [Description("Compile/reload saved sources through the normal host watcher path and invalidate old results. The next call must read rhdock_state to get the new revision. May execute Script initialization.")]
    public static IToolResult Reload(RhinoDoc doc, int revision)
    {
        if (Host.Revision != revision) throw new InvalidOperationException("Read fresh rhdock_state first.");
        Host.ReloadForTools();
        return Host.Session.Result.Status == "compile_error"
            ? Failure(ToolError.Failed, ContentBlock.CreateJson(Host.ToolState()), Host.Session.Result.Error) : State(doc);
    }

    [McpServerTool("rhdock_outputs", "Read Current Outputs", true, false)]
    [Description("Read bounded primitive/geometry summaries from the current successful Run only. Never returns live Script-owned objects.")]
    public static IToolResult Outputs(RhinoDoc doc) => Success(Host.ToolOutputs());

    [McpServerTool("rhdock_preview", "Set Preview Visibility", false, false)]
    [Description("Set Preview visibility for an existing geometry input or current output.")]
    public static IToolResult Preview(RhinoDoc doc, string name, bool visible, int revision)
    {
        Host.CheckToolRevision(revision);
        if (!Host.CanPreview(name) && Host.Picked(name) is null) throw new ArgumentException("No previewable value: " + name);
        Host.SetPreviewVisible(name, visible); return State(doc);
    }

    [McpServerTool("rhdock_preview_all", "Set Global Preview", false, false)]
    [Description("Hide or restore Script Previews: section is inputs, outputs, or all (default, both sections). Keeps individual and group eyes, output data, and Bake choices.")]
    public static IToolResult PreviewAll(RhinoDoc doc, bool visible, int revision, string section = "all")
    {
        Host.CheckToolRevision(revision);
        Host.SetPreviewAll(visible, section); return State(doc);
    }

    [McpServerTool("rhdock_set_layout", "Organize Script Panel", false, false)]
    [Description("Replace the selected Script's Inputs/Outputs visual layout: create, rename, reorder or remove groups and reorder/move Ports. Each section has groups and ungrouped arrays. Each group has name, ports, and optional previewOff (true hides that group's Previews without changing child eyes). Use declared Port names from rhdock_state; omitted Ports appear Ungrouped. Does not change parameter names, values, Run order or list-item order. Pass layout.rev from fresh state as layoutRevision, or 0 when layout is absent. A stale layout revision is refused. Folding and Arrange are local window interactions, not saved layout.")]
    public static IToolResult SetLayout(RhinoDoc doc, SectionLayout inputs, SectionLayout outputs, int layoutRevision, int revision)
    {
        Host.CheckToolRevision(revision);
        if (!Host.TrySetLayout(layoutRevision, inputs, outputs))
            return Failure(ToolError.Failed, ContentBlock.CreateJson(Host.ToolState()), "The layout changed. Read fresh rhdock_state and apply the intended change to its current layout.");
        return State(doc);
    }

    [McpServerTool("rhdock_request_pick", "Request User Geometry Pick", false, false)]
    [Description("Ask the user to pick geometry for the named input through rhDock's normal Rhino command. Waits for the pick command; cancelled picks are reported in resulting state.")]
    public static IToolResult Pick(RhinoDoc doc, string name, int revision)
    {
        Host.CheckToolRevision(revision);
        if (!Host.Session.Selected!.Ports.Any(p => !p.IsOutput && p.Name == name && SingleScriptHost.PortKind(p) == "pick")) throw new ArgumentException("Unknown geometry input.");
        Host.Pick(name, "pick", 0); return State(doc);
    }

    [McpServerTool("rhdock_bake", "Bake Current Outputs", false, true)]
    [Description("Bake named current successful geometry outputs into the bound document, using the host's explicit Undo record. Requires approval according to mode.")]
    public static IToolResult Bake(RhinoDoc doc, string[] names, int revision)
    {
        Host.CheckToolRevision(revision);
        if (names.Length == 0 || names.Any(name => !Host.CanBake(name))) throw new ArgumentException("Every named output must have current bakeable geometry.");
        Host.Bake(names); return State(doc);
    }

    [McpServerTool("rhdock_solve", "Control Solve", false, true)]
    [Description("Explicit Solver control: play, pause, step or reset. Play executes repository code repeatedly until Done/pause. Use rhdock_run for one Frame.")]
    public static IToolResult Solve(RhinoDoc doc, string action, int revision)
    {
        Host.CheckToolRevision(revision);
        if (!Host.Session.IsSolver) throw new ArgumentException("The selected Script is not a Solver Script.");
        switch (action) { case "play": Host.Play(); break; case "pause": Host.Pause(); break; case "step": Host.Pause(); Host.Step(); break; case "reset": Host.ResetSolve(); break; default: throw new ArgumentException("Use play, pause, step or reset."); }
        return State(doc);
    }

    [McpServerTool("rhdock_rhino_type", "Inspect RhinoCommon API", true, false)]
    [Description("Inspect an exact RhinoCommon type name and optionally filter its public members. Returns real installed signatures, at most 100.")]
    public static IToolResult RhinoType(RhinoDoc doc, string type, string? member = null)
    {
        var found = typeof(RhinoDoc).Assembly.GetType(type) ?? throw new ArgumentException("RhinoCommon type not found: " + type);
        return Success(new { type, members = found.GetMembers().Where(m => member is null || m.Name.Contains(member, StringComparison.OrdinalIgnoreCase)).Take(100).Select(m => m.ToString()).ToArray() });
    }
}
