using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Termiot.Terminal;

namespace Termiot;

// UI-side connection to a tab's shell host. Connecting to an already-running host (surviving a UI restart or crash) is preferred; only when none answers is a fresh host spawned, which replays the persisted log so the scrollback is restored either way. All pipe writes happen on a dedicated writer thread behind a bounded queue — the UI thread must never block on a misbehaving host.
public sealed class ShellSession : IDisposable
{
    private const int ConnectExistingTimeoutMs = 300;
    private const int SpawnConnectTimeoutMs = 15000;
    private const int ConnectPollMs = 100;
    private const int SendQueueCap = 1024;
    private const int DetachFlushTimeoutMs = 300;

    private readonly string _tabId;
    private readonly string _startDir;
    private readonly string _windowId;
    private readonly TermScreen _screen;
    private readonly VtParser _parser;
    private readonly BlockingCollection<(byte Type, byte[] Payload)> _sendQueue = new(new ConcurrentQueue<(byte, byte[])>(), SendQueueCap);
    private readonly ManualResetEventSlim _sendQueueDrained = new(false);
    private NamedPipeClientStream? _pipe;
    private long _offset;
    private volatile bool _disposed;
    private volatile int _pendingCols;
    private volatile int _pendingRows;

    public bool Dead { get; private set; }
    public event Action? OutputReceived;
    public event Action<int>? Exited;

    private ShellSession(string tabId, string startDir, string windowId, TermScreen screen, VtParser parser)
    {
        _tabId = tabId;
        _startDir = startDir;
        _windowId = windowId;
        _screen = screen;
        _parser = parser;
        _parser.OnRespond = SendInput;
    }

    // Two-phase start: create, subscribe to events, then Begin. Launching the reader inside the factory loses replay notifications — a live host accepts and replays faster than the caller can attach its handlers.
    public static ShellSession Create(string tabId, string startDir, string windowId, TermScreen screen, VtParser parser)
    {
        return new ShellSession(tabId, startDir, windowId, screen, parser);
    }

    public void Begin()
    {
        new Thread(Run) { IsBackground = true, Name = "shell-read-" + _tabId }.Start();
    }

    private void Run()
    {
        try
        {
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
            var hello = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(hello, _offset);
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
            AppLog.Write("session", $"{_tabId}: handshake sent, reading frames");
            new Thread(WriteLoop) { IsBackground = true, Name = "shell-write-" + _tabId }.Start();
            bool loggedFirstOutput = false;
            while (true)
            {
                var frame = PipeProtocol.ReadFrame(pipe);
                if (frame is not { } f)
                {
                    break;
                }
                switch (f.Type)
                {
                    case PipeProtocol.MsgOutput:
                        if (!loggedFirstOutput)
                        {
                            loggedFirstOutput = true;
                            AppLog.Write("session", $"{_tabId}: first output frame ({f.Payload.Length} bytes)");
                        }
                        _offset += f.Payload.Length;
                        try
                        {
                            lock (_screen.Sync)
                            {
                                _parser.Feed(f.Payload, 0, f.Payload.Length);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("session", "parser failed on output chunk: " + ex);
                        }
                        OutputReceived?.Invoke();
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
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--shellhost");
        psi.ArgumentList.Add(_tabId);
        psi.ArgumentList.Add(_startDir);
        Process.Start(psi);
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
