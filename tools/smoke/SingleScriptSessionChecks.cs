using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using rhDock.Core;

internal static class SingleScriptSessionChecks
{
    public static void Run()
    {
        CheckEnum();
        CheckNewRepository();
        CheckPulse();
        CheckSolver();
        Check(ColorText.Parse("#11223380") is { A: 0x80, R: 0x11, G: 0x22, B: 0x33 } translucent && ColorText.Format(translucent) == "#11223380"
            && ColorText.Format(ColorText.Parse("aabbcc")!.Value) == "#AABBCC" && ColorText.Parse("#12345") is null
            && SavedScalar.Capture(new[] { translucent })!.Restore() is List<System.Drawing.Color> { Count: 1 } colorList && colorList[0] == translucent,
            "color text keeps alpha and color lists restore");
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-single-" + Guid.NewGuid().ToString("N"));
        var components = Path.Combine(folder, "Components");
        Directory.CreateDirectory(components);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            var entry = Path.Combine(components, "Sample.cs");
            File.WriteAllText(entry, Source("double x", "x * 2"));
            using var compiler = new ScriptCompilerService();
            using var session = new SingleScriptSession(compiler);
            session.Open(folder);
            Check(session.Scripts.Count == 1, "direct session discovers Script");
            Check(PortTypeName.Element("System.Collections.Generic.List`1[[Rhino.Geometry.Curve, RhinoCommon]]") == "Rhino.Geometry.Curve"
                && PortTypeName.Element("System.Double[]") == "System.Double",
                "port element names remain available for direct Rhino inputs");
            session.Select("Components/Sample.cs");
            Check(session.Run().Status == "error" && session.Result.Error == "missing input: x" && Calls(compiler, folder) == 0, "missing required input invokes zero times");
            session.SetInput("x", 3.0);
            Check(session.Run().Status == "ok" && Equals(session.Outputs["y"], 6.0) && Calls(compiler, folder) == 1, "manual Run invokes once");
            var legacyPath = Path.Combine(folder, ".rhdock", "session.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, "legacy graph file");
            var savedInputs = new Dictionary<string, SavedScalar> { ["x"] = SavedScalar.Capture(3.0)! };
            var record = SingleScriptWorkspaceStore.CaptureRun(session, savedInputs, []);
            Check(record.Pid == Environment.ProcessId, "Run Records identify the process that evaluated the Script");
            var savedPath = SingleScriptWorkspaceStore.PathFor(folder);
            var seeded = SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, "Components/Sample.cs", "manual",
                new(StringComparer.Ordinal) { ["Components/Sample.cs"] = new(savedInputs, [], ["y"], [], record) }) { Pid = Environment.ProcessId });
            var oldState = seeded.Revisions[^1].Scripts["Components/Sample.cs"];
            Check(seeded.Revisions.Length == 1 && oldState.Inputs["x"].Restore() is 3.0 && oldState.LastRun?.Outputs["y"].Value == "6",
                "first save creates a one-revision history");
            var legacyPidJson = JsonNode.Parse(File.ReadAllText(savedPath))!;
            var legacyPidRevision = legacyPidJson["revisions"]![0]!.AsObject();
            legacyPidRevision.Remove("pid");
            legacyPidRevision["scripts"]!["Components/Sample.cs"]!["lastRun"]!.AsObject().Remove("pid");
            File.WriteAllText(savedPath, legacyPidJson.ToJsonString());
            var legacyPidHistory = SingleScriptWorkspaceStore.LoadHistory(folder)!;
            Check(legacyPidHistory.Revisions[0].Pid is null
                && legacyPidHistory.Revisions[0].Scripts["Components/Sample.cs"].LastRun!.Pid is null,
                "older v3 records without PID load with unknown provenance");
            AtomicJson.Write(savedPath, seeded);
            var otherScript = "Components/Other.cs";
            var otherState = new SavedScriptState(new() { ["x"] = SavedScalar.Capture(8.0)! },
                [], [], [], null)
            {
                NumericRanges = new() { ["x"] = new NumericRange(0, 20, 0.5) },
                BooleanStyles = new() { ["fire"] = "press" },
                PreviewColors = new() { ["y"] = "#9AA0F2" }
            };
            var pickedId = Guid.NewGuid().ToString();
            var pickedState = oldState with { Picks = new()
            {
                ["curves"] = new SavedPick("objects", null, null, true) { Ids = [pickedId] }
            } };
            var scripts = new Dictionary<string, SavedScriptState>(StringComparer.Ordinal)
            { ["Components/Sample.cs"] = pickedState, [otherScript] = otherState };
            var first = SingleScriptWorkspaceStore.Append(folder,
                new(DateTime.UtcNow, otherScript, "auto", scripts) { Pid = 73152 });
            var hostHistory = SingleScriptWorkspaceStore.LoadHistory(folder)!;
            Check(hostHistory.Revisions[0].Pid == Environment.ProcessId && hostHistory.Revisions[^1].Pid == 73152
                && hostHistory.Revisions[^1].Scripts["Components/Sample.cs"].LastRun!.Pid == Environment.ProcessId,
                "a second host's saved revision preserves the original Run's PID");
            Check(first.Revisions.Length == 2 && first.Revisions[^1].Script == otherScript
                && first.Revisions[^1].Scripts["Components/Sample.cs"].Inputs["x"].Restore() is 3.0,
                "switching Scripts keeps each Script's inputs and latest active selection");
            Check(File.ReadAllText(legacyPath) == "legacy graph file", "legacy graph file is preserved when history starts");
            for (var i = 0; i < 18; i++)
            {
                scripts = new(scripts, StringComparer.Ordinal)
                { [otherScript] = otherState with { Inputs = new() { ["x"] = SavedScalar.Capture((double)i)! } } };
                SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "auto", scripts));
            }
            var restored = SingleScriptWorkspaceStore.LoadHistory(folder)!;
            Check(restored.Revisions.Length == 16 && restored.Revisions[^1].Script == otherScript
                && restored.Revisions[^1].Scripts[otherScript].Inputs["x"].Restore() is 17.0
                && restored.Revisions[^1].Scripts["Components/Sample.cs"].Inputs["x"].Restore() is 3.0
                && restored.Revisions[^1].Scripts[otherScript].NumericRanges?["x"].Step == 0.5
                && restored.Revisions[^1].Scripts[otherScript].PreviewColors?["y"] == "#9AA0F2",
                "history retains only 16 revisions and restores the latest complete state");
            AtomicJson.Write(savedPath, new { restored.V, Repo = "/Users/another-user/working-repository", restored.Revisions });
            Check(SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions.Length == 16
                && SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "manual", scripts)).Revisions.Length == 16,
                "Workspace saved with an older absolute repo field loads and appends");
            var windowsKeys = restored with { Revisions = restored.Revisions.Select(revision => revision with
            {
                Script = revision.Script.Replace('/', '\\'),
                Scripts = revision.Scripts.ToDictionary(pair => pair.Key.Replace('/', '\\'), pair => pair.Value)
            }).ToArray() };
            AtomicJson.Write(savedPath, windowsKeys);
            Check(SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions[^1].Scripts.ContainsKey(otherScript),
                "older Windows Script keys load with portable separators");
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "manual", scripts));
            using var json = JsonDocument.Parse(File.ReadAllText(savedPath));
            var revisions = json.RootElement.GetProperty("revisions");
            var sampleJson = revisions[revisions.GetArrayLength() - 1].GetProperty("scripts")
                .GetProperty("Components/Sample.cs");
            Check(json.RootElement.GetProperty("v").GetInt32() == 3
                && revisions.GetArrayLength() == 16
                && sampleJson.GetProperty("picks").GetProperty("curves")
                    .GetProperty("ids")[0].GetString() == pickedId
                && !sampleJson.TryGetProperty("pickDocument", out _)
                && !json.RootElement.TryGetProperty("graph", out _),
                "auto-saved picks retain GUIDs without document-path or graph state");
            var previousV3 = JsonNode.Parse(File.ReadAllText(savedPath))!;
            var previousRevisions = previousV3["revisions"]!.AsArray();
            previousRevisions[previousRevisions.Count - 1]!["scripts"]!["Components/Sample.cs"]!
                .AsObject()["pickDocument"] = "/old/document.3dm";
            File.WriteAllText(savedPath, previousV3.ToJsonString());
            Check(SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions[^1]
                    .Scripts["Components/Sample.cs"].Picks["curves"].Ids![0] == pickedId,
                "older v3 document-path field is ignored without losing picked GUIDs");
            var componentScripts = new Dictionary<string, SavedScriptState>(scripts, StringComparer.Ordinal)
            {
                ["Components/Sample.cs"] = pickedState with { Picks = new()
                {
                    ["edges"] = new SavedPick("components", null, null, true)
                    { Ids = [pickedId, pickedId], Components = [new("SubdEdge", 4), new("SubdEdge", 6)] }
                } }
            };
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "auto", componentScripts));
            var componentPick = SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions[^1]
                .Scripts["Components/Sample.cs"].Picks["edges"];
            Check(componentPick.Kind == "components" && componentPick.Ids!.Length == 2
                && componentPick.Components is [{ Type: "SubdEdge", Index: 4 }, { Type: "SubdEdge", Index: 6 }],
                "Sub-object picks round-trip as object GUIDs with component type and index");
            try
            {
                SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "auto",
                    new(componentScripts, StringComparer.Ordinal)
                    {
                        ["Components/Sample.cs"] = pickedState with { Picks = new()
                        { ["edges"] = new SavedPick("components", null, null, true) { Ids = [pickedId, pickedId], Components = [new("SubdEdge", 4)] } } }
                    }));
                throw new Exception("accepted a Sub-object pick with mismatched components");
            }
            catch (InvalidDataException) { }
            var exportPath = Path.Combine(folder, "shared-workspace.json");
            var beforeExport = File.ReadAllText(savedPath);
            SingleScriptWorkspaceStore.Export(folder, restored.Revisions[^1], exportPath);
            using var exported = JsonDocument.Parse(File.ReadAllText(exportPath));
            Check(exported.RootElement.GetProperty("revisions").GetArrayLength() == 1
                && File.ReadAllText(savedPath) == beforeExport,
                "Export contains only the current revision and leaves history unchanged");
            try { SingleScriptWorkspaceStore.Export(folder, restored.Revisions[^1], savedPath); throw new Exception("export replaced runtime history"); }
            catch (InvalidOperationException) { }
            Check(File.ReadAllText(savedPath) == beforeExport, "Export cannot replace runtime history");
            Check(File.ReadAllText(legacyPath) == "legacy graph file", "new Workspace does not overwrite legacy session");
            using (var reopened = new SingleScriptSession(new ScriptCompilerService()))
            {
                reopened.Open(folder);
                reopened.Select("Components/Sample.cs");
                reopened.SetInput("x", restored.Revisions[^1].Scripts["Components/Sample.cs"].Inputs["x"].Restore());
                Check(!reopened.HasCurrentOutputs && reopened.Outputs.Count == 0,
                    "restored settings and historical Run cannot Preview or Bake before a fresh Run");
            }
            var intact = File.ReadAllText(savedPath);
            try { SingleScriptWorkspaceStore.Append(folder,
                new(DateTime.UtcNow, otherScript, "bad", scripts)); throw new Exception("accepted invalid revision"); }
            catch (InvalidDataException) { }
            Check(File.ReadAllText(savedPath) == intact, "invalid Workspace write preserves existing file");
            var badSettings = new Dictionary<string, SavedScriptState>(scripts, StringComparer.Ordinal)
            { [otherScript] = otherState with { NumericRanges = new() { ["x"] = new NumericRange(10, 0, 0.5) } } };
            try { SingleScriptWorkspaceStore.Append(folder,
                new(DateTime.UtcNow, otherScript, "manual", badSettings)); throw new Exception("accepted invalid control range"); }
            catch (InvalidDataException) { }
            Check(File.ReadAllText(savedPath) == intact, "invalid control range preserves existing Workspace");
            var styled = new Dictionary<string, SavedScriptState>(scripts, StringComparer.Ordinal)
            { [otherScript] = otherState with { PreviewStyles = new() { ["y"] = new PreviewStyle("#EC6F67", 3, null, 40) } } };
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "manual", styled));
            Check(SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions[^1].Scripts[otherScript].PreviewStyles?["y"] == new PreviewStyle("#EC6F67", 3, null, 40),
                "Preview Style round-trips in optional v3 settings");
            var channels = new PreviewStyle(null, 0, null, 30)
            {
                Edges = new PreviewChannel("#123456", 75),
                Fill = new PreviewChannel(null, 20)
            };
            styled[otherScript] = otherState with { PreviewStyles = new() { ["y"] = channels } };
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "manual", styled));
            Check(SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions[^1].Scripts[otherScript].PreviewStyles?["y"] == channels,
                "independent edges and automatic fill survive Workspace round-trip, including zero edge width");
            Check(!(channels with { Fill = new PreviewChannel(null, 101) }).IsValid,
                "invalid channel opacity is rejected");
            intact = File.ReadAllText(savedPath);
            var legacyPreview = styled[otherScript] with { PreviewOff = true };
            Check(!legacyPreview.InputsPreviewVisible && !legacyPreview.OutputsPreviewVisible, "legacy global Preview off still hides both sections");
            var explicitInput = legacyPreview with { InputPreviewOff = false };
            Check(explicitInput.InputsPreviewVisible && !explicitInput.OutputsPreviewVisible, "explicit section state overrides only its legacy global fallback");
            foreach (var inputOff in new[] { false, true })
                foreach (var outputOff in new[] { false, true })
                {
                    var splitPreview = new Dictionary<string, SavedScriptState>(styled, StringComparer.Ordinal)
                    { [otherScript] = styled[otherScript] with { InputPreviewOff = inputOff, OutputPreviewOff = outputOff } };
                    SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "manual", splitPreview));
                    var restoredPreview = SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions.Last().Scripts[otherScript];
                    Check(restoredPreview.InputsPreviewVisible == !inputOff && restoredPreview.OutputsPreviewVisible == !outputOff,
                        "independent input/output Preview switches round-trip");
                }
            intact = File.ReadAllText(savedPath);
            var badStyle = new Dictionary<string, SavedScriptState>(scripts, StringComparer.Ordinal)
            { [otherScript] = otherState with { PreviewStyles = new() { ["y"] = new PreviewStyle("red", 33, null, 140) } } };
            try { SingleScriptWorkspaceStore.Append(folder,
                new(DateTime.UtcNow, otherScript, "manual", badStyle)); throw new Exception("accepted invalid Preview Style"); }
            catch (InvalidDataException) { }
            Check(File.ReadAllText(savedPath) == intact, "invalid Preview Style preserves existing Workspace");
            File.WriteAllText(savedPath, "{\"v\":99}");
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "auto", scripts));
            var badBackup = Directory.GetFiles(Path.Combine(folder, ".rhdock"), "workspace.bad.*.json");
            Check(badBackup.Length == 1 && File.ReadAllText(badBackup[0]) == "{\"v\":99}"
                && SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions.Length == 1,
                "unsupported saved schema is backed up before a fresh history");
            File.WriteAllText(savedPath, "{broken json");
            SingleScriptWorkspaceStore.Append(folder, new(DateTime.UtcNow, otherScript, "auto", scripts));
            badBackup = Directory.GetFiles(Path.Combine(folder, ".rhdock"), "workspace.bad.*.json");
            Check(badBackup.Length == 2 && badBackup.Any(path => File.ReadAllText(path) == "{broken json")
                && SingleScriptWorkspaceStore.LoadHistory(folder)!.Revisions.Length == 1,
                "corrupt Workspace is backed up and replaced");
            File.WriteAllText(savedPath, intact);
            Check(SavedScalar.Capture(new[] { true, false })!.Restore() is List<bool> { Count: 2 },
                "boolean list inputs round-trip as whole values");
            session.SetInput("x", 3.0);
            Check(session.Dirty && !session.HasCurrentOutputs && session.Outputs.Count == 0, "changed picked source invalidates the same input value");
            session.SetInput("x", 4.0);
            Check(session.Dirty && !session.HasCurrentOutputs && session.Outputs.Count == 0, "input change clears stale outputs");
            Check(session.Run().Status == "ok" && Calls(compiler, folder) == 2, "next Run invokes once");
            session.SetInput("x", new[] { 1.0, 2.0 });
            Check(session.Run().Status == "error" && !session.HasCurrentOutputs && session.Outputs.Count == 0 && Calls(compiler, folder) == 2, "scalar mismatch invokes zero times");
            session.SetInput("x", "not a number");
            Check(session.Dirty && !session.HasCurrentOutputs && session.Outputs.Count == 0, "invalid text edit invalidates Preview and Bake state");
            Check(session.Run().Status == "error" && Calls(compiler, folder) == 2, "conversion failure invokes zero times");
            session.SetInput("x", -1.0);
            Check(session.Run().Status == "error" && session.Result.Error == "fixture failure" && session.Outputs.Count == 0 && Calls(compiler, folder) == 3, "runtime error clears outputs");
            var failed = SingleScriptWorkspaceStore.CaptureRun(session,
                new() { ["x"] = SavedScalar.Capture(-1.0)! }, []);
            Check(failed.Status == "error" && failed.Error == "fixture failure" && failed.Outputs.Count == 0,
                "failed Run Record keeps diagnostics without stale outputs");

            File.WriteAllText(entry, Source("double[] x", "x.Sum()"));
            session.Refresh();
            Check(session.Selected?.Ports.Single(p => p.Name == "x").Access == PortAccess.List && session.Inputs.Count == 0, "signature change drops incompatible input");
            session.SetInput("x", new[] { 2.0, 3.0 });
            Check(session.Run().Status == "ok" && Equals(session.Outputs["y"], 5.0) && Calls(compiler, folder) == 1, "list reaches one invocation whole");

            File.WriteAllText(entry, Source("double[] x", "x.Sum() * 2"));
            session.Refresh();
            Check(session.Inputs.TryGetValue("x", out var held) && held is double[] { Length: 2 }, "compatible input survives source refresh");
            Check(session.Run().Status == "ok" && Equals(session.Outputs["y"], 10.0), "refreshed source runs");

            File.WriteAllText(entry, Source("double[] x", "x.Sum()", broken: true));
            session.Refresh();
            Check(session.Run().Status == "compile_error" && session.Outputs.Count == 0, "compile failure invokes zero times");
            File.WriteAllText(entry, Source("double[] x", "x.Sum()"));
            session.Refresh();
            session.SetInput("x", new[] { 7.0 });
            Check(session.Run().Status == "ok", "source recovery runs");
            var weak = Retire(session, compiler, folder);
            for (var i = 0; i < 10 && weak.IsAlive; i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(20); }
            Check(!weak.IsAlive && !session.HasCurrentOutputs, "released generation collects and cannot Preview");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void CheckNewRepository()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            WorkingRepositoryTemplate.Create(folder);
            Check(File.Exists(Path.Combine(folder, "AGENTS.md"))
                && File.ReadAllText(Path.Combine(folder, "CLAUDE.md")).Contains("AGENTS.md")
                && File.Exists(Path.Combine(folder, "MyProject.csproj"))
                && File.Exists(Path.Combine(folder, ".gitignore"))
                && !File.Exists(Path.Combine(folder, "Core", "CurveUtilities.cs"))
                && !File.Exists(Path.Combine(folder, "Components", "Curve_Smooth.cs"))
                && !Directory.Exists(Path.Combine(folder, ".rhdock")),
                "New creates the clean working-repository template without test history or Curve Smooth");
            Check(File.Exists(Path.Combine(folder, "Components", "MyComponent.cs")),
                "New includes a discoverable starter Script");
            Check(File.ReadAllText(Path.Combine(folder, "Components", "CurveSmoothingComponent.cs")).Contains("ref bool Done")
                && File.ReadAllText(Path.Combine(folder, "AGENTS.md")).Contains("ref bool Done")
                && File.ReadAllText(Path.Combine(folder, "AGENTS.md")).Contains("ComponentIndex"),
                "New includes the Solver Script example and guidance for Solver Scripts and Sub-objects");
            try { WorkingRepositoryTemplate.Create(folder); throw new Exception("New overwrote an existing repository"); }
            catch (IOException) { }
            Check(File.Exists(Path.Combine(folder, "Components", "MyComponent.cs")),
                "New refuses a nonempty destination without changing it");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static void CheckPulse()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-pulse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Components"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Pulse.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Components", "Pulse.cs"),
                "public class Pulse { public void Run(bool fire, ref bool seen) { seen = fire; } }");
            using var compiler = new ScriptCompilerService();
            using var session = new SingleScriptSession(compiler);
            session.Open(folder);
            session.Select("Components/Pulse.cs");
            session.SetInput("fire", false);
            Check(session.RunPulse("fire").Status == "ok" && session.Outputs["seen"] is true
                && session.Inputs["fire"] is false && session.HasCurrentOutputs,
                "Boolean pulse Runs true once without changing committed input or clearing result");
            var record = SingleScriptWorkspaceStore.CaptureRun(session,
                new() { ["fire"] = SavedScalar.Capture(true)! }, []);
            Check(record.Inputs["fire"].Restore() is true && session.Run().Status == "ok"
                && session.Outputs["seen"] is false,
                "pulse Run Record captures actual true while later Run uses committed false");
            try { session.RunPulse("missing"); throw new Exception("accepted unknown pulse"); }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void CheckSolver()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-solver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Components"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Solver.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Components", "Counter.cs"), """
                namespace Fixture;
                public class Counter : System.IDisposable {
                    public static int Instances, Disposed;
                    int frame;
                    public Counter() { Instances++; }
                    public void Run(int limit, ref int Count, ref string Info, ref bool Done) {
                        frame++;
                        if (limit < 0) throw new System.InvalidOperationException("solver failure");
                        Count = frame;
                        Info = "frame " + frame;
                        Done = frame >= limit;
                    }
                    public void Dispose() { Disposed++; }
                }
                """);
            using var compiler = new ScriptCompilerService();
            using var session = new SingleScriptSession(compiler);
            session.Open(folder);
            session.Select("Components/Counter.cs");
            Check(session.IsSolver && session.Selected!.Ports.All(p => p.Name != "Done"),
                "ref bool Done marks a Solver Script and is not a Port");
            session.SetInput("limit", 3);
            session.Run(); session.Run();
            Check(session.Frame == 2 && Equals(session.Outputs["Count"], 2) && !session.Done && Counter(compiler, folder, "Instances") == 1,
                "Frames share one Script instance and its fields");
            Check(session.Run().Status == "ok" && session.Done && session.Frame == 3 && !session.Outputs.ContainsKey("Done"),
                "Done ends the Solve on the Script's Frame and stays hidden from Outputs");
            session.SetInput("limit", 5);
            Check(session.Frame == 0 && Counter(compiler, folder, "Disposed") == 1, "input change discards the Solve instance");
            session.Run();
            Check(Equals(session.Outputs["Count"], 1) && Counter(compiler, folder, "Instances") == 2, "next Frame after an input change starts a fresh Solve");
            session.ResetSolve();
            Check(session.Frame == 0 && !session.HasCurrentOutputs && Counter(compiler, folder, "Disposed") == 2, "Reset discards the instance and current outputs");
            session.SetInput("limit", -1);
            Check(session.Run().Status == "error" && session.Frame == 0 && Counter(compiler, folder, "Disposed") == 3, "a failed Frame ends the Solve");
            session.SetInput("limit", 5);
            session.Run();
            var weak = Retire(session, compiler, folder);
            for (var i = 0; i < 10 && weak.IsAlive; i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(20); }
            Check(!weak.IsAlive && session.Frame == 0, "a live Solve instance does not keep a released generation");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Counter(ScriptCompilerService compiler, string folder, string field) => (int)compiler.CompileProject(folder)
        .Assembly.GetType("Fixture.Counter", true)!.GetField(field)!.GetValue(null)!;

    private static string Source(string input, string expression, bool broken = false) => broken ? "public class Sample { syntax error }" : $$"""
        using System;
        using System.Linq;
        namespace Fixture;
        public class Sample {
            public static int Calls;
            public void Run({{input}}, ref double y, ref int calls) {
                Calls++;
                if ({{(input.StartsWith("double[]") ? "x.Any(v => v < 0)" : "x < 0")}}) throw new InvalidOperationException("fixture failure");
                y = {{expression}};
                calls = Calls;
            }
        }
        """;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Retire(SingleScriptSession session, ScriptCompilerService compiler, string folder)
    {
        var context = AssemblyLoadContext.GetLoadContext(compiler.CompileProject(folder).Assembly)!;
        var weak = new WeakReference(context);
        session.ReleaseGeneration();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Calls(ScriptCompilerService compiler, string folder) => (int)compiler.CompileProject(folder)
        .Assembly.GetType("Fixture.Sample", true)!.GetField("Calls")!.GetValue(null)!;

    public static void CheckEnum()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Components"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(folder, "Components", "Choice.cs"), "namespace Fixture; public enum Mode { Cylinder, Tangent, Cone, Rails } public class Choice { public void Run(ref string result, Mode mode = Mode.Cylinder) { result = mode.ToString(); } }");
            using var compiler = new ScriptCompilerService();
            using var session = new SingleScriptSession(compiler);
            session.Open(folder);
            session.Select("Components/Choice.cs");
            var port = session.Selected!.Ports.Single(p => p.Name == "mode");
            Check(port.DefaultValue == "Cylinder" && port.EnumValues.Length == 4, "enum metadata exposes choices and default");
            Check(session.Run().Status == "ok" && Equals(session.Outputs["result"], "Cylinder"), "unset enum uses Script default");
            session.SetInput("mode", "rails");
            Check(session.Inputs["mode"] is string name && name == "Rails", "enum input stores canonical string rather than a Script-owned object");
            Check(session.Run().Status == "ok" && Equals(session.Outputs["result"], "Rails"), "named choice reaches compiled enum parameter");
            foreach (object value in new object[] { "missing", 3 })
            {
                try { session.SetInput("mode", value); throw new Exception("Invalid enum was accepted"); }
                catch (ArgumentException) { }
            }
            var saved = SavedScalar.Capture(session.Inputs["mode"])!;
            session.SetInput("mode", saved.Restore());
            Check(session.Run().Status == "ok", "enum string survives persistence");
            session.SetInput("mode", null);
            Check(session.Run().Status == "ok" && Equals(session.Outputs["result"], "Cylinder"), "clearing choice restores default");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
}
