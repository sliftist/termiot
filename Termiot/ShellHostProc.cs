using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace Termiot;

// One host process per shell instance. It owns the ConPTY running cmd.exe and appends every output byte to shells\<id>\output.log, which doubles as the persisted shell state: a window replays the log on connect, so watching-window changes, window restarts, and cold resurrections all restore the scrollback. The host knows which window process owns it (via Associate) and checks that window's liveness every 5 seconds — a shell whose window is gone terminates itself so nothing is ever orphaned; its folder stays on disk, and the next window process to open either reuses the still-live host or offers to resume from the persisted state.
public static class ShellHostProc
{
    private const long MaxLogBytes = 64L * 1024 * 1024;
    private const long TrimTargetBytes = 48L * 1024 * 1024;
    private const int PtyReadBufferBytes = 64 * 1024;
    private const int ReplayChunkBytes = 64 * 1024;
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int MaxCols = 1000;
    private const int MaxRows = 500;
    private const int ExitLingerMs = 300;
    private const int PipeRetryDelayMs = 1000;
    private const int LivenessCheckMs = 5000;
    // Long enough for a relaunched window or a drag-handoff target to connect before the orphan check pulls the trigger.
    private const int OrphanGraceMs = 10000;
    private const int PtyInputQueueCap = 1024;
    private const int PipeCreateMaxFailures = 3;
    private const int LogFlushIntervalMs = 1000;
    // Without explicit buffer sizes a named pipe gets ZERO-byte buffers: every write rendezvous-blocks until the peer posts a read, which deadlocks the connect handshake (host replays while the client is still writing its handshake frames — each side waits for the other to read).
    private const int PipeBufferBytes = 128 * 1024;
    private const int TerminateLockTimeoutMs = 2000;

    private static readonly object IoLock = new();
    private static NamedPipeServerStream? _client;
    private static ConPty _pty = null!;
    private static string _logPath = null!;
    private static string _shellId = null!;
    private static readonly BlockingCollection<byte[]> PtyInputQueue = new(new ConcurrentQueue<byte[]>(), PtyInputQueueCap);

    private static volatile int _windowPid;
    private static long _windowStartTicks;
    private static volatile string? _windowId;
    private static long _lastClientSeenTick = Environment.TickCount64;
    private static bool _writeLogImmediately;
    private static readonly MemoryStream PendingLog = new();

