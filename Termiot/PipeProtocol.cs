using System.Buffers.Binary;
using System.IO;

namespace Termiot;

// Frames are [type:1][payloadLength:4][payload]. Client → host: Hello(long replayFromOffset), Input(raw pty bytes), Resize(int cols, int rows), Shutdown (terminate the shell; its folder stays on disk for resurrection), Detach (clean disconnect — do not treat as a window crash), Associate(int windowPid, long windowStartTicks, utf8 windowId — the window this shell now belongs to, for the orphan liveness check). Host → client: Output(raw pty bytes), Exited(int code).
public static class PipeProtocol
{
    public const byte MsgHello = 1;
    public const byte MsgInput = 2;
    public const byte MsgResize = 3;
    public const byte MsgShutdown = 4;
    public const byte MsgOutput = 5;
    public const byte MsgExited = 6;
    public const byte MsgDetach = 7;
    public const byte MsgAssociate = 8;
    // Client → host: the renderer is about to send this command as input. The host embeds it into the output stream as an APC escape sequence (ESC _ termiot-cmd:<base64> ESC \), which structures the otherwise-flat log into command/output pairs while staying invisible to the terminal — parsers consume APC strings without rendering, and replay keeps the markers aligned with the output they precede.
    public const byte MsgCommandMarker = 9;

    // A malformed or hostile peer can put anything in the length field; cap it so we drop the connection instead of attempting a huge allocation.
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public readonly record struct Frame(byte Type, byte[] Payload);

    public static void WriteFrame(Stream stream, byte type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(1), payload.Length);
        stream.Write(header);
        if (payload.Length > 0)
        {
            stream.Write(payload);
        }
        stream.Flush();
    }

    public static Frame? ReadFrame(Stream stream)
    {
        var header = new byte[5];
        if (!ReadExact(stream, header))
        {
            return null;
        }
        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (len < 0 || len > MaxFrameBytes)
        {
            return null;
        }
        var payload = new byte[len];
        if (!ReadExact(stream, payload))
        {
            return null;
        }
        return new Frame(header[0], payload);
    }

    private static bool ReadExact(Stream stream, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n;
            try
            {
                n = stream.Read(buf, read, buf.Length - read);
            }
            catch
            {
                return false;
            }
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }
}
