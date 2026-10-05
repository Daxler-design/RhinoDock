using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace rhDock.Rhino;

/// <summary>
/// Applies the panel's pending selection inside a Rhino command, like SelLast, so Rhino refreshes selection-dependent
/// display such as the Gumball when the command ends.
/// </summary>
[Guid("B2E4A6C1-3D7F-4F28-9A15-6C0D8E2B7F43")]
[CommandStyle(Style.Hidden)]
public sealed class RhinoDockSelectCommand : Command
{
    public override string EnglishName => "RhinoDockSelect";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try { return SingleScriptHost.Current.RunPendingSelect(doc); }
        finally { SingleScriptHost.Current.Refresh(); }
    }
}
