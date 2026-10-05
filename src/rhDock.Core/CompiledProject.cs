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

    public void Dispose() => loadContext.Unload();
}

public sealed class ScriptCompileException : Exception
{
    public ScriptCompileException(string message) : base(message) { }
}
