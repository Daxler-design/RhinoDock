using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace rhDock.Core;

public sealed record SingleScriptOutputRecord(string Type, int Count, string? Value);

public sealed record SingleScriptRunRecord(
    DateTime Utc, string Script, string Status, long ElapsedMs, string? Error,
    string[] Diagnostics, Dictionary<string, SavedScalar> Inputs,
    Dictionary<string, SavedPick> Picks, Dictionary<string, SingleScriptOutputRecord> Outputs)
{
    public int? Pid { get; init; }
}

public sealed record SavedScriptState(
    Dictionary<string, SavedScalar> Inputs, Dictionary<string, SavedPick> Picks,
    string[] HiddenPreview, string[] OmittedInputs,
    SingleScriptRunRecord? LastRun)
{
    public Dictionary<string, NumericRange>? NumericRanges { get; init; }
    public Dictionary<string, string>? BooleanStyles { get; init; }
    public Dictionary<string, string>? PreviewColors { get; init; }
    public Dictionary<string, PreviewStyle>? PreviewStyles { get; init; }
    public double? FrameRate { get; init; }
}

public sealed record NumericRange(double Min, double Max, double Step);

public sealed record WorkspaceRevision(
    DateTime Utc, string Script, string Mode, Dictionary<string, SavedScriptState> Scripts)
{
    public int? Pid { get; init; }
}

public sealed record SingleScriptWorkspaceHistory(int V, WorkspaceRevision[] Revisions);

/// <summary>Reduced, graph-free Workspace saved inside its Loaded Repository.</summary>
public static class SingleScriptWorkspaceStore
{
    public static string PathFor(string repository) => Path.Combine(repository, ".rhdock", "workspace.json");

    public static SingleScriptWorkspaceHistory? LoadHistory(string repository)
    {
        var path = PathFor(repository);
        if (!File.Exists(path)) return null;
        var history = AtomicJson.Read<SingleScriptWorkspaceHistory>(path);
        ValidateHistory(history);
        return history with { Revisions = history.Revisions.Select(revision => revision with
        {
            Script = NormalizeScript(revision.Script),
            Scripts = revision.Scripts.ToDictionary(pair => NormalizeScript(pair.Key), pair => pair.Value, StringComparer.Ordinal)
        }).ToArray() };
    }

    private static string NormalizeScript(string script) => script.Replace('\\', '/');

