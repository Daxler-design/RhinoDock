using System.Diagnostics;
using System.Reflection;
using rhDock.Core;

namespace rhDock.Rhino;

/// <summary>
/// One Extension folder and its current generation. A reload compiles and activates the next generation before the
/// current one is retired, so a compile or activation failure leaves the last working generation running.
/// It has no Rhino dependency so the smoke suite can check it.
/// </summary>
internal sealed class ExtensionSlot(string folder, ScriptCompilerService compiler)
{
    private readonly List<WeakReference> retired = [];
    private CompiledProject? project;
    private Action? retire;

    public string Folder { get; } = Path.GetFullPath(folder);
    public string Name => Path.GetFileName(Folder);
    public int Generation { get; private set; }
    public bool Loaded => project is not null;
    public string? Error { get; private set; }

    /// <summary>
    /// Compiles and activates a new generation. <paramref name="activate"/> undoes its own partial work before it throws,
    /// and otherwise returns the action that retires the generation; that action must not throw.
    /// </summary>
    public bool Reload(Func<Assembly, Action> activate)
    {
        var next = Generation + 1;
        Log($"generation {next} compile start");
        var clock = Stopwatch.StartNew();
        CompiledProject compiled;
        try { compiled = compiler.CompileUncached(Folder); }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log($"generation {next} compile failed after {clock.ElapsedMilliseconds} ms: {ex.Message}");
            return false;
        }
        Log($"generation {next} compiled in {clock.ElapsedMilliseconds} ms");
        Action nextRetire;
        try { nextRetire = activate(compiled.Assembly); }
        catch (Exception ex)
        {
            compiled.Dispose();
            Error = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            Log($"generation {next} activation failed: {ex}");
            return false;
        }
        Log($"generation {next} activated");
        Unload();
        project = compiled;
        retire = nextRetire;
        Generation = next;
        return true;
    }

    public void Unload()
    {
        Error = null;
        if (project is null) return;
        retire?.Invoke();
        retired.Add(project.LoadContextReference());
        project.Dispose();
        project = null;
        retire = null;
        Log($"generation {Generation} retired and unloading");
    }

    /// <summary>The number of retired generations whose load context survives full collections.</summary>
    public int Leaked()
    {
        if (retired.Count == 0) return 0;
        var before = retired.Count;
        for (var i = 0; i < 3 && retired.Count > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            retired.RemoveAll(reference => !reference.IsAlive);
        }
        Log($"leak check: {before - retired.Count} collected, {retired.Count} still loaded");
        return retired.Count;
    }

    private void Log(string text) => HostLog.Write($"extension {Name} {text}");
}
