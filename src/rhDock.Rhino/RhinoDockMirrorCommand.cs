using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace rhDock.Rhino;

/// <summary>
/// Toggles the rhDock Mirror, which shows the live panel and Extension windows in a browser. The Vite option instead
/// relays the panel to a frontend development server, for work on rhDock's own panel.
/// </summary>
[Guid("84FE9FDD-97EF-4DE3-A63E-3EEDC73ED729")]
public sealed class RhinoDockMirrorCommand : Command
{
    public override string EnglishName => "RhinoDockMirror";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var host = SingleScriptHost.Current;
        if (MirrorServer.Running || host.MirrorActive)
        {
            if (MirrorServer.Running) { MirrorServer.Stop(); RhinoApp.WriteLine("rhDock Mirror stopped."); }
            if (host.MirrorActive) host.StopMirror();
            return Result.Success;
        }
        var get = new GetOption();
        get.SetCommandPrompt("Press Enter to mirror the panel and Extension windows to a browser, or choose ViteDevServer (panel only)");
        get.AcceptNothing(true);
        var vite = get.AddOption("ViteDevServer");
        if (get.Get() == GetResult.Option && get.OptionIndex() == vite)
        {
            var port = 5173;
            var result = RhinoGet.GetInteger("Vite development server port", false, ref port, 1, 65535);
            return result != Result.Success ? result : host.StartMirror(port) ? Result.Success : Result.Failure;
        }
        if (get.CommandResult() != Result.Success) return get.CommandResult();
        if (!MirrorServer.Start())
        {
            RhinoApp.WriteLine("rhDock Mirror could not start: ports 5170–5189 are in use.");
            return Result.Failure;
        }
        host.RefreshPanel();
        RhinoApp.WriteLine($"rhDock Mirror: {MirrorServer.Url} (only this computer can open it; run RhinoDockMirror again to stop).");
        MirrorServer.OpenInBrowser();
        return Result.Success;
    }
}
