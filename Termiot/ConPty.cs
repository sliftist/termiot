using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Termiot;

public sealed class ConPty : IDisposable
{
    private IntPtr _hpc;
    private IntPtr _hProcess;
    // The ConPTY-side pipe ends. The classic sample closes these right after CreatePseudoConsole, but on current Windows 11 builds that races conhost's startup — conhost can observe EOF on its input pipe and tear the whole session down (cmd exits with code 0 within milliseconds). Keeping them open for the session's lifetime is harmless and removes the race.
    private SafeFileHandle _ptyIn = null!;
    private SafeFileHandle _ptyOut = null!;
    public int ChildPid { get; private init; }
    public FileStream Output { get; private init; } = null!;
    public FileStream Input { get; private init; } = null!;

    public static ConPty Start(string commandLine, string workingDir, int cols, int rows)
    {
        if (!CreatePipe(out var ptyIn, out var ourInputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }
        if (!CreatePipe(out var ourOutputRead, out var ptyOut, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }
        int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, ptyIn, ptyOut, 0, out var hpc);
        if (hr != 0)
        {
            throw new Win32Exception(hr);
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            {
                throw new Win32Exception();
            }
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
            var siex = new STARTUPINFOEXW();
            siex.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEXW>();
            // STARTF_USESTDHANDLES with NULL handles (same as Windows Terminal): without it, CreateProcess clones this process's std handles into the console child, overriding the pseudoconsole — cmd's stdin becomes whatever launched the host (a pipe, NUL, ...), reads EOF, and exits 0 within milliseconds.
            siex.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            siex.lpAttributeList = attrList;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workingDir, ref siex, out var pi))
            {
                throw new Win32Exception();
            }
            CloseHandle(pi.hThread);
            return new ConPty
            {
                _hpc = hpc,
                _hProcess = pi.hProcess,
                _ptyIn = ptyIn,
                _ptyOut = ptyOut,
                ChildPid = pi.dwProcessId,
                Output = new FileStream(ourOutputRead, FileAccess.Read),
                Input = new FileStream(ourInputWrite, FileAccess.Write),
            };
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    public void Resize(int cols, int rows)
    {
        ResizePseudoConsole(_hpc, new COORD { X = (short)cols, Y = (short)rows });
    }

    public int WaitForExit()
    {
        uint waitResult = WaitForSingleObject(_hProcess, INFINITE);
        if (waitResult != 0)
        {
            AppLog.Write("conpty", $"WaitForSingleObject on cmd pid {ChildPid} returned {waitResult:x} (error {Marshal.GetLastWin32Error()})");
        }
        if (!GetExitCodeProcess(_hProcess, out int code))
        {
            AppLog.Write("conpty", $"GetExitCodeProcess on cmd pid {ChildPid} failed (error {Marshal.GetLastWin32Error()})");
            return -1;
        }
        return code;
    }

    public void Kill()
    {
        TerminateProcess(_hProcess, 1);
        Dispose();
    }

    public void Dispose()
    {
        if (_hpc != IntPtr.Zero)
        {
            ClosePseudoConsole(_hpc);
            _hpc = IntPtr.Zero;
        }
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        _ptyIn.Dispose();
        _ptyOut.Dispose();
    }

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
