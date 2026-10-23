using System.Collections;
using System.Globalization;
using rhDock.Core;

namespace rhDock.Extensions;

/// <summary>The active input row. Index is null for a single value; this state is not saved.</summary>
public sealed record ScriptInputItem(string Name, int? Index);

/// <summary>An explicit list move. Extensions decide whether another input should follow it.</summary>
public sealed record ScriptInputReorder(string Name, int From, int To);

internal static class InputItems
{
    internal static object Parse(string type, string text) => type switch
    {
        "System.String" => text,
        "System.Boolean" => bool.Parse(text),
        "System.Int32" => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "System.Double" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => number,
        "System.Drawing.Color" or "Rhino.Display.Color4f" => ColorText.Parse(text) ?? throw new FormatException("Enter a valid color."),
        _ => throw new FormatException("Enter a valid value for " + type + "."),
    };

    internal static Array Replace(IEnumerable source, int index, object? value)
    {
        var copy = Copy(source);
        if (index < 0 || index >= copy.Length) throw new ArgumentOutOfRangeException(nameof(index));
        copy.SetValue(value, index);
        return copy;
    }

    internal static Array Move(IEnumerable source, int from, int to)
    {
        var copy = Copy(source);
        if (from < 0 || to < 0 || from >= copy.Length || to >= copy.Length)
            throw new ArgumentOutOfRangeException(nameof(from));
        var moved = copy.GetValue(from);
        for (var i = from; i != to; i += from < to ? 1 : -1)
            copy.SetValue(copy.GetValue(i + (from < to ? 1 : -1)), i);
        copy.SetValue(moved, to);
        return copy;
    }

    internal static int MovedIndex(int index, int from, int to) => index == from ? to
        : from < to && index > from && index <= to ? index - 1
        : from > to && index >= to && index < from ? index + 1 : index;

    private static Array Copy(IEnumerable source)
    {
        if (source is Array array) return (Array)array.Clone();
        var element = source.GetType().GetInterfaces().First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];
        var items = source.Cast<object?>().ToArray();
        var copy = Array.CreateInstance(element, items.Length);
        for (var i = 0; i < items.Length; i++) copy.SetValue(items[i], i);
        return copy;
    }
}
