using rhDock.Rhino;

internal static class ScriptInputUndoChecks
{
    public static void Run() => new SingleScriptHost().CheckInputUndo();
}

namespace rhDock.Rhino
{
    sealed partial class SingleScriptHost
    {
        // Compile the real history without RhinoDoc: any native history dependency fails compilation.
        private bool disposed = false, running = false;
        private object? activePick = null;

        internal void CheckInputUndo()
        {
            var folder = Path.Combine(Path.GetTempPath(), "rhdock-undo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Components"));
            try
            {
                File.WriteAllText(Path.Combine(folder, "Fixture.csproj"), "<Project />");
                File.WriteAllText(Path.Combine(folder, "Components", "Sample.cs"), "public class Sample { public void Run(double x, System.Collections.Generic.List<double> values, ref double y) { y = x; } }");
                Session.Open(folder);
                Session.Select(Session.Scripts.Single().EntryFile);
                SetInput("x", 1.0);
                SetInput("x", 2.0);
                UndoInput(false); Require((double)Session.Inputs["x"]! == 1, "Undo restores previous input");
                UndoInput(true); Require((double)Session.Inputs["x"]! == 2, "Redo restores edited input");
                UndoInput(false);
                SetInput("x", 1.0);
                UndoInput(true); Require((double)Session.Inputs["x"]! == 2, "No-op preserves Redo");
                UndoInput(false);
                SetInput("x", 3.0);
                UndoInput(true); Require((double)Session.Inputs["x"]! == 3, "New edit clears Redo");
                SetInput("x", 3.0);
                UndoInput(false); Require((double)Session.Inputs["x"]! == 1, "No-op consumes no Undo");
                var values = new List<double> { 1, 2 };
                SetInput("values", values);
                SetInput("values", new List<double> { 4, 5 });
                values[0] = 99;
                UndoInput(false);
                Require(((List<double>)Session.Inputs["values"]!)[0] == 1, "History owns copied list values");
                UndoInput(false);
                Require(!Session.Inputs.ContainsKey("values"), "Undo restores unset input");
                UndoInput(true);
                Require(((List<double>)Session.Inputs["values"]!).SequenceEqual(new double[] { 1, 2 }), "Redo restores list");
                Revision++;
                UndoInput(false);
                Require(Session.Inputs.ContainsKey("values") && inputUndo.Count == 0 && inputRedo.Count == 0, "Revision change clears history");
                for (var i = 0; i < 120; i++) SetInput("x", (double)i);
                Require(inputUndo.Count == 100, "History is bounded");
                running = true;
                UndoInput(false); Require((double)Session.Inputs["x"]! == 119, "Running blocks replay");
                running = false;
                ClearInputHistory();
                UndoInput(false); Require((double)Session.Inputs["x"]! == 119, "Document reset clears history");
                Console.WriteLine("PASS: isolated input Undo/Redo, copied lists, unset values, no-op/branching, revision reset, bounded history; no RhinoDoc dependency.");
            }
            finally { Session.Dispose(); Directory.Delete(folder, true); }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new Exception(message); }
    }
}
