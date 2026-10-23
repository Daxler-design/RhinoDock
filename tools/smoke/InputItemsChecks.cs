using rhDock.Extensions;

internal static class InputItemsChecks
{
    public static void Run()
    {
        var values = new List<double> { 10, 20, 30, 40 };
        Check(InputItems.Replace(values, 1, 25.0) is double[] changed && changed.SequenceEqual(new[] { 10.0, 25, 30, 40 })
            && values[1] == 20, "item edits preserve type and leave the source unchanged");
        Check(InputItems.Replace(new[] { "a,b", "c" }, 1, "") is string[] text && text.SequenceEqual(new[] { "a,b", "" }),
            "string elements retain commas and empty values");
        for (var from = 0; from < values.Count; from++)
            for (var to = 0; to < values.Count; to++)
            {
                var moved = (double[])InputItems.Move(values, from, to);
                for (var active = 0; active < values.Count; active++)
                    if (moved[InputItems.MovedIndex(active, from, to)] != values[active]) throw new Exception("Active item did not follow the move.");
            }
        Console.WriteLine("PASS: active item follows every move");
        try { InputItems.Move(Array.Empty<double>(), 0, 0); throw new Exception("An empty list accepted a move."); }
        catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: empty lists reject moves"); }
        try { InputItems.Replace(values, -1, 1.0); throw new Exception("A negative index was accepted."); }
        catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: invalid item indices are rejected"); }
        Check((double)InputItems.Parse("System.Double", "1.25") == 1.25 && (string)InputItems.Parse("System.String", "a,b") == "a,b",
            "item text uses invariant numbers and preserves strings");
        foreach (var invalid in new[] { "NaN", "Infinity", "1e999", "bad" })
        {
            try { InputItems.Parse("System.Double", invalid); throw new Exception("Invalid numeric text accepted."); }
            catch (FormatException) { }
        }
        Console.WriteLine("PASS: invalid and non-finite numeric items are rejected");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