    public static void Run(string shellId, string startDir)
    {
        _shellId = shellId;
        Directory.CreateDirectory(AppPaths.ShellDir(shellId));
        _logPath = AppPaths.LogFile(shellId);
        TrimLog();
        HostInfo.SaveCurrentProcess(shellId);
        _writeLogImmediately = AppSettings.Load().WriteLogImmediately;
        if (!Directory.Exists(startDir))
        {
            startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        string shellPath = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        // Set on the host so cmd inherits it: the shell (and AUTORESUME.cmd) can reference its own state folder.
        Environment.SetEnvironmentVariable("SHELLFOLDER", AppPaths.ShellDir(shellId));
        _pty = ConPty.Start($"\"{shellPath}\"", startDir, DefaultCols, DefaultRows);
        AppLog.Write("shellhost", $"shell {shellId} started, cmd pid {_pty.ChildPid}");

        new Thread(ReadPtyLoop) { IsBackground = true }.Start();
        new Thread(WriteInputLoop) { IsBackground = true }.Start();
        new Thread(WaitExitLoop) { IsBackground = true }.Start();
        new Thread(LivenessLoop) { IsBackground = true }.Start();
        if (!_writeLogImmediately)
        {
            new Thread(FlushLogLoop) { IsBackground = true }.Start();
        }

        int pipeFailures = 0;
        while (true)
        {
            NamedPipeServerStream server;
            try
            {
                // PipeOptions.Asynchronous is load-bearing: a synchronous pipe handle serializes ALL I/O in the kernel, so a pending blocking Read on the frame-loop thread blocks the pty thread's Writes on the same handle — output wedges behind waiting-for-input and nothing flows until the peer disconnects. Overlapped handles allow concurrent read/write.
                server = new NamedPipeServerStream(AppPaths.PipeName(shellId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferBytes, PipeBufferBytes);
                pipeFailures = 0;
            }
            catch (Exception ex)
            {
                // Persistent failure means another host already owns this shell's pipe (e.g. a duplicate spawned during a handoff race) — this instance must die, not fight for the name.
                pipeFailures++;
                AppLog.Write("shellhost", $"pipe server create failed ({pipeFailures}): {ex.Message}");
                if (pipeFailures >= PipeCreateMaxFailures)
                {
                    Terminate("pipe name is owned by another host");
                }
                Thread.Sleep(PipeRetryDelayMs);
                continue;
            }
            try
            {
                server.WaitForConnection();
                _lastClientSeenTick = Environment.TickCount64;
                ServeClient(server);
            }
            catch (Exception ex)
            {
                AppLog.Write("shellhost", "client session ended: " + ex.Message);
            }
            lock (IoLock)
            {
                if (_client == server)
                {
                    _client = null;
                }
            }
            _lastClientSeenTick = Environment.TickCount64;
            try
            {
                server.Dispose();
            }
            catch
            {
            }
        }
    }

    private static void ServeClient(NamedPipeServerStream server)
    {
        var hello = PipeProtocol.ReadFrame(server);
        if (hello is not { Type: PipeProtocol.MsgHello } h || h.Payload.Length < 8)
        {
            return;
        }
        long offset = BinaryPrimitives.ReadInt64LittleEndian(h.Payload);
        lock (IoLock)
        {
            FlushLogLocked();
        }
        // The bulk replay runs OUTSIDE IoLock: a slow-draining client must not stall log flushes, the pty reader, or termination. The short locked pass afterwards catches bytes appended mid-replay and atomically promotes the client to live-forwarding.
        long pos = ReplayRange(server, offset, long.MaxValue);
        lock (IoLock)
        {
            FlushLogLocked();
            ReplayRange(server, pos, long.MaxValue);
            _client = server;
        }
        while (true)
        {
            _lastClientSeenTick = Environment.TickCount64;
            var frame = PipeProtocol.ReadFrame(server);
            if (frame is not { } f)
            {
                return;
            }
            switch (f.Type)
            {
                case PipeProtocol.MsgInput:
                    if (!PtyInputQueue.TryAdd(f.Payload))
                    {
                        AppLog.Write("shellhost", "pty input queue full — dropping input");
                    }
                    break;
                case PipeProtocol.MsgResize:
                    if (f.Payload.Length >= 8)
                    {
                        int cols = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(f.Payload), 2, MaxCols);
                        int rows = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(f.Payload.AsSpan(4)), 2, MaxRows);
                        try
                        {
                            _pty.Resize(cols, rows);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("shellhost", "pty resize failed: " + ex.Message);
                        }
                    }
                    break;
                case PipeProtocol.MsgAssociate:
                    if (f.Payload.Length >= 12)
                    {
                        _windowPid = BinaryPrimitives.ReadInt32LittleEndian(f.Payload);
                        _windowStartTicks = BinaryPrimitives.ReadInt64LittleEndian(f.Payload.AsSpan(4));
                        _windowId = Encoding.UTF8.GetString(f.Payload.AsSpan(12));
                    }
                    break;
                case PipeProtocol.MsgCommandMarker:
                    var marker = Encoding.UTF8.GetBytes("\x1b_termiot-cmd:" + Convert.ToBase64String(f.Payload) + "\x1b\\");
                    Broadcast(marker, marker.Length);
                    break;
                case PipeProtocol.MsgDetach:
                    return;
                case PipeProtocol.MsgShutdown:
                    Terminate("shutdown requested");
                    break;
            }
        }
    }

    // No orphaned shells: if no window is connected and the associated window process is gone (pid + start time must both match to count as alive), this shell ends itself. The folder stays for resurrection from the settings window.
    private static void LivenessLoop()
    {
        while (true)
        {
            Thread.Sleep(LivenessCheckMs);
            if (_client != null)
            {
                continue;
            }
            if (Environment.TickCount64 - Volatile.Read(ref _lastClientSeenTick) < OrphanGraceMs)
            {
                continue;
            }
            if (_windowPid != 0 && HostInfo.ProcessAlive(_windowPid, _windowStartTicks))
            {
                continue;
            }
            Terminate("window is gone — exiting to avoid an orphaned shell");
        }
    }

