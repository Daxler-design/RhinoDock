using System.Threading;
using rhDock.Core;

internal static class ScriptProjectChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-p1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Components"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            var entry = Path.Combine(folder, "Components", "Sample.cs");
            const string source = """
                using System;
                using System.Collections.Generic;
                namespace Fixture;
                public class Sample {
                    public void Run(double x, ref object y, int count = 7) {
                        if (x < 0) throw new InvalidOperationException("negative fixture input");
                        y = x + count;
                    }
                }
                public interface IGH_Goo { object ScriptVariable(); }
                public class Wrapper : IGH_Goo { object IGH_Goo.ScriptVariable() => 42; }
                """;
            File.WriteAllText(entry, source);
            using var compiler = new ScriptCompilerService();
            var runner = new ScriptRunner(compiler);
            var first = compiler.CompileProject(folder);
            Require(ReferenceEquals(first, compiler.CompileProject(folder)), "mtime cache reuse");
            var ports = runner.Describe(folder, "Components/Sample.cs");
            Require(ports.Single(p => p.Name == "count").IsOptional && ports.Single(p => p.Name == "y").IsOutput, "optional and ref port descriptors");
            var bag = new Bag(); bag.Values["x"] = "2.5";
            Require(runner.Execute(folder, "Sample.cs", bag).Status == "ok" && Equals(bag.Values["y"], 9.5), "input conversion and optional default");
            var wrapper = Activator.CreateInstance(first.Assembly.GetType("Fixture.Wrapper", true)!);
            Require(Equals(PortUnwrap.Unwrap(wrapper), 42) && PortUnwrap.Unwrap(null) is null && Equals(PortUnwrap.Unwrap("plain"), "plain"), "explicit IGH_Goo interface and plain values");
            bag.Values["x"] = -1;
            var error = runner.Execute(folder, "Sample.cs", bag);
            Require(error.Status == "error" && error.Error == "negative fixture input" && bag.Values["y"] is null, "readable invocation error and cleared output");
            using (var watch = new ProjectWatchService(compiler))
            {
                var target = new ReloadTarget(); watch.Register(folder, target);
                for (int i = 0; i < 4; i++) { File.AppendAllText(entry, "\n// burst"); Thread.Sleep(20); }
                Require(SpinWait.SpinUntil(() => target.Changed == 1, 5000), "watcher source callback");
                Thread.Sleep(400);
                Require(target.Changed == 1, "200ms debounce coalesces burst");
                Directory.CreateDirectory(Path.Combine(folder, "bin"));
                File.WriteAllText(Path.Combine(folder, "bin", "Ignored.cs"), "invalid source");
                Thread.Sleep(500);
                Require(target.Changed == 1, "watcher ignores bin output");
                var renamed = Path.Combine(folder, "Components", "Renamed.cs");
                File.Move(entry, renamed);
                if (OperatingSystem.IsMacOS()) Console.WriteLine("SKIP: watcher rename callback is not reliable on macOS.");
                else Require(SpinWait.SpinUntil(() => target.Changed >= 2, 5000) && target.Old == Path.Combine("Components", "Sample.cs") && target.New == Path.Combine("Components", "Renamed.cs"),
                    "watcher relative rename callback");
                watch.Unregister(folder);
                var beforeUnregister = target.Changed;
                File.AppendAllText(renamed, "\n// after unregister");
                Thread.Sleep(400);
                Require(target.Changed == beforeUnregister, "watcher unregister stops callbacks");
                File.WriteAllText(renamed, "public class Broken { syntax error }");
            }
            compiler.Invalidate(folder);
            var failed = runner.Execute(folder, "Renamed.cs", bag);
            Require(failed.Status == "compile_error" && failed.Error!.Contains("CS"), "compile_error diagnostics returned without throwing");
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), """
                <Project><ItemGroup><Reference Include="Missing">
                <HintPath>$(RhDockMissingProperty_762AAC)/Missing.dll</HintPath>
                </Reference></ItemGroup></Project>
                """);
            compiler.Invalidate(folder);
            runner.Execute(folder, "Renamed.cs", bag);
            Require(compiler.Messages.Any(m => m.Contains("Unresolved reference HintPath")) && compiler.LastReferences.Count > 0,
                "Reference diagnostics remain available after failed compilation");
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), """
                <Project><ItemGroup><Reference Include="rhDock.Core">
                <HintPath>$(RhDockHostProperty_762AAC)/rhDock.Core.dll</HintPath>
                </Reference></ItemGroup></Project>
                """);
            using var hostCompiler = new ScriptCompilerService(typeof(IPortBag).Assembly.Location);
            runner = new ScriptRunner(hostCompiler);
            runner.Execute(folder, "Renamed.cs", bag);
            Require(!hostCompiler.Messages.Any(m => m.Contains("Unresolved reference HintPath")),
                "Host-provided reference does not produce an unresolved HintPath warning");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Require(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    private sealed class ReloadTarget : IReloadable
    {
        private int changed;
        public int Changed => Volatile.Read(ref changed);
        public string? Old, New;
        public void OnSourceChanged() => Interlocked.Increment(ref changed);
        public void OnEntryRenamed(string oldRel, string newRel) { Old = oldRel; New = newRel; }
    }
    private sealed class Bag : IPortBag
    {
        public Dictionary<string, object?> Values { get; } = new();
        public object? GetInput(string name) => Values.GetValueOrDefault(name);
        public void SetOutput(string name, object? value) => Values[name] = value;
    }
}
