using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace rhDock.Rhino;

[Guid("7C88F9AC-0C33-483C-A50A-5A9CFDD88428")]
public sealed class RhinoDockCommand : Command
{
    public override string EnglishName => "RhinoDock";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        SingleScriptHost.Current.Toggle(doc);
        return Result.Success;
    }
}
