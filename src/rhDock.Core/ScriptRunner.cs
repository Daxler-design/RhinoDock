using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace rhDock.Core;

public sealed record OperationInfo(string EntryFile, string ClassName, string Name, string Category, string Description, PortDescriptor[] Ports)
{
    /// <summary>True when Run declares <c>ref bool Done</c>: the host calls it once per Frame with one kept instance.</summary>
    public bool IsSolver { get; init; }
}

public sealed class ScriptRunner
{
    private readonly ScriptCompilerService compiler;
    public ScriptRunner(ScriptCompilerService compiler) => this.compiler = compiler;

    public RunResult Execute(string projectFolder, string entryFile, IPortBag bag)
    {
        var sw = Stopwatch.StartNew();
        lock (compiler.Gate)
        {
            ScriptRunMethod? method = null;
            try
            {
                method = ScriptRunMethod.Find(compiler.CompileProject(projectFolder).ResolveEntryType(entryFile));
                var statusLine = method.Invoke(bag);
                return new("ok", sw.ElapsedMilliseconds, null, compiler.Messages.ToArray()) { StatusLine = statusLine };
            }
            catch (Exception ex)
            {
                if (method is not null)
                    foreach (var output in method.Outputs)
                        try { bag.SetOutput(output.Name!, null); } catch { /* Preserve the original failure. */ }
                while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
                return new(ex is ScriptCompileException ? "compile_error" : "error", sw.ElapsedMilliseconds,
                    ex.Message, compiler.Messages.ToArray());
            }
        }
    }

    /// <summary>
    /// Runs one Frame of a Solver Script. The caller owns <paramref name="instance"/> for the Solve and must drop it
    /// before the generation is released; an instance from an older generation is replaced.
    /// </summary>
    public RunResult ExecuteFrame(string projectFolder, string entryFile, IPortBag bag, ref object? instance, out bool done)
    {
        var sw = Stopwatch.StartNew();
        done = false;
        lock (compiler.Gate)
        {
            ScriptRunMethod? method = null;
            try
            {
                method = ScriptRunMethod.Find(compiler.CompileProject(projectFolder).ResolveEntryType(entryFile));
                if (instance is not null && instance.GetType() != method.DeclaringType) { ScriptRunMethod.Release(instance); instance = null; }
                instance ??= method.CreateInstance();
                var statusLine = method.Invoke(bag, instance, out done);
                return new("ok", sw.ElapsedMilliseconds, null, compiler.Messages.ToArray()) { StatusLine = statusLine };
            }
            catch (Exception ex)
            {
                if (method is not null)
                    foreach (var output in method.Outputs)
                        try { bag.SetOutput(output.Name!, null); } catch { /* Preserve the original failure. */ }
                while (ex is TargetInvocationException { InnerException: not null }) ex = ex.InnerException;
                return new(ex is ScriptCompileException ? "compile_error" : "error", sw.ElapsedMilliseconds,
                    ex.Message, compiler.Messages.ToArray());
            }
        }
    }

    public PortDescriptor[] Describe(string projectFolder, string entryFile)
    {
        lock (compiler.Gate) return ScriptRunMethod.Describe(compiler.CompileProject(projectFolder).ResolveEntryType(entryFile));
    }

    public OperationInfo[] ListOperations(string projectFolder)
    {
        lock (compiler.Gate)
        {
            var project = compiler.CompileProject(projectFolder);
            var list = new List<OperationInfo>();
            foreach (var entry in project.EntryCandidates.Where(p => p.Key.StartsWith("Components/", StringComparison.OrdinalIgnoreCase)))
                foreach (var name in entry.Value)
                {
                    var type = project.Assembly.GetType(name, true)!;
                    if (type.IsAbstract || type.ContainsGenericParameters || !type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Any(m => m.Name == "Run")) continue;
                    string Metadata(string field, string fallback) => type.GetField(field, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true, FieldType: var ft } f && ft == typeof(string)
                        ? (string?)f.GetRawConstantValue() ?? fallback : fallback;
                    list.Add(new(entry.Key, name, Metadata("Name", type.Name), Metadata("Category", ""), Metadata("Description", ""), ScriptRunMethod.Describe(type)) { IsSolver = ScriptRunMethod.Find(type).IsSolver });
                }
            return list.OrderBy(c => c.Category).ThenBy(c => c.Name).ToArray();
        }
    }
}

internal sealed class ScriptRunMethod
{
    private readonly MethodInfo method;
    private readonly ParameterInfo[] parameters;
    private ScriptRunMethod(MethodInfo method) { this.method = method; parameters = method.GetParameters(); }
    public string SignatureKey => string.Join("|", new[] { method.ReturnType.FullName ?? method.ReturnType.Name }
        .Concat(parameters.Select(p => $"{p.Name}:{p.ParameterType.FullName}:{p.IsOut}:{p.ParameterType.IsByRef}")));
    public IEnumerable<ParameterInfo> Outputs => parameters.Where(p => IsOutputParameter(p) && !IsDone(p));
    public bool IsSolver => parameters.Any(IsDone);
    public Type DeclaringType => method.DeclaringType!;

