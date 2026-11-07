using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OneLlm.JavaScript;

/// <summary>A short-lived loopback listener. Authentication and identity are fixed for the lifetime of one child process.</summary>
internal sealed class ToolCallbackEndpoint : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop;
    private readonly Task _serve;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> _call;
    private readonly int _maxBytes;

    public ToolCallbackEndpoint(Func<string, JsonObject, CancellationToken, Task<JsonNode?>> call, int maxBytes, CancellationToken token)
    {
        _call = call;
        _maxBytes = maxBytes;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        for (int attempt = 0; ; attempt++)
        {
            Url = "http://127.0.0.1:" + RandomNumberGenerator.GetInt32(20000, 60000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(Url);
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 9) { }
        }

        _serve = ServeAsync();
    }

    public string Url { get; }
    public string Token { get; }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext request = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
                await RespondAsync(request).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        using HttpListenerResponse response = context.Response;
        HttpListenerRequest request = context.Request;
        if (!string.Equals(request.Headers["Authorization"], "Bearer " + Token, StringComparison.Ordinal))
        {
            response.StatusCode = 401;
            return;
        }

        if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/" || request.ContentLength64 < 0 || request.ContentLength64 > _maxBytes)
        {
            response.StatusCode = 400;
            return;
        }

        try
        {
            byte[] body = new byte[(int)request.ContentLength64];
            await request.InputStream.ReadExactlyAsync(body, _stop.Token).ConfigureAwait(false);
            JsonObject input = JsonNode.Parse(body) as JsonObject ?? throw new FormatException();
            string name = input["name"]?.GetValue<string>() ?? throw new FormatException();
            JsonObject inputs = input["inputs"] as JsonObject ?? new JsonObject();
            JsonNode? result = await _call(name, inputs, _stop.Token).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(result?.ToJsonString() ?? "null");
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Never echo the request, host exception or bearer token.
            response.StatusCode = 400;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        await _serve.ConfigureAwait(false);
        _stop.Dispose();
    }
}
