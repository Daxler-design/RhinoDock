using System.Collections.ObjectModel;

namespace rhDock.Core;

/// <summary>One selected Script and its current, generation-scoped result.</summary>
public sealed class SingleScriptSession : IDisposable
{
    private readonly ScriptCompilerService compiler;
    private readonly ScriptRunner runner;
    private readonly Dictionary<string, object?> inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> outputs = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, object?> inputView;
    private readonly IReadOnlyDictionary<string, object?> outputView;
    private string? folder;
    private string? selectedEntry;
    private object? solveInstance;
    private bool disposed;

    public SingleScriptSession(ScriptCompilerService compiler)
    {
        this.compiler = compiler;
        runner = new(compiler);
        inputView = new ReadOnlyDictionary<string, object?>(inputs);
        outputView = new ReadOnlyDictionary<string, object?>(outputs);
    }

    public string? Repository => folder;
    public IReadOnlyList<OperationInfo> Scripts { get; private set; } = [];
    public OperationInfo? Selected { get; private set; }
    public IReadOnlyDictionary<string, object?> Inputs => inputView;
    public IReadOnlyDictionary<string, object?> Outputs => outputView;
    public RunResult Result { get; private set; } = new("pending", 0, null, []);
    public bool Dirty { get; private set; } = true;
    public bool HasCurrentOutputs => !Dirty && Result.Status == "ok";
    /// <summary>The selected Script declares <c>ref bool Done</c>; each Run is one Frame of a Solve.</summary>
    public bool IsSolver => Selected?.IsSolver == true;
    /// <summary>Frames completed in the current Solve; 0 when no Solve is in progress.</summary>
    public int Frame { get; private set; }
    /// <summary>The Solver Script reported <c>Done = true</c> on the latest Frame.</summary>
    public bool Done { get; private set; }

    public void Open(string repository)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var path = Path.GetFullPath(repository);
        if (folder is not null) ReleaseGeneration();
        EndSolve();
        folder = path;
        selectedEntry = null;
        Scripts = runner.ListOperations(path);
        Selected = null;
        MarkDirty();
    }

    public void Select(string entryFile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Selected = Scripts.FirstOrDefault(script => script.EntryFile == entryFile)
            ?? throw new ArgumentException("Script not found: " + entryFile, nameof(entryFile));
        selectedEntry = Selected.EntryFile;
        EndSolve();
        inputs.Clear();
        MarkDirty();
    }

    public void SetInput(string name, object? value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var port = Selected?.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput)
            ?? throw new ArgumentException("Input not found: " + name, nameof(name));
        if (value is not null && port.EnumValues.Length > 0)
            value = value is string choice
                ? port.EnumValues.FirstOrDefault(option => option.Equals(choice, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Unknown choice for " + name + ": " + choice)
                : throw new ArgumentException("Enum inputs require a named choice.", nameof(value));
        if (value is null) inputs.Remove(port.Name);
        else inputs[port.Name] = value;
        EndSolve();
        MarkDirty();
    }

    /// <summary>Runs once, or for a Solver Script runs the next Frame of the current Solve.</summary>
    public RunResult Run() => RunCore(inputs);

    /// <summary>Discards the Solve and its Script instance; the next Run starts at Frame 1.</summary>
    public void ResetSolve()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EndSolve();
        MarkDirty();
    }

    private void EndSolve()
    {
        var instance = solveInstance;
        solveInstance = null;
        Frame = 0;
        Done = false;
        try { (instance as IDisposable)?.Dispose(); } catch { /* A failing Script Dispose must not keep its generation alive. */ }
    }

    public RunResult RunPulse(string name)
    {
        if (Selected?.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput) is not { TypeName: "System.Boolean", Access: PortAccess.Item })
            throw new ArgumentException("A pulse requires a Boolean item input.", nameof(name));
        var values = new Dictionary<string, object?>(inputs, StringComparer.Ordinal) { [name] = true };
        return RunCore(values);
    }

    private RunResult RunCore(IReadOnlyDictionary<string, object?> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (folder is null || Selected is null) throw new InvalidOperationException("Open a repository and select a Script before Run.");
        MarkDirty();
        foreach (var port in Selected.Ports.Where(p => !p.IsOutput && !p.IsOptional))
            if (!values.ContainsKey(port.Name))
            {
                Dirty = false;
                return Result = new("error", 0, $"missing input: {port.Name}", []);
            }
        foreach (var port in Selected.Ports.Where(p => !p.IsOutput && p.Access == PortAccess.Item))
            if (values.GetValueOrDefault(port.Name) is System.Collections.IEnumerable and not string && port.TypeName != "System.Object")
            {
                Dirty = false;
                return Result = new("error", 0, $"type mismatch: {port.Name} expects a scalar value", []);
            }

        var bag = new RunBag(values);
        if (Selected.IsSolver)
        {
            if (Done) EndSolve();
            Result = runner.ExecuteFrame(folder, Selected.EntryFile, bag, ref solveInstance, out var done);
            if (Result.Status == "ok") { Frame++; Done = done; }
            else EndSolve();
        }
        else Result = runner.Execute(folder, Selected.EntryFile, bag);
        Dirty = false;
        if (Result.Status == "ok")
        {
            foreach (var output in bag.Outputs) outputs[output.Key] = output.Value;
        }
        return Result;
    }

    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (folder is null) return;
        var entry = selectedEntry;
        var previousSelected = Selected;
        var oldPorts = Selected?.Ports.ToDictionary(p => p.Name, StringComparer.Ordinal) ?? [];
        var held = inputs.ToArray();
        EndSolve();
        ReleaseGeneration();
        try
        {
            Scripts = runner.ListOperations(folder);
            Selected = Scripts.FirstOrDefault(script => script.EntryFile == entry);
        }
        catch (ScriptCompileException ex)
        {
            Selected = previousSelected;
            foreach (var (name, value) in held)
                if (IsHostValue(value)) inputs[name] = value;
            Result = new("compile_error", 0, ex.Message, compiler.Messages.ToArray());
            return;
        }
        if (Selected is not null)
            foreach (var (name, value) in held)
                if (oldPorts.TryGetValue(name, out var old) && Selected.Ports.FirstOrDefault(p => p.Name == name && !p.IsOutput) is { } current
                    && old.TypeName == current.TypeName && old.Access == current.Access && IsHostValue(value)) inputs[name] = value;
    }

    public void ReleaseGeneration()
    {
        if (folder is null) return;
        EndSolve();
        MarkDirty();
        inputs.Clear();
        Selected = null;
        Scripts = [];
        compiler.Invalidate(folder);
    }

    private void MarkDirty()
    {
        Dirty = true;
        outputs.Clear();
        Result = new("pending", 0, null, []);
    }

    private static bool IsHostValue(object? value) => value is null || value.GetType().Assembly.IsCollectible == false
        && (value is not System.Collections.IEnumerable sequence || value is string || sequence.Cast<object?>().All(IsHostValue));

    public void Dispose()
    {
        if (disposed) return;
        ReleaseGeneration();
        disposed = true;
    }

    private sealed class RunBag(IReadOnlyDictionary<string, object?> inputs) : IPortBag
    {
        public Dictionary<string, object?> Outputs { get; } = new(StringComparer.Ordinal);
        public object? GetInput(string name) => inputs.GetValueOrDefault(name);
        public void SetOutput(string name, object? value) => Outputs[name] = value;
    }
}