    /// <summary>The Solver Script control flag: <c>ref bool Done</c> or <c>out bool Done</c>. It is not an output Port.</summary>
    public static bool IsDone(ParameterInfo p) => p.Name == "Done" && IsOutputParameter(p) && GetEffectiveType(p) == typeof(bool);

    public static ScriptRunMethod Find(Type type) => new(type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(m => m.Name == "Run").OrderBy(m => m.GetParameters() is var p && p.Length == 1 && p[0].ParameterType.Name == "GhScriptContext" ? 1 : 0)
        .FirstOrDefault() ?? throw new MissingMethodException(type.FullName, "Run"));

    public static PortDescriptor[] Describe(Type type) => Find(type).parameters.Where(p => !IsDone(p)).Select(p =>
        new PortDescriptor(p.Name!, GetEffectiveType(p).FullName ?? GetEffectiveType(p).Name,
            GetAccess(GetEffectiveType(p)), IsOutputParameter(p), p.HasDefaultValue, null)
        {
            DefaultValue = p.HasDefaultValue ? Convert.ToString(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) : null,
            EnumValues = GetEffectiveType(p).IsEnum ? Enum.GetNames(GetEffectiveType(p)) : []
        }).ToArray();

    public string? Invoke(IPortBag bag)
    {
        var instance = CreateInstance();
        try { return Invoke(bag, instance, out _); }
        finally { Release(instance); }
    }

    public object CreateInstance() => Activator.CreateInstance(method.DeclaringType!) ?? throw new InvalidOperationException("Cannot create script instance.");
    public static void Release(object? instance) => (instance as IDisposable)?.Dispose();

    public string? Invoke(IPortBag bag, object instance, out bool done)
    {
        done = false;
        var args = new object?[parameters.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var p = parameters[i];
            var type = GetEffectiveType(p);
            if (IsDone(p)) { args[i] = false; continue; }
            var value = IsOutputParameter(p) ? null : PortUnwrap.Unwrap(bag.GetInput(p.Name!));
            args[i] = value is null ? p.HasDefaultValue ? p.DefaultValue :
                Nullable.GetUnderlyingType(p.ParameterType) is not null ? null : type.IsValueType ? Activator.CreateInstance(type) : null : ConvertValue(value, type);
        }
        method.Invoke(instance, args);
        string? statusLine = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (IsDone(parameters[i])) { done = args[i] is true; continue; }
            if (!IsOutputParameter(parameters[i])) continue;
            var value = PortUnwrap.Unwrap(args[i]);
            bag.SetOutput(parameters[i].Name!, value);
            if (parameters[i].Name!.Equals("Info", StringComparison.OrdinalIgnoreCase) && value is string text)
                statusLine ??= text;
        }
        return statusLine;
    }

    private static object? ConvertValue(object? value, Type type)
    {
        try { return ConvertValueCore(value, type); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or ArgumentException or OverflowException)
        { throw new InvalidOperationException($"type mismatch: expected {type.Name}, got {PortUnwrap.Unwrap(value)?.GetType().Name ?? "null"}", ex); }
    }

    private static object? ConvertValueCore(object? value, Type type)
    {
        value = PortUnwrap.Unwrap(value);
        if (value is null || type.IsInstanceOfType(value)) return value;
        // A string is an IEnumerable<char>; enumerating one would turn unparsed list text into character codes.
        if (GetAccess(type) == PortAccess.List && value is IEnumerable sequence && value is not string)
        {
            var element = GetElementType(type);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in sequence) list.Add(ConvertValue(item, element));
            if (type.IsArray) { var array = Array.CreateInstance(element, list.Count); list.CopyTo(array, 0); return array; }
            if (type.IsInstanceOfType(list)) return list;
        }
        if (type.IsEnum) return value is string s ? Enum.Parse(type, s, true) : Enum.ToObject(type, value);
        // Struct constructors bridge host values to richer value types, e.g. System.Drawing.Color → Rhino Color4f.
        // Primitive sources stay excluded so an int never becomes a capacity or packed-ARGB constructor call.
        if (type.IsValueType && !value.GetType().IsPrimitive && type.GetConstructor([value.GetType()]) is { } constructor) return constructor.Invoke([value]);
        return Convert.ChangeType(value, type);
    }

    public static bool IsOutputParameter(ParameterInfo p) => p.IsOut || p.ParameterType.IsByRef;
    public static Type GetEffectiveType(ParameterInfo p)
    {
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() ?? typeof(object) : p.ParameterType;
        return Nullable.GetUnderlyingType(type) ?? type;
    }
    public static PortAccess GetAccess(Type type)
    {
        if (type.GetInterfaces().Append(type).Any(t => t.Name is "DataTree`1" or "GH_Structure`1" or "IGH_DataTree" or "IGH_Structure")) return PortAccess.Tree;
        return type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) ? PortAccess.List : PortAccess.Item;
    }
    public static Type GetElementType(Type type) => type.IsArray ? type.GetElementType() ?? typeof(object) :
        type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object);
}
