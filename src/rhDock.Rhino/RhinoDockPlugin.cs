using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace rhDock.Rhino;

[Guid("F48C10C1-44B2-47BD-B817-D6A8FE399B76")]
public sealed class RhinoDockPlugin : PlugIn
{
    public static RhinoDockPlugin? Instance { get; private set; }
    public RhinoDockPlugin() => Instance = this;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        HostLog.Write($"start pid={Environment.ProcessId} version={typeof(RhinoDockPlugin).Assembly.GetName().Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HostLog.Fatal(e.ExceptionObject);
        global::Rhino.UI.Panels.RegisterPanel(this, typeof(SingleScriptPanel), "rhDock",
            typeof(RhinoDockPlugin).Assembly, "rhDock.PanelIcon.ico", global::Rhino.UI.PanelType.System);
        // Compile enabled Extensions after Rhino finishes loading, not during plug-in load.
        EventHandler? loadExtensions = null;
        loadExtensions = (_, _) =>
        {
            global::Rhino.RhinoApp.Idle -= loadExtensions;
            Extensions.ExtensionHost.Current.LoadAtStartup();
        };
        global::Rhino.RhinoApp.Idle += loadExtensions;
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        MirrorServer.Stop();
        Extensions.ExtensionHost.Shutdown();
        AgentHost.Shutdown();
        SingleScriptHost.Shutdown();
        HostLog.Write($"shutdown clean pid={Environment.ProcessId}");
        base.OnShutdown();
    }
}
