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
                var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(2000)) { process.Kill(); return null; }
                return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }
        var root = Git("rev-parse", "--show-toplevel");
        if (string.IsNullOrEmpty(root)) return null;
        var branch = Git("symbolic-ref", "--quiet", "--short", "HEAD") ?? Git("rev-parse", "--short", "HEAD");
        return new(root, branch ?? "Detached HEAD");
    }
}
