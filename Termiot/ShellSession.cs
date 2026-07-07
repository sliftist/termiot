using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Termiot.Terminal;

namespace Termiot;

// UI-side connection to a tab's shell host. The bulk of the scrollback is read straight from the persisted log FILE (fast, local) — the host only streams the most recent slice, which is overlap-joined onto the file read, plus live output going forward. So restoring a heavy shell never transfers or parses megabytes over the pipe: the recent tail renders immediately from the file, and older history is read from the file lazily, off-thread.
public sealed class ShellSession : IDisposable
{
    private const int ConnectExistingTimeoutMs = 300;
    private const int SpawnConnectTimeoutMs = 15000;
    private const int ConnectPollMs = 100;
    private const int SendQueueCap = 1024;
    private const int DetachFlushTimeoutMs = 300;
    // Read this much of the log tail straight from the file for the instant initial render.
    private const int RecentFileTailBytes = 256 * 1024;
    // Ask the host for this much recent output (covers anything it hasn't flushed to the file yet); overlap-joined onto the file read.
    private const int HostRecentBytes = 128 * 1024;
    private const int OverlapProbeBytes = 8 * 1024;
    private const int FeedChunkBytes = 64 * 1024;

    private readonly string _tabId;
    private readonly string _startDir;
    private readonly string _windowId;
    private readonly bool _elevated;
    private readonly TermScreen _screen;
    private readonly VtParser _parser;
    private readonly BlockingCollection<(byte Type, byte[] Payload)> _sendQueue = new(new ConcurrentQueue<(byte, byte[])>(), SendQueueCap);
    private readonly ManualResetEventSlim _sendQueueDrained = new(false);
    private NamedPipeClientStream? _pipe;

    // The file tail we read and rendered, kept only to overlap-join the host's recent slice; and the logical boundary for the lazy older-history read.
    private byte[] _fileTail = Array.Empty<byte>();
    private long _tailStart;
    private long _tailPrevLen;
    private bool _joined;

    // Lazy older-history load (scrollback below the recent tail), triggered by Activate and serialized across tabs so several heavy shells don't reconstruct at once.
    private readonly object _scrollGate = new();
    private bool _scrollStarted;
    private static readonly SemaphoreSlim ScrollbackGate = new(1, 1);

    private volatile bool _disposed;
    private volatile int _pendingCols;
    private volatile int _pendingRows;

    public bool Dead { get; private set; }
    public event Action? OutputReceived;
    public event Action<int>? Exited;
    // Fired when older history is prepended as scrollback, with the net number of lines added. Raised under Screen.Sync so a handler can shift absolute line references (e.g. command marks) atomically with the insertion.
    public event Action<int>? ScrollbackPrepended;
    // Fired once the older-history load has finished (or immediately if there is none). Lets the window schedule background tabs only after the focused tab's is done.
    public event Action? ScrollbackLoaded;
    // Fired once on connect with the host's actual elevation (running as administrator).
    public event Action<bool>? ElevatedReported;

    private ShellSession(string tabId, string startDir, string windowId, TermScreen screen, VtParser parser, bool elevated)
    {
        _tabId = tabId;
        _startDir = startDir;
        _windowId = windowId;
        _elevated = elevated;
        _screen = screen;
        _parser = parser;
        _parser.OnRespond = SendInput;
    }

    // Two-phase start: create, subscribe to events, then Begin. Launching the reader inside the factory loses notifications the caller wants to observe.
    public static ShellSession Create(string tabId, string startDir, string windowId, TermScreen screen, VtParser parser, bool elevated = false)
    {
        return new ShellSession(tabId, startDir, windowId, screen, parser, elevated);
    }

    public void Begin()
    {
        new Thread(Run) { IsBackground = true, Name = "shell-read-" + _tabId }.Start();
    }

