using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OneLlm.Tests.Support;

/// <summary>A request the stub server received.</summary>
internal sealed record StubRequest(string Method, string Target, Dictionary<string, string> Headers, string Body);

/// <summary>What the stub answers with.</summary>
internal sealed record StubResponse(int Status, string Body = "", string ContentType = "text/plain; charset=utf-8", Dictionary<string, string>? Headers = null, byte[]? Bytes = null);

/// <summary>A minimal HTTP/1.1 server on the loopback interface, enough to test tools that talk to a web server. One response per connection.</summary>
internal sealed class StubServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<StubRequest, StubResponse> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<StubRequest> _requests = [];

    private readonly bool _keepAlive;

    /// <param name="handler">Answers each request.</param>
    /// <param name="keepAlive">True to serve many requests per connection, so a client pool reuses it.</param>
    public StubServer(Func<StubRequest, StubResponse> handler, bool keepAlive = false)
    {
        _handler = handler;
        _keepAlive = keepAlive;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public IReadOnlyList<StubRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                do
                {
                var head = new StringBuilder();
                byte[] one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one) == 0)
                    {
                        return;
                    }

                    head.Append((char)one[0]);
                }

                string[] lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                string[] first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string l in lines.Skip(1))
                {
                    int c = l.IndexOf(':', StringComparison.Ordinal);
                    headers[l[..c]] = l[(c + 1)..].Trim();
                }

                string body = "";
                if (headers.TryGetValue("Content-Length", out string? len) && int.Parse(len) > 0)
                {
                    byte[] buf = new byte[int.Parse(len)];
                    await stream.ReadExactlyAsync(buf);
                    body = Encoding.UTF8.GetString(buf);
                }

                var request = new StubRequest(first[0], first[1], headers, body);
                lock (_requests)
                {
                    _requests.Add(request);
                }

                StubResponse r = _handler(request);
                byte[] payload = r.Bytes ?? Encoding.UTF8.GetBytes(r.Body);
                var resp = new StringBuilder();
                resp.Append($"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {payload.Length}\r\n{(_keepAlive ? "" : "Connection: close\r\n")}");
                foreach ((string k, string v) in r.Headers ?? [])
                {
                    resp.Append($"{k}: {v}\r\n");
                }

                resp.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(resp.ToString()));
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                }
                while (_keepAlive);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away.
            }
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        301 => "Moved Permanently",
        302 => "Found",
        307 => "Temporary Redirect",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "Status",
    };
}

/// <summary>A scratch directory under the test temp path, removed on dispose.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch()
    {
        // The temp path honours TMPDIR, which the test runs point at disk.
        string baseDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "onellm-tools-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(baseDir);
        // The guard reports real paths, and macOS temp directories sit behind a link (/var is /private/var), so the scratch directory
        // is named by its real path from the start; tests that compare or intercept paths then see the spelling the guard returns.
        baseDir = OneLlm.Capabilities.Files(baseDir).ResolveRead(".");
        Root = Path.GetFullPath(Path.Combine(baseDir, "root"));
        Outside = Path.GetFullPath(Path.Combine(baseDir, "outside"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        Base = baseDir;
    }

    public string Base { get; }

    /// <summary>The granted directory.</summary>
    public string Root { get; }

    /// <summary>A sibling directory that is not granted.</summary>
    public string Outside { get; }

    public string In(string relative) => Path.Combine(Root, relative);

    public string Write(string relative, string text)
    {
        string p = In(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    /// <summary>Creates a symbolic link; returns false when the platform or account does not allow it (Windows without developer mode).</summary>
    public static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
