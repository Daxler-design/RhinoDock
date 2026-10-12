using System.Reflection;

namespace rhDock.Core;

public sealed class CompiledProject : IDisposable
{
    private readonly ScriptAssemblyLoadContext loadContext;

    public string ProjectFolder { get; }

    public Assembly Assembly { get; }

    public DateTime LastWriteUtc { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> EntryCandidates { get; }

    internal CompiledProject(
        string projectFolder,
        Assembly assembly,
        ScriptAssemblyLoadContext loadContext,
        DateTime lastWriteUtc,
        IReadOnlyDictionary<string, IReadOnlyList<string>> entryCandidates)
    {
        ProjectFolder = projectFolder;
        Assembly = assembly;
        this.loadContext = loadContext;
        LastWriteUtc = lastWriteUtc;
        EntryCandidates = entryCandidates;
    }

    public Type ResolveEntryType(string entryFile)
    {
        if (string.IsNullOrWhiteSpace(entryFile))
            throw new MissingMethodException("Could not resolve an entry file from the operation name.");

        var normalized = entryFile.Replace('\\', '/');
        if (!EntryCandidates.TryGetValue(normalized, out var candidates))
            EntryCandidates.TryGetValue(Path.GetFileName(normalized), out candidates);
        if (candidates is null)
            throw new MissingMethodException("Entry file not found in project: " + entryFile);

        return candidates.Count switch
        {
            0 => throw new MissingMethodException("No class with Run(...) found in " + entryFile + "."),
            1 => Assembly.GetType(candidates[0], throwOnError: false)
                ?? throw new MissingMethodException("Entry class not found: " + candidates[0]),
            _ => throw new MissingMethodException(
                "Multiple Run classes found in " + entryFile + "; keep one entry class per operation file.")
        };
    }

    /// <summary>A weak handle to this generation's load context, to verify that it is collected after <see cref="Dispose"/>.</summary>
    public WeakReference LoadContextReference() => new(loadContext);

    public void Dispose()
    {
        loadContext.Unload();
        ClearJsonCache();
    }

    // System.Text.Json caches type metadata process-wide; a generation's types serialized by the host (Extension Post)
    // or by its own code would keep its load context alive. This is the internal handler .NET hot reload calls.
    private static readonly MethodInfo? JsonClearCache = typeof(System.Text.Json.JsonSerializer).Assembly
        .GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler")
        ?.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static void ClearJsonCache() => JsonClearCache?.Invoke(null, new object?[] { null });
}

public sealed class ScriptCompileException : Exception
{
    public ScriptCompileException(string message) : base(message) { }
}
