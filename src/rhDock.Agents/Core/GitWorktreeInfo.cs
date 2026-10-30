using System.Diagnostics;
using System;
using System.IO;

namespace rhDock.Agents;

public sealed record GitWorktreeInfo(string Root, string Branch)
{
    public static GitWorktreeInfo? Read(string directory)
    {
        string? Git(params string[] args)
        {
            try
            {
                using var process = AgentProcess.Launch("git", args, directory, AgentProcess.ExtendedPath());
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(2000)) { AgentProcess.Kill(process); return null; }
                return process.Process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }
        var root = Git("rev-parse", "--show-toplevel");
        if (string.IsNullOrEmpty(root)) return null;
        var branch = Git("symbolic-ref", "--quiet", "--short", "HEAD") ?? Git("rev-parse", "--short", "HEAD");
        return new(root, branch ?? "Detached HEAD");
    }
}
