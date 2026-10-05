using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace rhDock.Agents;

/// <summary>
/// Finds and starts an agent CLI from a GUI-launched Rhino (MonoCode's core/child.ts). Lookup and launch follow RhinoAI's
/// CliProcess; the extended PATH follows Claudian, because a GUI process lacks the user's shell PATH.
/// </summary>
public static class AgentProcess
{
    /// <summary>The process PATH plus common install folders. The agent receives it too, since it runs git, dotnet, and node.</summary>
    public static string ExtendedPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var raw in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dir = Environment.ExpandEnvironmentVariables(raw);
                if (!dirs.Contains(dir, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                    dirs.Add(dir);
            }
        }
        string Join(params string?[] parts) => string.Join(Path.PathSeparator, parts.Where(p => p is not null));

        Add(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            // Rhino keeps the PATH it started with; installs made since then are only in the registry.
            Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Add(Join(
                Path.Combine(appData, "npm"),
                Path.Combine(home, ".opencode", "bin"),
                Path.Combine(home, ".grok", "bin"),
                Path.Combine(home, ".cargo", "bin"),
                Path.Combine(home, ".bun", "bin"),
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, "scoop", "shims"),
                Path.Combine(local, "Volta", "bin"),
                Path.Combine(local, "mise", "shims"),
                Environment.GetEnvironmentVariable("NVM_SYMLINK"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")));
        }
        else
        {
            Add(Join(
                Path.Combine(home, ".opencode", "bin"),
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".claude", "local"),
                Path.Combine(home, ".grok", "bin"),
                Path.Combine(home, ".fx", "bin"),
                Path.Combine(home, ".hermes", "hermes-agent", "venv", "bin"),
                Path.Combine(home, ".local", "share", "agy-acp"),
                Path.Combine(home, ".cargo", "bin"),
                Path.Combine(home, ".bun", "bin"),
                Path.Combine(home, ".npm-global", "bin"),
                Path.Combine(home, ".volta", "bin"),
                Path.Combine(home, ".local", "share", "fnm", "aliases", "default", "bin"),
                Path.Combine(home, ".local", "share", "mise", "shims"),
                Path.Combine(home, ".asdf", "shims"),
                "/opt/homebrew/bin",
                "/usr/local/bin"));
            var nvm = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
                // ponytail: newest by folder name, not semver; add nvm's default alias if users hit a wrong Node.
                Add(Join(Directory.GetDirectories(nvm).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin")).ToArray()));
        }
        return string.Join(Path.PathSeparator, dirs.Where(Directory.Exists));
    }

    /// <summary>
    /// Resolves a command. A user-set path wins; otherwise a real .exe anywhere on the PATH beats an
    /// npm .cmd shim, because CreateProcess cannot start a batch file directly.
    /// </summary>
    public static string? Find(string command, string? userPath, string path)
    {
        if (!string.IsNullOrWhiteSpace(userPath)) return File.Exists(userPath) ? userPath : null;
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var extension in extensions)
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, command + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Starts an agent CLI with redirected UTF-8 streams; stderr goes to the log. Callers own the process and end it with <see cref="Kill"/>.</summary>
    public static Process Launch(string executable, IReadOnlyList<string> args, string cwd, string path, Action<string>? log = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        // Agents write UTF-8; Windows would otherwise decode redirected streams with the console code page. No BOM on stdin.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        var extension = Path.GetExtension(executable);
        if (OperatingSystem.IsWindows() && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(executable);
        }
        else psi.FileName = executable;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = path;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) psi.Environment[name] = value;

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke("stderr " + e.Data); };
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Runs an executable to completion and returns its standard output, or "" when it fails to start or outlives the timeout,
    /// which kills it. For one-shot calls: --version, --help, and a conversation title.
    /// </summary>
    // ponytail: output is buffered, not streamed; fine for short answers.
    public static string Capture(string executable, IReadOnlyList<string> args, string cwd, TimeSpan timeout, string? path = null)
    {
        try
        {
            using var process = Launch(executable, args, cwd, path ?? ExtendedPath());
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) { Kill(process); return ""; }
            return output.Result;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }

    /// <summary>Ends the process and everything it started.</summary>
    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        process.Dispose();
    }

    // Agents key their stored conversations by directory, and some (OpenCode) keep no file snapshots for one reached through a symbolic link
    // (macOS /var is /private/var), so they all get the real path.
    private static readonly ConcurrentDictionary<string, string> RealPaths = new();

    public static string RealPath(string path) => OperatingSystem.IsWindows() ? path : RealPaths.GetOrAdd(path, p =>
    {
        try
        {
            var full = Path.GetFullPath(p);
            var real = Path.GetPathRoot(full)!;
            foreach (var part in full[real.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                real = Path.Combine(real, part);
                if (new DirectoryInfo(real).ResolveLinkTarget(returnFinalTarget: true) is { } target) real = target.FullName;
            }
            return real;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return p; }
    });
}
