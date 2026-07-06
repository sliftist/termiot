using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Termiot;

// The receiving side of Windows' default-terminal delegation. When a console app starts unattached, conhost CoCreates our CLSID (COM launches this exe with -Embedding) and calls EstablishPtyHandoff on ITerminalHandoff3: we create the in/out pipes, hand conhost its ends via the out parameters, and wrap the session in our normal architecture — a --handoffhost shell host owns the pty streams (log, named pipe, liveness), and a fresh renderer window is spawned around it.
[StructLayout(LayoutKind.Sequential)]
public struct TERMINAL_STARTUP_INFO
{
    public IntPtr Title;
    public IntPtr IconPath;
    public int IconIndex;
    public uint DwX;
    public uint DwY;
    public uint DwXSize;
    public uint DwYSize;
    public uint DwXCountChars;
    public uint DwYCountChars;
    public uint DwFillAttribute;
    public uint DwFlags;
    public ushort WShowWindow;
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00")]
public interface ITerminalHandoff3
{
    [PreserveSig]
    int EstablishPtyHandoff(out IntPtr inPipe, out IntPtr outPipe, IntPtr signal, IntPtr reference, IntPtr server, IntPtr client, in TERMINAL_STARTUP_INFO startupInfo);
}

public static class TerminalHandoffServer
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    // The COM server exits after this long with no incoming handoffs; the next console launch simply restarts it.
    private const int IdleExitMs = 120_000;

    private static long _lastActivityTick = Environment.TickCount64;

    public static int Run()
    {
        int exitCode = 0;
        var thread = new Thread(() =>
        {
            var clsid = Guid.Parse(DefaultTerminal.TermiotClsid);
            int hr = CoRegisterClassObject(in clsid, new HandoffClassFactory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _);
            if (hr != 0)
            {
                AppLog.Write("defterm", $"CoRegisterClassObject failed: 0x{hr:x8}");
                exitCode = 1;
                return;
            }
            AppLog.Write("defterm", "handoff COM server running");
            while (Environment.TickCount64 - Volatile.Read(ref _lastActivityTick) < IdleExitMs)
            {
                Thread.Sleep(5000);
            }
            AppLog.Write("defterm", "handoff COM server idle — exiting");
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    [ComVisible(true)]
    private sealed class TerminalHandoffObject : ITerminalHandoff3
    {
        public int EstablishPtyHandoff(out IntPtr inPipe, out IntPtr outPipe, IntPtr signal, IntPtr reference, IntPtr server, IntPtr client, in TERMINAL_STARTUP_INFO startupInfo)
        {
            inPipe = IntPtr.Zero;
            outPipe = IntPtr.Zero;
            Volatile.Write(ref _lastActivityTick, Environment.TickCount64);
            try
            {
                string title = "";
                try
                {
                    title = startupInfo.Title != IntPtr.Zero ? Marshal.PtrToStringBSTR(startupInfo.Title) : "";
                }
                catch
                {
                }
                // conhost reads its input from pipe1 and writes its output to pipe2; the out params transfer those ends to COM (which duplicates them across and frees ours). The [in] handles are freed by COM after return, so everything we keep must be duplicated — directly as inheritable handles for the host child.
                if (!CreatePipe(out var conhostRead, out var ourInputWrite, IntPtr.Zero, 0) || !CreatePipe(out var ourOutputRead, out var conhostWrite, IntPtr.Zero, 0))
                {
                    return Marshal.GetHRForLastWin32Error();
                }
                inPipe = conhostRead.DangerousGetHandle();
                outPipe = conhostWrite.DangerousGetHandle();
                var outReadInh = DuplicateInheritable(ourOutputRead.DangerousGetHandle());
                var inWriteInh = DuplicateInheritable(ourInputWrite.DangerousGetHandle());
                var signalInh = DuplicateInheritable(signal);
                var clientInh = DuplicateInheritable(client);
                var referenceInh = DuplicateInheritable(reference);
                ourOutputRead.Dispose();
                ourInputWrite.Dispose();

                string shellId = Program.NewShellId();
                Directory.CreateDirectory(AppPaths.ShellDir(shellId));
                ShellInfo.Save(new TabInfo { Id = shellId, Title = title.Length > 0 ? title : "console" });
                string windowId = Program.NewId();
                new WindowState { Shells = new List<string> { shellId } }.Save(windowId);

                var exe = Environment.ProcessPath!;
                SpawnWithInheritedHandles($"\"{exe}\" --handoffhost {shellId} {(long)outReadInh} {(long)inWriteInh} {(long)signalInh} {(long)clientInh} {(long)referenceInh}");
                Program.SpawnWindowProcess(windowId);
                AppLog.Write("defterm", $"handoff accepted: shell {shellId}, window {windowId}, title '{title}'");
                return 0;
            }
            catch (Exception ex)
            {
                AppLog.Write("defterm", "handoff failed: " + ex);
                return Marshal.GetHRForException(ex);
            }
        }
    }

    private static IntPtr DuplicateInheritable(IntPtr handle)
    {
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, handle, process, out var duplicate, 0, true, 2))
        {
            throw new IOException("DuplicateHandle failed: " + Marshal.GetLastWin32Error());
        }
        return duplicate;
    }

    // .NET's Process.Start doesn't guarantee bInheritHandles, so the host child (which needs the pty handles) is spawned with raw CreateProcess.
    private static void SpawnWithInheritedHandles(string commandLine)
    {
        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero, null, ref si, out var pi))
        {
            throw new IOException("CreateProcess failed: " + Marshal.GetLastWin32Error());
        }
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer(bool fLock);
    }

    private sealed class HandoffClassFactory : IClassFactory
    {
        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
            {
                return CLASS_E_NOAGGREGATION;
            }
            var unknown = Marshal.GetIUnknownForObject(new TerminalHandoffObject());
            try
            {
                return Marshal.QueryInterface(unknown, in riid, out ppvObject);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public int LockServer(bool fLock)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(in Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
