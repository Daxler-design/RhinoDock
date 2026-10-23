using rhDock.Core;
using rhDock.Extensions;
using rhDock.Rhino;

internal static class ExtensionScriptsChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Components"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Components", "Sample.cs"), """
                public class Sample {
                    public void Run(double value, double[] values, System.DateTime date, System.DateTime[] dates) { }
                }
                public class PrivateValue { }
                public static class EditorCallbacks {
                    public static void Edit(string name, int? index) { }
                    public static string Format(object value) { return "value"; }
                    public static object Parse(string text) { return new System.DateTime(2026, 10, 14); }
                }
                """);
            var host = SingleScriptHost.Current;
            host.Session.Open(folder);
            host.Session.Select("Components/Sample.cs");
            var context = new ExtensionContext();
            var scripts = new ExtensionScripts(context);
            scripts.SetInput("values", new[] { 1.0, 2.0 });
            scripts.SetInputItem("values", 1, 7.0);
            Check(scripts.Input("values") is double[] { Length: 2 } values && values[0] == 1 && values[1] == 7,
                "public item edits preserve the other values");
            var copy = (double[])scripts.Input("values")!;
            copy[0] = 99;
            Check(((double[])scripts.Input("values")!)[0] == 1, "public input queries return copies");
            Check(Throws(() => scripts.SetInputItem("value", 0, 1.0)) && Throws(() => scripts.SetInputItem("values", -1, 1.0))
                && Throws(() => scripts.SetInputItem("values", 0, "bad")), "public item edits reject scalar ports, indices, and mismatched types");

            var calls = new List<ScriptInputItem>();
            scripts.RegisterEditor(typeof(DateTime), (name, index) => calls.Add(new(name, index)));
            scripts.Edit(typeof(DateTime).FullName!, "date", null);
            scripts.Edit(typeof(DateTime).FullName!, "dates", 1);
            Check(calls.SequenceEqual(new[] { new ScriptInputItem("date", null), new ScriptInputItem("dates", 1) }),
                "registered actions receive scalar and list targets");

            scripts.RegisterEditor(typeof(Guid), _ => { });
            Check(scripts.Edits(typeof(Guid).FullName!) && !scripts.Edits(typeof(Guid).FullName!, true),
                "legacy single-value editors do not take over list ports");
            var date = new DateTime(2026, 10, 14);
            scripts.SetInput("dates", new[] { date, date.AddDays(1) });
            scripts.RegisterEditor(typeof(DateTime), "text", value => ((DateTime)value!).ToString("O"),
                text => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
            scripts.Parse(typeof(DateTime).FullName!, "date", null, "2026-10-16T00:00:00");
            scripts.Parse(typeof(DateTime).FullName!, "dates", 1, "2026-10-17T00:00:00");
            Check((DateTime)scripts.Input("date")! == date.AddDays(2) && ((DateTime[])scripts.Input("dates")!)[1] == date.AddDays(3)
                && scripts.Control(typeof(DateTime).FullName!) == "text", "inline editors update single values and individual list elements");
            scripts.ParseList(typeof(DateTime).FullName!, "dates", new[] { "2026-10-18", "2026-10-19" });
            Check(((DateTime[])scripts.Input("dates")!).SequenceEqual(new[] { date.AddDays(4), date.AddDays(5) }),
                "registered inline editors also handle whole-list text");
            var before = (DateTime[])scripts.Input("dates")!;
            scripts.Parse(typeof(DateTime).FullName!, "dates", 1, "invalid date");
            Check(((DateTime[])scripts.Input("dates")!).SequenceEqual(before) && context.Errors.Count == 1,
                "an editor parse failure keeps the committed input");
            Check(Throws(() => scripts.RegisterEditor(typeof(DateTime), "html", _ => "", _ => null)), "unknown embedded controls are rejected");
            using (var compiler = new ScriptCompilerService())
            using (var project = compiler.CompileUncached(folder))
                Check(Throws(() => scripts.RegisterEditor(project.Assembly.GetType("PrivateValue")!, (_, _) => { })),
                    "collectible editor types are rejected");

            scripts.RegisterEditor(typeof(DateTime), (_, _) => { });
            Check(scripts.Control(typeof(DateTime).FullName!) is null && scripts.Edits(typeof(DateTime).FullName!, true),
                "a replacement Edit action removes the previous inline control");
            var changes = 0;
            scripts.Changed += () => changes++;
            scripts.Reordered += _ => changes++;
            scripts.Retire();
            scripts.Raise();
            scripts.RaiseReordered(new("values", 0, 1));
            scripts.Edit(typeof(DateTime).FullName!, "date", null);
            Check(changes == 0 && calls.Count == 2 && !scripts.Edits(typeof(DateTime).FullName!)
                && Throws(() => scripts.SetInputItem("values", 0, 1.0)), "retirement drops editors and callbacks and rejects later writes");
            var (weak, registered) = RegisterGeneration(folder);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check(weak.IsAlive, "live editor callbacks retain their Extension generation");
            registered.Retire();
            for (var i = 0; i < 10 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Check(!weak.IsAlive, "retiring indexed and inline editors releases their Extension generation");
        }
        finally { SingleScriptHost.Current.Session.Dispose(); Directory.Delete(folder, true); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference, ExtensionScripts) RegisterGeneration(string folder)
    {
        using var compiler = new ScriptCompilerService();
        using var project = compiler.CompileUncached(folder);
        var type = project.Assembly.GetType("EditorCallbacks")!;
        var scripts = new ExtensionScripts(new ExtensionContext());
        scripts.RegisterEditor(typeof(double), type.GetMethod("Edit")!.CreateDelegate<Action<string, int?>>());
        scripts.RegisterEditor(typeof(DateTime), "text", type.GetMethod("Format")!.CreateDelegate<Func<object?, string>>(),
            type.GetMethod("Parse")!.CreateDelegate<Func<string, object?>>());
        return (project.LoadContextReference(), scripts);
    }

    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}

// Compile the real facade against a portable host boundary, as the AgentHost checks do; native Rhino is a separate gate.
namespace rhDock.Rhino
{
    sealed partial class SingleScriptHost
    {
        public SingleScriptSession Session { get; } = new(new ScriptCompilerService());
        public bool Running => false;
        public bool HasDocument => true;
        public int Revision { get; set; } = 1;
        public ScriptInputItem? ActiveItem { get; private set; }
        public static string PortKind(PortDescriptor port) => "list";
        public void SetActiveItem(string? name, int? index) => ActiveItem = name is null ? null : new(name, index);
        public void Select(string path) => Session.Select(path);
        public void SetInput(string name, object? value)
        {
            var before = SavedScalar.Capture(Session.Inputs.GetValueOrDefault(name));
            Session.SetInput(name, value);
            if (before is not null && SavedScalar.Capture(value) is { } after) RecordInputUndo(name, before, after);
        }
        public void Run() => Session.Run();
        public void Refresh() { }
        public void ReorderInput(string name, int from, int to) => Session.SetInput(name,
            InputItems.Move((System.Collections.IEnumerable)Session.Inputs[name]!, from, to));
    }
    static class GeometryVocabulary { public static Type? Resolve(string name) => null; }
}
namespace rhDock.Extensions
{
    sealed class ExtensionContext
    {
        public readonly List<Exception> Errors = [];
        public void Guard(string name, Action action) { try { action(); } catch (Exception ex) { Errors.Add(ex); } }
    }
}
namespace Rhino.Geometry
{
    class GeometryBase { public GeometryBase Duplicate() => new(); }
    class Polyline { public Polyline(Polyline source) { } }
}
