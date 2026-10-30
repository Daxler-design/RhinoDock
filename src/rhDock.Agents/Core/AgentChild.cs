using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace rhDock.Agents;

/// <summary>A child process and its owned redirected streams, including Windows processes started with an explicit handle list.</summary>
public sealed class AgentChild : IDisposable
{
    public Process Process { get; }
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public bool HasExited => Process.HasExited;
    public bool WaitForExit(int milliseconds) => Process.WaitForExit(milliseconds);
    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);

    internal AgentChild(Process process, StreamWriter input, StreamReader output, StreamReader error)
    { Process = process; StandardInput = input; StandardOutput = output; StandardError = error; }

    internal static AgentChild Start(ProcessStartInfo info)
    {
        if (OperatingSystem.IsWindows()) return WindowsAgentStart.Start(info);
        var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("The agent did not start.");
        return new(process, process.StandardInput, process.StandardOutput, process.StandardError);
    }

    internal void ReadErrors(Action<string>? log) => _ = Task.Run(async () =>
    {
        try { while (await StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) if (line.Length != 0) log?.Invoke("stderr " + line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    });

    public void Dispose()
    {
        try { StandardInput.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
            Process.Dispose();
        }
    }
}
