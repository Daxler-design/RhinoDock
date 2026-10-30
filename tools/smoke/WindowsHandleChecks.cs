using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using rhDock.Agents;

internal static class WindowsHandleChecks
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);

    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows handle inheritance."); return; }
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-handles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cwd = Path.Combine(folder, "working directory");
        Directory.CreateDirectory(cwd);
        var model = Path.Combine(folder, "original.3dm");
        var shim = Path.Combine(folder, "agent.cmd");
        File.WriteAllText(shim, "@echo off\r\necho READY\r\necho %CD%\r\necho %RHDOCK_HANDLE_TEST%\r\nset /p response=\r\necho DONE\r\n");
        try
        {
            foreach (var native in new[] { false, true })
            {
                using var handle = File.OpenHandle(model, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                if (!SetHandleInformation(handle, 1, 1)) throw new System.ComponentModel.Win32Exception();
                using var child = AgentProcess.Launch(native ? Environment.GetEnvironmentVariable("COMSPEC")! : shim,
                    native ? ["/d", "/c", shim] : [], cwd, AgentProcess.ExtendedPath(), environment:
                    new Dictionary<string, string> { ["RHDOCK_HANDLE_TEST"] = "isolated-environment" });
                try
                {
                    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "READY")
                        throw new Exception("Child did not start.");
                    if (!GetHandleInformation(handle, out var flags) || (flags & 1) == 0)
                        throw new Exception("Launcher changed the parent's original handle flags.");
                    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != cwd
                        || await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "isolated-environment")
                        throw new Exception("Working directory or custom environment changed.");
                    handle.Dispose(); // Rhino releases its old file after Save As; the agent must not retain a copy.
                    File.Move(model, model + (native ? ".exe-renamed" : ".shim-renamed"));
                    await child.StandardInput.WriteLineAsync("exit");
                    await child.StandardInput.FlushAsync();
                    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "DONE")
                        throw new Exception("Redirected pipes stopped working.");
                    if (!child.WaitForExit(5000)) throw new Exception("Child did not exit.");
                    Console.WriteLine("PASS: " + (native ? "native executable" : "npm shim") + " cannot retain an unrelated inheritable file handle; pipes, environment and working directory work.");
                }
                finally { AgentProcess.Kill(child); }
            }
            for (var i = 0; i < 3; i++)
            {
                var text = AgentProcess.Capture(Environment.GetEnvironmentVariable("COMSPEC")!, ["/d", "/c", "echo CAPTURE"], cwd, TimeSpan.FromSeconds(10));
                if (text.Trim() != "CAPTURE") throw new Exception("Short-lived process capture failed.");
            }
            try
            {
                using var missing = AgentProcess.Launch(Path.Combine(folder, "missing.exe"), [], cwd, AgentProcess.ExtendedPath());
                throw new Exception("Missing executable unexpectedly started.");
            }
            catch (System.ComponentModel.Win32Exception) { }
            Console.WriteLine("PASS: short-lived capture and missing-executable cleanup.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
