using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;

namespace rhDock.Rhino;

[Guid("84FE9FDD-97EF-4DE3-A63E-3EEDC73ED729")]
public sealed class RhinoDockMirrorCommand : Command
{
    public override string EnglishName => "RhinoDockMirror";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var host = SingleScriptHost.Current;
        if (host.MirrorActive) { host.StopMirror(); return Result.Success; }
        var port = 5173;
        var result = RhinoGet.GetInteger("Vite development server port", false, ref port, 1, 65535);
        return result != Result.Success ? result : host.StartMirror(port) ? Result.Success : Result.Failure;
    }
}
