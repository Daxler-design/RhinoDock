namespace rhDock.Core;

/// <summary>A named visual group. The name is a display label; it never renames a Script parameter.</summary>
public sealed record LayoutGroup(string Name, string[] Ports)
{
    /// <summary>The group's own Preview switch. It hides the group's Previews without changing any port's own visibility.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreviewOff { get; init; }
}

/// <summary>Visual order of one panel section (Inputs or Outputs): named groups in order, then the Ungrouped ports.</summary>
public sealed record SectionLayout(LayoutGroup[] Groups, string[] Ungrouped);

/// <summary>The saved visual layout of one Script. <see cref="Rev"/> counts accepted writes so a stale writer is detected.</summary>
public sealed record ScriptLayout(int Rev, SectionLayout Inputs, SectionLayout Outputs);

public sealed record ScriptLayoutFile(int V, Dictionary<string, ScriptLayout> Scripts);

/// <summary>
/// Per-Script panel layout saved in the Loaded Repository (<c>.rhdock/layout.json</c>), apart from the Workspace history so rewinding
/// a Workspace never reorders the panel. A write names the revision it was based on; another Rhino's newer write wins and is reported,
/// never silently overwritten. Layout never affects Script execution or list-element order.
/// </summary>
public static class ScriptLayoutStore
{
    private const int MaxGroups = 64, MaxPorts = 512, MaxName = 40;

    public static string PathFor(string repository) => Path.Combine(repository, ".rhdock", "layout.json");

    private static string Key(string script) => script.Replace('\\', '/');

    /// <summary>The saved layout, or null when none exists or the file is unreadable (a bad file must never block the panel).</summary>
    public static ScriptLayout? Load(string repository, string script) => ReadAll(repository)?.Scripts.GetValueOrDefault(Key(script));

    private static ScriptLayoutFile? ReadAll(string repository)
    {
        var path = PathFor(repository);
        if (!File.Exists(path)) return null;
        try
        {
            var file = AtomicJson.Read<ScriptLayoutFile>(path);
            return file.V == 1 && file.Scripts is not null ? file : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Saves a layout based on <paramref name="expectedRev"/>. Returns false, with the newer saved layout in <paramref name="current"/>, when another writer got there first.</summary>
    public static bool TryReplace(string repository, string script, int expectedRev, SectionLayout inputs, SectionLayout outputs, out ScriptLayout? current)
    {
        Validate(inputs, outputs);
        using var lease = Lock(repository);
        var file = ReadAll(repository) ?? new(1, new(StringComparer.Ordinal));
        current = file.Scripts.GetValueOrDefault(Key(script));
        if ((current?.Rev ?? 0) != expectedRev) return false;
        current = new((current?.Rev ?? 0) + 1, inputs, outputs);
        file.Scripts[Key(script)] = current;
        AtomicJson.Write(PathFor(repository), file);
        return true;
    }

    private static FileStream Lock(string repository)
    {
        // Keep the lock file: deleting it would let another process lock a different file at the same path.
        var path = PathFor(repository) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (wait.ElapsedMilliseconds < 2000) { Thread.Sleep(20); }
        }
    }

    private static void Validate(SectionLayout inputs, SectionLayout outputs)
    {
        foreach (var section in new[] { inputs, outputs })
        {
            if (section?.Groups is null || section.Ungrouped is null || section.Groups.Length > MaxGroups)
                throw new InvalidDataException("Invalid layout section.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in section.Groups)
            {
                if (group is null || group.Ports is null || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > MaxName || !names.Add(group.Name.Trim()))
                    throw new InvalidDataException("Invalid or duplicate layout group name.");
                foreach (var port in group.Ports) if (string.IsNullOrEmpty(port) || !ports.Add(port)) throw new InvalidDataException("A port appears twice in the layout.");
            }
            foreach (var port in section.Ungrouped) if (string.IsNullOrEmpty(port) || !ports.Add(port)) throw new InvalidDataException("A port appears twice in the layout.");
            if (ports.Count > MaxPorts) throw new InvalidDataException("Layout has too many ports.");
        }
    }
}
