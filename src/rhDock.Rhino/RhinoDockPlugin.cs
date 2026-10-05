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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AgentHost.LogFatal(e.ExceptionObject);
        global::Rhino.UI.Panels.RegisterPanel(this, typeof(SingleScriptPanel), "rhDock",
            typeof(RhinoDockPlugin).Assembly, "rhDock.PanelIcon.ico", global::Rhino.UI.PanelType.System);
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        AgentHost.Shutdown();
        SingleScriptHost.Shutdown();
        base.OnShutdown();
    }
}