    // Returns the file position reached. Reads the log without holding any lock; each chunk is read via a fresh open-read-close.
    private static long ReplayRange(NamedPipeServerStream server, long from, long to)
    {
        long pos = from;
        var buf = new byte[ReplayChunkBytes];
        while (pos < to)
        {
            int n;
            try
            {
                using var rs = new FileStream(_logPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
                if (pos < 0 || pos > rs.Length)
                {
                    pos = 0;
                }
                rs.Position = pos;
                n = rs.Read(buf, 0, (int)Math.Min(buf.Length, to - pos));
            }
            catch (Exception ex)
            {
                AppLog.Write("shellhost", "replay read failed: " + ex.Message);
                break;
            }
            if (n <= 0)
            {
                break;
            }
            PipeProtocol.WriteFrame(server, PipeProtocol.MsgOutput, buf.AsSpan(0, n));
            pos += n;
        }
        return pos;
    }

    // Termination must never hang on a wedged lock — flush the log if the lock is available soon, otherwise die anyway (losing at most a second of scrollback beats a zombie host).
    private static void Terminate(string reason)
    {
        AppLog.Write("shellhost", $"shell {_shellId} terminating: {reason}");
        if (Monitor.TryEnter(IoLock, TerminateLockTimeoutMs))
        {
            try
            {
                FlushLogLocked();
            }
            finally
            {
                Monitor.Exit(IoLock);
            }
        }
        try
        {
            _pty.Kill();
        }
        catch
        {
        }
        Process.GetCurrentProcess().Kill();
    }

    private static void WriteInputLoop()
    {
        foreach (var bytes in PtyInputQueue.GetConsumingEnumerable())
        {
            try
            {
                _pty.Input.Write(bytes);
                _pty.Input.Flush();
            }
            catch (Exception ex)
            {
                AppLog.Write("shellhost", "pty input write failed: " + ex.Message);
            }
        }
    }

    private static void ReadPtyLoop()
    {
        var buf = new byte[PtyReadBufferBytes];
        try
        {
            while (true)
            {
                int n = _pty.Output.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    break;
                }
                Broadcast(buf, n);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "pty read loop ended: " + ex.Message);
        }
    }

    private static void WaitExitLoop()
    {
        int code;
        try
        {
            code = _pty.WaitForExit();
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "wait for exit failed: " + ex);
            code = -1;
        }
        var marker = Encoding.UTF8.GetBytes($"\r\n\x1b[90m[process exited with code {code}]\x1b[0m\r\n");
        Broadcast(marker, marker.Length);
        lock (IoLock)
        {
            if (_client is { } client)
            {
                var payload = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(payload, code);
                try
                {
                    PipeProtocol.WriteFrame(client, PipeProtocol.MsgExited, payload);
                }
                catch
                {
                }
            }
        }
        Thread.Sleep(ExitLingerMs);
        Terminate($"cmd exited with code {code}");
    }

    // Output is accumulated in memory and flushed to disk at most once per second (or per chunk when WriteLogImmediately is set). Live forwarding to the window is never delayed either way. No file handle is held between writes — every flush is open-append-close.
    private static void Broadcast(byte[] buf, int count)
    {
        lock (IoLock)
        {
            PendingLog.Write(buf, 0, count);
            if (_writeLogImmediately)
            {
                FlushLogLocked();
            }
            if (_client is { } client)
            {
                try
                {
                    PipeProtocol.WriteFrame(client, PipeProtocol.MsgOutput, buf.AsSpan(0, count));
                }
                catch
                {
                    _client = null;
                }
            }
        }
    }

    private static void FlushLogLoop()
    {
        while (true)
        {
            Thread.Sleep(LogFlushIntervalMs);
            lock (IoLock)
            {
                FlushLogLocked();
            }
        }
    }

    private static void FlushLogLocked()
    {
        if (PendingLog.Length == 0)
        {
            return;
        }
        try
        {
            using var log = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            log.Write(PendingLog.GetBuffer(), 0, (int)PendingLog.Length);
            PendingLog.SetLength(0);
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "log flush failed: " + ex.Message);
        }
    }

    private static void TrimLog()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length <= MaxLogBytes)
            {
                return;
            }
            byte[] tail;
            using (var rs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                rs.Position = rs.Length - TrimTargetBytes;
                tail = new byte[TrimTargetBytes];
                rs.ReadExactly(tail);
            }
            File.WriteAllBytes(_logPath, tail);
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "log trim failed: " + ex.Message);
        }
    }
}
