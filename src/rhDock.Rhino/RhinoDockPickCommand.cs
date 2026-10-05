using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace rhDock.Rhino;

/// <summary>Runs the panel's pending pick inside a real Rhino command, so Rhino owns the prompt, Escape, and undo context.</summary>
[Guid("5E0C3B7A-6F0B-4D55-9C0E-2B7A3C6F4E19")]
[CommandStyle(Style.Hidden)]
public sealed class RhinoDockPickCommand : Command
{
    public override string EnglishName => "RhinoDockPick";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try { return SingleScriptHost.Current.RunPendingPick(doc); }
        finally { SingleScriptHost.Current.Refresh(); }
    }
}
