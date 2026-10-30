using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace rhDock.Agents;

/// <summary>.NET 8 Process.Start inherits unrelated inheritable Rhino handles when redirecting streams. Restrict this child to its three pipes.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAgentStart
{
    public static AgentChild Start(ProcessStartInfo info)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        var initialized = false;
        var created = false;
        ProcessInfo native = default;
        Process? process = null;
        StreamWriter? input = null;
        StreamReader? output = null, error = null;
        try
        {
            Check(CreatePipe(out inputRead, out inputWrite, ref security, 0));
            Check(CreatePipe(out outputRead, out outputWrite, ref security, 0));
            Check(CreatePipe(out errorRead, out errorWrite, ref security, 0));
            Check(SetHandleInformation(inputWrite, 1, 0));
            Check(SetHandleInformation(outputRead, 1, 0));
            Check(SetHandleInformation(errorRead, 1, 0));

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            var allowed = new[] { inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle() };
            handles = Marshal.AllocHGlobal(IntPtr.Size * allowed.Length);
            Marshal.Copy(allowed, 0, handles, allowed.Length);
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: unrelated inheritable handles are excluded, without altering Rhino's flags.
            Check(UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(IntPtr.Size * allowed.Length), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx
            {
                Info = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = allowed[0], Output = allowed[1], Error = allowed[2]
                },
                Attributes = attributes
            };
            var command = new StringBuilder(Quote(info.FileName));
            foreach (var arg in info.ArgumentList) command.Append(' ').Append(Quote(arg));
            var variables = string.Join("\0", info.Environment.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + "=" + p.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(variables);
            // Start suspended so even an immediately exiting command can be attached before its PID disappears.
            Check(CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x00000400 | 0x08000000 | 0x00000004,
                environment, info.WorkingDirectory, ref startup, out native));
            created = true;
            process = Process.GetProcessById(native.Id);
            _ = process.SafeHandle; // Keep a process handle before resuming, including for very short-lived commands.
            input = new StreamWriter(new FileStream(inputWrite, FileAccess.Write, 4096, false), info.StandardInputEncoding!, 4096);
            output = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, false), info.StandardOutputEncoding!, true, 4096);
            error = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, false), info.StandardErrorEncoding!, true, 4096);
            if (ResumeThread(native.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new AgentChild(process, input, output, error);
            process = null; input = null; output = null; error = null;
            inputWrite = null; outputRead = null; errorRead = null; // Ownership transferred to streams.
            return result;
        }
        catch
        {
            if (created) TerminateProcess(native.Process, 1);
            throw;
        }
        finally
        {
            input?.Dispose(); output?.Dispose(); error?.Dispose(); process?.Dispose();
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose();
            if (created) { CloseHandle(native.Thread); CloseHandle(native.Process); }
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    // Match Windows argv quoting: double trailing backslashes and backslashes preceding quotes.
    private static string Quote(string value)
    {
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("Process arguments cannot contain a null character.", nameof(value));
        if (value.Length != 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', slashes * (c == '"' ? 2 : 1));
            if (c == '"') text.Append('\\');
            text.Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
