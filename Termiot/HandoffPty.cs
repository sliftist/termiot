using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Termiot;

// A pty session received via default-terminal handoff: conhost owns the console; we hold its output/input pipes, the signal pipe (resize goes there as a u16-code packet), the client process handle (its exit ends the session), and the reference handle (holding it keeps the console session alive).
public sealed class HandoffPty : IPtySession
{
    private const ushort SignalResizeWindow = 8;

    private readonly FileStream _signal;
    private readonly SafeFileHandle _reference;
    private readonly IntPtr _client;
    private readonly object _signalLock = new();

    public FileStream Output { get; }
    public FileStream Input { get; }

    public HandoffPty(long outRead, long inWrite, long signal, long client, long reference)
    {
        Output = new FileStream(new SafeFileHandle((IntPtr)outRead, true), FileAccess.Read);
        Input = new FileStream(new SafeFileHandle((IntPtr)inWrite, true), FileAccess.Write);
        _signal = new FileStream(new SafeFileHandle((IntPtr)signal, true), FileAccess.Write);
        _client = (IntPtr)client;
        _reference = new SafeFileHandle((IntPtr)reference, true);
    }

    public void Resize(int cols, int rows)
    {
        var packet = new byte[6];
        BitConverter.TryWriteBytes(packet.AsSpan(0), SignalResizeWindow);
        BitConverter.TryWriteBytes(packet.AsSpan(2), (ushort)Math.Clamp(cols, 1, ushort.MaxValue));
        BitConverter.TryWriteBytes(packet.AsSpan(4), (ushort)Math.Clamp(rows, 1, ushort.MaxValue));
        lock (_signalLock)
        {
            _signal.Write(packet);
            _signal.Flush();
        }
    }

    public int WaitForExit()
    {
        WaitForSingleObject(_client, INFINITE);
        GetExitCodeProcess(_client, out int code);
        return code;
    }

    public void Kill()
    {
        TerminateProcess(_client, 1);
        Dispose();
    }

    public void Dispose()
    {
        try
        {
            Output.Dispose();
            Input.Dispose();
            _signal.Dispose();
            _reference.Dispose();
        }
        catch
        {
        }
        CloseHandle(_client);
    }

    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
