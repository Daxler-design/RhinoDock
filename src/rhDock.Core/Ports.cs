namespace rhDock.Core;

public interface IPortBag
{
    object? GetInput(string name);
    void SetOutput(string name, object? value);
}

public interface IReloadable
{
    void OnSourceChanged();
    void OnEntryRenamed(string oldRel, string newRel);
}

public sealed record PortDescriptor(string Name, string TypeName, PortAccess Access,
    bool IsOutput, bool IsOptional, string? Tooltip)
{
    public string? DefaultValue { get; init; }
    public string[] EnumValues { get; init; } = [];
}
public enum PortAccess { Item, List, Tree }
public sealed record RunResult(string Status, long ElapsedMs, string? Error, IReadOnlyList<string> Log)
{
    public string? StatusLine { get; init; }
}