    public static SingleScriptWorkspaceHistory Append(string repository, WorkspaceRevision revision)
    {
        var path = PathFor(repository);
        SingleScriptWorkspaceHistory? previous;
        try { previous = LoadHistory(repository); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // A copied, edited, or older file must never block saving: keep it beside workspace.json and start fresh.
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, $"workspace.bad.{stamp}.json"));
            previous = null;
        }
        var history = new SingleScriptWorkspaceHistory(3,
            (previous?.Revisions ?? []).Append(revision).TakeLast(16).ToArray());
        ValidateHistory(history);
        AtomicJson.Write(path, history);
        return history;
    }

    public static void Export(string repository, WorkspaceRevision revision, string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetDirectoryName(Path.GetFullPath(PathFor(repository))))
            throw new InvalidOperationException("Export outside the .rhdock runtime folder.");
        var exported = new SingleScriptWorkspaceHistory(3, [revision]);
        ValidateHistory(exported);
        AtomicJson.Write(path, exported);
    }

    public static SingleScriptRunRecord CaptureRun(SingleScriptSession session,
        Dictionary<string, SavedScalar> inputs, Dictionary<string, SavedPick> picks)
    {
        var script = session.Selected ?? throw new InvalidOperationException("Select a Script before recording a Run.");
        var outputs = new Dictionary<string, SingleScriptOutputRecord>(StringComparer.Ordinal);
        if (session.Result.Status == "ok")
            foreach (var (name, value) in session.Outputs)
            {
                var declared = script.Ports.FirstOrDefault(port => port.Name == name && port.IsOutput)?.TypeName;
                outputs[name] = Summarize(value, declared);
            }
        return new(DateTime.UtcNow, script.EntryFile, session.Result.Status, session.Result.ElapsedMs,
            Limit(session.Result.Error), session.Result.Log.Take(50).Select(line => Limit(line) ?? "").ToArray(),
            new(inputs, StringComparer.Ordinal), new(picks, StringComparer.Ordinal), outputs) { Pid = Environment.ProcessId };
    }

    private static SingleScriptOutputRecord Summarize(object? value, string? declared)
    {
        var type = declared ?? value?.GetType().FullName ?? "unknown";
        if (value is null) return new(type, 0, null);
        if (ScalarText(value) is { } scalar) return new(type, 1, Limit(scalar));
        if (value is ICollection collection)
        {
            var values = collection.Cast<object?>().Take(20).Select(ScalarText).ToArray();
            var text = values.All(item => item is not null)
                ? string.Join(", ", values) + (collection.Count > 20 ? ", ..." : "") : null;
            return new(type, collection.Count, Limit(text));
        }
        return new(type, 1, null);
    }

    private static string? ScalarText(object? value) => value switch
    {
        string text => text,
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? Limit(string? value) => value is { Length: > 2000 } ? value[..2000] + "..." : value;

    private static void ValidateHistory(SingleScriptWorkspaceHistory history)
    {
        if (history.V != 3 || history.Revisions is not { Length: > 0 and <= 16 })
            throw new InvalidDataException("Invalid single-Script Workspace history.");
        foreach (var revision in history.Revisions)
        {
            if (revision is null || string.IsNullOrWhiteSpace(revision.Script)
                || revision.Mode is not ("manual" or "auto") || revision.Scripts is null
                || !revision.Scripts.ContainsKey(revision.Script))
                throw new InvalidDataException("Incomplete Workspace revision.");
            foreach (var state in revision.Scripts.Values) ValidateState(state);
        }
    }

    private static void ValidateState(SavedScriptState state)
    {
        if (state is null || state.Inputs is null || state.Picks is null
            || state.HiddenPreview is null || state.OmittedInputs is null)
            throw new InvalidDataException("Incomplete saved Script state.");
        if (state.Inputs.Keys.Intersect(state.Picks.Keys, StringComparer.Ordinal).Any())
            throw new InvalidDataException("An input cannot be both a value and a pick.");
        foreach (var value in state.Inputs.Values)
        {
            if (value is null) throw new InvalidDataException("Invalid saved input.");
            _ = value.Restore();
        }
        foreach (var pick in state.Picks.Values)
        {
            static bool BadPoint(double[]? point) => point is null || point.Length != 3 || point.Any(n => !double.IsFinite(n));
            if (pick is null || pick.Kind is not ("objects" or "points" or "components")
                || pick.Kind is "objects" or "components" && (pick.Ids is not { Length: > 0 } || pick.Ids.Any(id => !Guid.TryParse(id, out _)))
                || pick.Kind == "components" && (pick.Components is null || pick.Components.Length != pick.Ids!.Length
                    || pick.Components.Any(c => c is null || string.IsNullOrWhiteSpace(c.Type) || c.Index < 0))
                || pick.Kind == "points" && (pick.Points is not { Length: > 0 } || pick.Points.Any(BadPoint)))
                throw new InvalidDataException("Invalid saved Rhino pick.");
        }
        if (state.NumericRanges is not null && state.NumericRanges.Values.Any(range =>
            range is null || !double.IsFinite(range.Min) || !double.IsFinite(range.Max)
            || !double.IsFinite(range.Step) || range.Min >= range.Max || range.Step <= 0))
            throw new InvalidDataException("Invalid numeric control range.");
        if (state.BooleanStyles is not null && state.BooleanStyles.Values.Any(style => style is not ("press" or "switch")))
            throw new InvalidDataException("Invalid Boolean control style.");
        if (state.PreviewColors is not null && state.PreviewColors.Values.Any(color =>
            color is null || !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")))
            throw new InvalidDataException("Invalid Preview color.");
        if (state.PreviewStyles is not null && state.PreviewStyles.Values.Any(style => style is null || !style.IsValid))
            throw new InvalidDataException("Invalid Preview Style.");
        if (state.FrameRate is { } rate && rate is not (0 or 1 or 10 or 30))
            throw new InvalidDataException("Invalid Solve frame rate.");
    }
}
