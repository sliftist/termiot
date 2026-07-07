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
    // Each of the two rotating log files is capped here; total retained history is up to two of these.
    private const long MaxActiveLogBytes = 10L * 1024 * 1024;
    private const int PtyReadBufferBytes = 64 * 1024;
    private const int ReplayChunkBytes = 64 * 1024;
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int MaxCols = 1000;
    private const int MaxRows = 500;
    private const int ExitLingerMs = 300;
    private const int PipeRetryDelayMs = 1000;
    private const int LivenessCheckMs = 5000;
    // Long enough for a relaunched or rebuilt-and-reloaded window, or a drag-handoff target, to connect before the orphan check pulls the trigger — renderer restarts must never cost the shells.
    private const int OrphanGraceMs = 15000;
    private const int PtyInputQueueCap = 1024;
    private const int PipeCreateMaxFailures = 3;
    private const int LogFlushIntervalMs = 1000;
    // Without explicit buffer sizes a named pipe gets ZERO-byte buffers: every write rendezvous-blocks until the peer posts a read, which deadlocks the connect handshake (host replays while the client is still writing its handshake frames — each side waits for the other to read).
    private const int PipeBufferBytes = 128 * 1024;
    private const int TerminateLockTimeoutMs = 2000;

    private static readonly object IoLock = new();
    private static NamedPipeServerStream? _client;
    private static IPtySession _pty = null!;
    private static string _logPath = null!;
    private static string _prevLogPath = null!;
    // Bytes currently in the active log file; guarded by IoLock. Drives rotation.
    private static long _activeLen;
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
        Setup(shellId);
        if (!Directory.Exists(startDir))
        {
            startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        string shellPath = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        // Set on the host so cmd inherits it: the shell (and AUTORESUME.cmd) can reference its own state folder.
        Environment.SetEnvironmentVariable("SHELLFOLDER", AppPaths.ShellDir(shellId));
        var pty = ConPty.Start($"\"{shellPath}\"", startDir, DefaultCols, DefaultRows);
        AppLog.Write("shellhost", $"shell {shellId} started, cmd pid {pty.ChildPid}");
        MainLoop(shellId, pty);
    }

    // Same host, but the pty came from a default-terminal handoff instead of a cmd we spawned.
    public static void RunHandoff(string shellId, long outRead, long inWrite, long signal, long client, long reference)
    {
        Setup(shellId);
        var pty = new HandoffPty(outRead, inWrite, signal, client, reference);
        AppLog.Write("shellhost", $"shell {shellId} started from handoff");
        MainLoop(shellId, pty);
    }

    private static void Setup(string shellId)
    {
        _shellId = shellId;
        Directory.CreateDirectory(AppPaths.ShellDir(shellId));
        _logPath = AppPaths.LogFile(shellId);
        _prevLogPath = AppPaths.PrevLogFile(shellId);
        // Migrate any oversized file (pre-rotation single log, or a crash-bloated one) down to the cap.
        TrimToCap(_prevLogPath);
        TrimToCap(_logPath);
        _activeLen = FileLen(_logPath);
        HostInfo.SaveCurrentProcess(shellId);
        _writeLogImmediately = AppSettings.Load().WriteLogImmediately;
    }

    private static void MainLoop(string shellId, IPtySession pty)
    {
        _pty = pty;
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
        // The client reads the bulk of the history from the log file itself; we only send it the most recent slice (default if unspecified), which it overlap-joins onto its file read. No megabytes over the pipe.
        long recentBytes = h.Payload.Length >= 16 ? BinaryPrimitives.ReadInt64LittleEndian(h.Payload.AsSpan(8)) : 128L * 1024;
        if (recentBytes <= 0)
        {
            recentBytes = 128L * 1024;
        }
        long prevLen, activeLen;
        byte[] recent;
        lock (IoLock)
        {
            FlushLogLocked();
            prevLen = FileLen(_prevLogPath);
            activeLen = _activeLen;
            long combined = prevLen + activeLen;
            recent = ReadLogicalBytes(Math.Max(0, combined - recentBytes), combined, prevLen);
        }
        PipeProtocol.WriteFrame(server, PipeProtocol.MsgHostElevated, new[] { (byte)(IsElevated() ? 1 : 0) });
        PipeProtocol.WriteFrame(server, PipeProtocol.MsgReplayRecent, recent);
        lock (IoLock)
        {
            FlushLogLocked();
            // Anything appended while we were sending the recent slice: forward it live so no bytes are missed, then promote to live-forwarding.
            if (_activeLen > activeLen)
            {
                ReplayLogical(server, prevLen + activeLen, prevLen + _activeLen, PipeProtocol.MsgOutput, prevLen);
            }
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

    // No orphaned shells, ever: the windows\*.json files are the single source of truth for which window owns which shell. When no client is connected, this shell finds its parent window there (cached id first, full scan on miss) and verifies that window's owner process is alive (pid + start time must both match). No referencing window, or a dead owner → self-terminate. The folder stays for resurrection.
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
            var windowId = FindParentWindowId();
            if (windowId == null)
            {
                Terminate("no window file references this shell — exiting to avoid an orphan");
            }
            var state = WindowState.Load(windowId!);
            if (state.OwnerPid != 0 && HostInfo.ProcessAlive(state.OwnerPid, state.OwnerStartTicks))
            {
                continue;
            }
            Terminate($"parent window {windowId} has no live owner — exiting to avoid an orphan");
        }
    }

    private static string? FindParentWindowId()
    {
        if (_windowId is { } cached && WindowListsShell(cached))
        {
            return cached;
        }
        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.WindowsDir, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (WindowListsShell(id))
                {
                    _windowId = id;
                    return id;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "window scan failed: " + ex.Message);
        }
        return null;
    }

    private static bool WindowListsShell(string windowId)
    {
        try
        {
            return File.Exists(AppPaths.WindowFile(windowId)) && WindowState.Load(windowId).Shells.Contains(_shellId);
        }
        catch
        {
            return false;
        }
    }

    // Sends the logical byte range [from, to) — positions run across the previous file then the active file as one continuous stream — each chunk framed as msgType. prevLen is the snapshot boundary between the two files. Reads without holding any lock, fresh open-read-close per chunk; a short read (e.g. a file that rotated out mid-replay) just stops early.
    private static long ReplayLogical(NamedPipeServerStream server, long from, long to, byte msgType, long prevLen)
    {
        long pos = Math.Max(0, from);
        var buf = new byte[ReplayChunkBytes];
        while (pos < to)
        {
            string path = pos < prevLen ? _prevLogPath : _logPath;
            long localPos = pos < prevLen ? pos : pos - prevLen;
            long segEnd = pos < prevLen ? Math.Min(to, prevLen) : to;
            int n;
            try
            {
                using var rs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
                if (localPos > rs.Length)
                {
                    break;
                }
                rs.Position = localPos;
                n = rs.Read(buf, 0, (int)Math.Min(buf.Length, segEnd - pos));
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
            PipeProtocol.WriteFrame(server, msgType, buf.AsSpan(0, n));
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
            long added = PendingLog.Length;
            using (var log = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                log.Write(PendingLog.GetBuffer(), 0, (int)PendingLog.Length);
            }
            PendingLog.SetLength(0);
            _activeLen += added;
            RotateIfNeeded();
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "log flush failed: " + ex.Message);
        }
    }

    // When the active file passes the cap, discard the previous file and rotate the active one into its place; a fresh active file starts on the next write. Retained history stays within two caps. Called under IoLock.
    private static void RotateIfNeeded()
    {
        if (_activeLen <= MaxActiveLogBytes)
        {
            return;
        }
        try
        {
            File.Delete(_prevLogPath);
            File.Move(_logPath, _prevLogPath);
            _activeLen = 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "log rotate failed: " + ex.Message);
        }
    }

    private static long FileLen(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // Read the logical byte range [from, to) across the previous file then the active file into one buffer (for the small recent slice sent to a connecting client).
    private static byte[] ReadLogicalBytes(long from, long to, long prevLen)
    {
        if (to <= from)
        {
            return Array.Empty<byte>();
        }
        var result = new byte[to - from];
        int written = 0;
        long pos = from;
        while (pos < to)
        {
            string path = pos < prevLen ? _prevLogPath : _logPath;
            long localPos = pos < prevLen ? pos : pos - prevLen;
            long segEnd = pos < prevLen ? Math.Min(to, prevLen) : to;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (localPos >= fs.Length)
                {
                    break;
                }
                fs.Position = localPos;
                int want = (int)Math.Min(segEnd - pos, fs.Length - localPos);
                int got = fs.Read(result, written, want);
                if (got <= 0)
                {
                    break;
                }
                written += got;
                pos += got;
            }
            catch
            {
                break;
            }
        }
        if (written < result.Length)
        {
            Array.Resize(ref result, written);
        }
        return result;
    }

    // Bound a single log file to the cap by keeping only its last MaxActiveLogBytes — used at startup to migrate a pre-rotation (or crash-bloated) file down to size.
    private static void TrimToCap(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxActiveLogBytes)
            {
                return;
            }
            byte[] tail;
            using (var rs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                rs.Position = rs.Length - MaxActiveLogBytes;
                tail = new byte[MaxActiveLogBytes];
                rs.ReadExactly(tail);
            }
            File.WriteAllBytes(path, tail);
        }
        catch (Exception ex)
        {
            AppLog.Write("shellhost", "log trim failed: " + ex.Message);
        }
    }
}
