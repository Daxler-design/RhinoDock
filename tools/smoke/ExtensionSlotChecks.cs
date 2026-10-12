using System.Reflection;
using System.Runtime.CompilerServices;
using rhDock.Core;
using rhDock.Rhino;

internal static class ExtensionSlotChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Ext.cs");
        var live = new List<string>();
        var retired = 0;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            File.WriteAllText(source, Source("one"));
            using var compiler = new ScriptCompilerService();
            var slot = new ExtensionSlot(folder, compiler);
            Func<Assembly, Action> activate = assembly => Activate(assembly, live, () => retired++);

            Check(slot.Reload(activate) && slot.Generation == 1 && live[^1] == "one", "first Extension generation activates");

            File.WriteAllText(source, Source("two").Replace("return", "retur"));
            Check(!slot.Reload(activate) && slot.Error is { Length: > 0 } && slot.Generation == 1 && slot.Loaded && retired == 0,
                "compile failure keeps the running generation");

            File.WriteAllText(source, Source("two").Replace("public Ext() { }", "public Ext() => throw new System.Exception(\"bad start\");"));
            Check(!slot.Reload(activate) && slot.Error == "bad start" && slot.Generation == 1 && retired == 0,
                "activation failure keeps the running generation");

            File.WriteAllText(source, Source("two"));
            Check(slot.Reload(activate) && slot.Generation == 2 && slot.Error is null && live[^1] == "two" && retired == 1,
                "successful reload retires the previous generation after the next one activates");
            Check(slot.Leaked() == 0, "retired Extension generation collects");

            slot.Unload();
            Check(!slot.Loaded && retired == 2 && slot.Leaked() == 0, "unloaded Extension generation collects");
        }
        finally { Directory.Delete(folder, true); }
        CheckTemplate();
        CheckBoundary();
    }

    // Values between Scripts and Extensions: only .NET or host types cross, and collections become typed arrays.
    private static void CheckBoundary()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Thing.cs"), "public class Thing { }");
            using var compiler = new ScriptCompilerService();
            using var project = compiler.CompileUncached(folder);
            CheckBoundaryValues(project.Assembly);
        }
        finally { Directory.Delete(folder, true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckBoundaryValues(Assembly script)
    {
        var thing = Activator.CreateInstance(script.GetType("Thing", throwOnError: true)!)!;
        Func<object, object?> duplicate = value => value is Exception ? value : null;
        var list = (Array)Activator.CreateInstance(script.GetType("Thing")!.MakeArrayType(), 1)!;
        Check(!BoundaryValues.TryCopy(thing, duplicate, out _) && !BoundaryValues.TryCopy(list, duplicate, out _)
            && !BoundaryValues.CanCross(thing.GetType()) && BoundaryValues.CanCross(typeof(DateTime)),
            "Script-defined values and arrays of them cannot cross");
        Check(BoundaryValues.TryCopy(new List<double> { 1, 2 }, duplicate, out var numbers) && numbers is double[] { Length: 2 }
            && BoundaryValues.TryCopy("text", duplicate, out var text) && (string?)text == "text",
            "lists cross as typed arrays and strings as themselves");
        Check(BoundaryValues.TryCopy(new object[] { new ArgumentException(), new InvalidOperationException() }, duplicate, out var mixed)
            && mixed is SystemException[], "mixed items cross as an array of their common base type");
    }

    private static void CheckTemplate()
    {
        var repo = Path.Combine(Path.GetTempPath(), "rhdock-extension-repo-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtensionRepositoryTemplate.CreateRepository(repo);
            Check(new[] { ".gitignore", "AGENTS.md", "CLAUDE.md", "MyExtension/MyExtension.csproj", "MyExtension/MyExtension.cs", "MyExtension/index.html", "MyExtension/rhdock.js" }
                .All(file => File.Exists(Path.Combine(repo, file))), "new Extension Repo has guidance and the starter Extension");
            ExtensionRepositoryTemplate.CreateExtension(repo, "SunStudy");
            var source = File.ReadAllText(Path.Combine(repo, "SunStudy", "SunStudy.cs"));
            Check(File.Exists(Path.Combine(repo, "SunStudy", "SunStudy.csproj")) && source.Contains("class SunStudy") && !source.Contains("MyExtension"),
                "new Extension renames the starter class and project");
            Check(Throws(() => ExtensionRepositoryTemplate.CreateExtension(repo, "SunStudy")) && Throws(() => ExtensionRepositoryTemplate.CreateExtension(repo, "Sun Study"))
                && Throws(() => ExtensionRepositoryTemplate.CreateExtension(repo, "class")) && Throws(() => ExtensionRepositoryTemplate.CreateRepository(repo)),
                "Extension templates refuse existing folders and invalid names");
        }
        finally { Directory.Delete(repo, true); }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (Exception) { return true; }
    }

    // Kept out of Run so no local in the checking frame holds a fixture type, assembly, or instance.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Action Activate(Assembly assembly, List<string> live, Action onRetire)
    {
        var instance = Activator.CreateInstance(assembly.GetType("Ext", throwOnError: true)!)!;
        live.Add((string)instance.GetType().GetMethod("Value")!.Invoke(instance, null)!);
        // As ExtensionView.Post does: host-side serialization caches the generation's type in System.Text.Json.
        System.Text.Json.JsonSerializer.Serialize(instance, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return () => { GC.KeepAlive(instance); onRetire(); };
    }

    private static string Source(string value) =>
        "public class Ext { public Ext() { } public int Count { get; set; } public string Value() { return \"" + value + "\"; } }";

    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
}