    private void Run()
    {
        try
        {
            // Render the recent tail straight from the file, before we even connect — instant, no megabytes over the pipe.
            ReadAndFeedFileTail();

            var pipe = TryConnect(ConnectExistingTimeoutMs);
            bool spawned = false;
            if (pipe == null)
            {
                spawned = true;
                SpawnHost();
                var deadline = Environment.TickCount64 + SpawnConnectTimeoutMs;
                while (pipe == null && Environment.TickCount64 < deadline && !_disposed)
                {
                    pipe = TryConnect(ConnectPollMs);
                }
            }
            if (pipe == null)
            {
                Dead = true;
                AppLog.Write("session", $"{_tabId}: no host answered within {SpawnConnectTimeoutMs}ms after spawn");
                ReportLocal("failed to start shell host process");
                return;
            }
            AppLog.Write("session", $"{_tabId}: connected to {(spawned ? "spawned" : "existing")} host");
            _pipe = pipe;
            // Hello: ask only for the recent slice — we read the bulk of the history from the file ourselves. (offset field kept for the fixed payload layout.)
            var hello = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(hello, 0);
            BinaryPrimitives.WriteInt64LittleEndian(hello.AsSpan(8), HostRecentBytes);
            PipeProtocol.WriteFrame(pipe, PipeProtocol.MsgHello, hello);
            var self = Process.GetCurrentProcess();
            var windowIdBytes = Encoding.UTF8.GetBytes(_windowId);
            var associate = new byte[12 + windowIdBytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(associate, self.Id);
            BinaryPrimitives.WriteInt64LittleEndian(associate.AsSpan(4), self.StartTime.Ticks);
            windowIdBytes.CopyTo(associate.AsSpan(12));
            PipeProtocol.WriteFrame(pipe, PipeProtocol.MsgAssociate, associate);
            if (_pendingCols > 0)
            {
                Resize(_pendingCols, _pendingRows);
            }
            new Thread(WriteLoop) { IsBackground = true, Name = "shell-write-" + _tabId }.Start();
            while (true)
            {
                var frame = PipeProtocol.ReadFrame(pipe);
                if (frame is not { } f)
                {
                    break;
                }
                switch (f.Type)
                {
                    case PipeProtocol.MsgHostElevated:
                        ElevatedReported?.Invoke(f.Payload.Length > 0 && f.Payload[0] != 0);
                        break;
                    case PipeProtocol.MsgReplayRecent:
                        JoinRecent(f.Payload);
                        break;
                    case PipeProtocol.MsgOutput:
                        // Normally live output (feed directly). An older host that never sends ReplayRecent delivers its tail as the first Output frames instead — overlap-join those onto the file read so they don't duplicate it.
                        if (_joined)
                        {
                            FeedChunks(f.Payload, 0, f.Payload.Length);
                        }
                        else
                        {
                            JoinRecent(f.Payload);
                        }
                        break;
                    case PipeProtocol.MsgExited:
                        Dead = true;
                        Exited?.Invoke(f.Payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(f.Payload) : -1);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("session", "reader loop ended: " + ex);
            if (!_disposed && !Dead)
            {
                ReportLocal(ex.Message);
            }
        }
        if (!Dead && !_disposed)
        {
            Dead = true;
            ReportLocal("shell host disconnected");
            Exited?.Invoke(-1);
        }
    }

    // Read the last RecentFileTailBytes of the log directly and render them, so the tab shows its recent history instantly without waiting on the host.
    private void ReadAndFeedFileTail()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            long activeLen = FileLen(AppPaths.LogFile(_tabId));
            long prevLen = FileLen(AppPaths.PrevLogFile(_tabId));
            long combined = prevLen + activeLen;
            _tailPrevLen = prevLen;
            _tailStart = Math.Max(0, combined - RecentFileTailBytes);
            _fileTail = ReadLogical(_tailStart, combined, prevLen);
            if (_fileTail.Length > 0)
            {
                FeedChunks(_fileTail, 0, _fileTail.Length);
            }
            PerfLog.Record($"file-tail {_tabId}: {_fileTail.Length / 1024.0:0}KB read+rendered in {sw.Elapsed.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            AppLog.Write("session", "file tail read failed: " + ex.Message);
        }
    }

    // The host's recent slice overlaps what we already read from the file; feed only the bytes past the overlap (typically just the little that wasn't flushed to the file yet).
    private void JoinRecent(byte[] recent)
    {
        if (_joined)
        {
            FeedChunks(recent, 0, recent.Length);
            return;
        }
        _joined = true;
        int overlap = FindOverlap(_fileTail, recent);
        if (overlap < recent.Length)
        {
            FeedChunks(recent, overlap, recent.Length - overlap);
        }
        _fileTail = Array.Empty<byte>();
    }

    private void FeedChunks(byte[] data, int offset, int count)
    {
        int end = offset + count;
        for (int off = offset; off < end; off += FeedChunkBytes)
        {
            int n = Math.Min(FeedChunkBytes, end - off);
            try
            {
                lock (_screen.Sync)
                {
                    _parser.Feed(data, off, n);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("session", "parse failed: " + ex.Message);
            }
        }
        if (count > 0)
        {
            OutputReceived?.Invoke();
        }
    }

    // Largest k such that the file tail ends with the recent slice's first k bytes — the seam where the two streams meet.
    private static int FindOverlap(byte[] tail, byte[] recent)
    {
        if (recent.Length == 0 || tail.Length == 0)
        {
            return 0;
        }
        int probeLen = Math.Min(OverlapProbeBytes, recent.Length);
        var tailSpan = tail.AsSpan();
        // Latest occurrence of the probe → largest overlap.
        int idx = tailSpan.LastIndexOf(recent.AsSpan(0, probeLen));
        if (idx < 0)
        {
            return 0;
        }
        int k = Math.Min(tail.Length - idx, recent.Length);
        return tailSpan.Slice(tail.Length - k).SequenceEqual(recent.AsSpan(0, k)) ? k : 0;
    }

    // Trigger the lazy older-history (scrollback) load, idempotent. The window calls it for the focused tab once its tail is up, and for others on switch or after a delay.
    public void Activate()
    {
        lock (_scrollGate)
        {
            if (_scrollStarted)
            {
                return;
            }
            _scrollStarted = true;
        }
        new Thread(LoadScrollbackRun) { IsBackground = true, Name = "shell-scroll-" + _tabId }.Start();
    }

    private void LoadScrollbackRun()
    {
        try
        {
            if (_tailStart <= 0)
            {
                return;
            }
            // One scrollback reconstruction at a time across all tabs, off the UI thread.
            ScrollbackGate.Wait();
            try
            {
                var sw = Stopwatch.StartNew();
                var bytes = ReadLogical(0, _tailStart, _tailPrevLen);
                double readMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                int cols, rows;
                lock (_screen.Sync)
                {
                    cols = _screen.Cols;
                    rows = _screen.Rows;
                }
                // Scratch parses the older history under the live cap; no callbacks are wired, so ancient query/title/marker sequences stay inert.
                var scratch = new TermScreen(cols, rows) { ScrollbackCap = _screen.ScrollbackCap };
                var parser = new VtParser(scratch) { ShowEscapes = _parser.ShowEscapes };
                parser.Feed(bytes, 0, bytes.Length);
                double feedMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var lines = scratch.SnapshotLines();
                double snapshotMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                lock (_screen.Sync)
                {
                    int before = _screen.ScrollbackCount;
                    _screen.PrependScrollback(lines);
                    ScrollbackPrepended?.Invoke(_screen.ScrollbackCount - before);
                }
                double prependMs = sw.Elapsed.TotalMilliseconds;
                PerfLog.Record($"scrollback {_tabId}: {bytes.Length / 1048576.0:0.0}MB lines={lines.Count} read={readMs:0}ms feed={feedMs:0}ms snapshot={snapshotMs:0}ms prepend={prependMs:0}ms");
                OutputReceived?.Invoke();
            }
            finally
            {
                ScrollbackGate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("session", "scrollback load failed: " + ex.Message);
        }
        ScrollbackLoaded?.Invoke();
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

    // Read the logical byte range [from, to) across the previous file then the active file into one buffer. Fresh open-read-close, tolerant of the host appending/rotating concurrently.
    private byte[] ReadLogical(long from, long to, long prevLen)
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
            string path = pos < prevLen ? AppPaths.PrevLogFile(_tabId) : AppPaths.LogFile(_tabId);
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

    private void WriteLoop()
    {
        try
        {
            foreach (var (type, payload) in _sendQueue.GetConsumingEnumerable())
            {
                if (_pipe is not { } pipe)
                {
                    break;
                }
                PipeProtocol.WriteFrame(pipe, type, payload);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                AppLog.Write("session", "writer loop ended: " + ex.Message);
            }
        }
        _sendQueueDrained.Set();
    }

    private void Enqueue(byte type, byte[] payload)
    {
        if (Dead || _disposed || _sendQueue.IsAddingCompleted)
        {
            return;
        }
        try
        {
            if (!_sendQueue.TryAdd((type, payload)))
            {
                AppLog.Write("session", "send queue full — dropping frame");
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private NamedPipeClientStream? TryConnect(int timeoutMs)
    {
        // PipeOptions.Asynchronous is load-bearing: a synchronous handle serializes all I/O in the kernel, so the reader thread's pending blocking Read would block the writer thread's input frames on the same handle. Overlapped handles allow concurrent read/write.
        var pipe = new NamedPipeClientStream(".", AppPaths.PipeName(_tabId), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(timeoutMs);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            return null;
        }
    }

    private void SpawnHost()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!);
        psi.ArgumentList.Add("--shellhost");
        psi.ArgumentList.Add(_tabId);
        psi.ArgumentList.Add(_startDir);
        if (_elevated)
        {
            // Elevate just the host (and its cmd) via UAC; the renderer stays unelevated. A declined prompt throws — reported as a failed start.
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        else
        {
            psi.UseShellExecute = false;
        }
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLog.Write("session", $"{_tabId}: host spawn failed (elevated={_elevated}): {ex.Message}");
        }
    }

    private void ReportLocal(string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"\r\n[termiot] {message}\r\n");
        try
        {
            lock (_screen.Sync)
            {
                _parser.Feed(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("session", "report failed: " + ex);
        }
        OutputReceived?.Invoke();
    }

    public void SendText(string text)
    {
        SendInput(Encoding.UTF8.GetBytes(text));
    }

    public void SendCommandMarker(string command)
    {
        Enqueue(PipeProtocol.MsgCommandMarker, Encoding.UTF8.GetBytes(command));
    }

    public void SendInput(byte[] bytes)
    {
        Enqueue(PipeProtocol.MsgInput, bytes);
    }

    public void Resize(int cols, int rows)
    {
        _pendingCols = cols;
        _pendingRows = rows;
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, cols);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), rows);
        Enqueue(PipeProtocol.MsgResize, payload);
    }

    // Clean disconnect: tells the host the orchestrator is exiting on purpose, so it must NOT trigger the crash-autorestart path. Bounded wait for the writer to flush — closing the app must not hang on a wedged host.
    public void Detach()
    {
        Enqueue(PipeProtocol.MsgDetach, Array.Empty<byte>());
        _sendQueue.CompleteAdding();
        _sendQueueDrained.Wait(DetachFlushTimeoutMs);
        Dispose();
    }

    public void ShutdownHost()
    {
        Enqueue(PipeProtocol.MsgShutdown, Array.Empty<byte>());
        _sendQueue.CompleteAdding();
        _sendQueueDrained.Wait(DetachFlushTimeoutMs);
        Dispose();
    }

    // Immediate teardown for restart/close: force-kill the host process rather than sending a graceful MsgShutdown it might be too busy to read, then drop our pipe so the reader/writer threads unwind. Killing the host first makes any in-flight blocking write fail fast, so Dispose can't stall. Call from a background thread — never the UI thread.
    public void KillHost()
    {
        Dead = true;
        HostInfo.Kill(_tabId);
        Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _sendQueue.CompleteAdding();
        }
        catch
        {
        }
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }
    }
}
