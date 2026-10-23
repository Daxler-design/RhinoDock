using System.Runtime.InteropServices;
using System.Text.Json;
using Eto.Forms;

namespace rhDock.Rhino;

/// <summary>Records native surface geometry without changing the host's DPI awareness or browser zoom.</summary>
internal static class WebViewDiagnostics
{
    public static void Record(WebView web, string phase, string? browser = null)
    {
        if (!OperatingSystem.IsWindows() || web.IsDisposed) return;
        try
        {
            var control = web.ControlObject;
            object? Read(string name) => control?.GetType().GetProperty(name)?.GetValue(control);
            var core = Read("CoreWebView2");
            var environment = core?.GetType().GetProperty("Environment")?.GetValue(core);
            var handle = Read("Handle") is IntPtr pointer ? pointer : IntPtr.Zero;
            HostLog.Write("webview-layout " + JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, phase, handler = control?.GetType().FullName,
                eto = new { width = web.Size.Width, height = web.Size.Height, visible = web.Visible },
                actualWidth = Read("ActualWidth"), actualHeight = Read("ActualHeight"), zoom = Read("ZoomFactor"),
                browserPid = core?.GetType().GetProperty("BrowserProcessId")?.GetValue(core),
                userDataFolder = environment?.GetType().GetProperty("UserDataFolder")?.GetValue(environment),
                surface = Window(handle), parent = Window(handle == IntPtr.Zero ? IntPtr.Zero : GetParent(handle)),
                browser
            }));
        }
        catch (Exception ex) { HostLog.Write("webview-layout unavailable: " + ex.Message); }
    }

    private static object? Window(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return null;
        return new { handle = handle.ToInt64(), dpi = GetDpiForWindow(handle), x = rect.Left, y = rect.Top,
            width = rect.Right - rect.Left, height = rect.Bottom - rect.Top };
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}
