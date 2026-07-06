using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Termiot;

// In-process trigger for "open a tab here": GET http://127.0.0.1:47811/open?dir=<url-encoded path>. Bound to loopback only, one endpoint, GET only, and the ONLY effect is opening a new tab whose shell starts in an existing directory — no command execution of any kind is reachable through this, so even a hostile local page that tricked its way past the browser's local-network protections could at worst annoy the user with tabs. Hosted by whichever renderer wins the port; ownership migrates when it dies.
public static class HttpOpenHost
{
    public const int Port = 47811;
    private const int RetryOwnershipMs = 5000;

    private static bool _started;

    public static void Start(Action<string> openLocally)
    {
        if (_started)
        {
            return;
        }
        _started = true;
        new Thread(() => ServeLoop(openLocally)) { IsBackground = true, Name = "open-http" }.Start();
    }

    private static void ServeLoop(Action<string> openLocally)
    {
        while (true)
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch
            {
                // Another renderer owns the port; check back in case it goes away.
                Thread.Sleep(RetryOwnershipMs);
                continue;
            }
            try
            {
                while (true)
                {
                    using var client = listener.AcceptTcpClient();
                    try
                    {
                        HandleClient(client, openLocally);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("ui", "open-http client error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("ui", "open-http listener ended: " + ex.Message);
            }
            finally
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
        }
    }

    private static void HandleClient(TcpClient client, Action<string> openLocally)
    {
        client.ReceiveTimeout = 2000;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        var requestLine = reader.ReadLine() ?? "";
        var parts = requestLine.Split(' ');
        bool ok = false;
        if (parts.Length >= 2 && parts[0] == "GET" && parts[1].StartsWith("/open?dir=", StringComparison.Ordinal))
        {
            var dir = WebUtility.UrlDecode(parts[1]["/open?dir=".Length..]);
            if (Directory.Exists(dir))
            {
                AppLog.Write("ui", "open-http request for " + dir);
                if (!LastActiveWindow.TryOpenTab(dir))
                {
                    openLocally(dir);
                }
                ok = true;
            }
        }
        var response = ok ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n" : "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
    }
}
