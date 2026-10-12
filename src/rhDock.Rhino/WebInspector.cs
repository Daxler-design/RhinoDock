using System.Runtime.InteropServices;
using Eto.Forms;

namespace rhDock.Rhino;

/// <summary>
/// Lets Safari's Develop menu inspect rhDock's pages. Since macOS 13.3 a WKWebView is inspectable only when the app sets
/// <c>isInspectable</c>, and Eto does not. WebView2 on Windows keeps its own default (F12 and the Inspect menu item).
/// </summary>
internal static class WebInspector
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    /// <summary>Returns a short result for the log.</summary>
    public static string Enable(WebView web)
    {
        if (!OperatingSystem.IsMacOS()) return "WebView2 default";
        try
        {
            var view = NativeHandle(web.ControlObject);
            if (view == IntPtr.Zero) return "unavailable (no native view)";
            var setter = sel_registerName("setInspectable:");
            if (!RespondsToSelector(view, sel_registerName("respondsToSelector:"), setter))
                return $"unavailable ({Marshal.PtrToStringUTF8(object_getClassName(view))} has no setInspectable:; macOS before 13.3?)";
            SetBool(view, setter, true);
            return "on";
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }

    // Eto.Mac's control object is the WKWebView. Its Handle is an IntPtr or, in .NET for macOS, a NativeHandle struct.
    private static IntPtr NativeHandle(object? control)
    {
        var handle = control?.GetType().GetProperty("Handle")?.GetValue(control);
        return handle switch
        {
            IntPtr pointer => pointer,
            null => IntPtr.Zero,
            _ => handle.GetType().GetProperty("Handle")?.GetValue(handle) is IntPtr inner ? inner : IntPtr.Zero
        };
    }

    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr object_getClassName(IntPtr instance);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool RespondsToSelector(IntPtr receiver, IntPtr selector, IntPtr query);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
}
