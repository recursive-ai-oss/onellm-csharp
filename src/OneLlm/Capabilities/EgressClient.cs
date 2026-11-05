using System.Net;
using System.Net.Sockets;
using System.Text;
using OneLlm.Tools;

namespace OneLlm.Access;

/// <summary>
/// The HTTP client the built-in tools use to reach the network. Every request is checked against the grants before it is sent, every
/// redirect is checked again for the hop it points at, and every connection is checked against the address the host name resolved to.
/// </summary>
/// <remarks>
/// Redirects are followed here, one hop at a time, so no hop can reach a host the grants do not cover. Credentials the caller
/// put in headers are dropped when a redirect leaves the original scheme, host and port. The handler ignores system proxy settings
/// because a proxy would make the connection to a different address than the one checked.
/// </remarks>
public static class EgressClient
{
    private static readonly HttpRequestOptionsKey<ICapabilities> s_capsKey = new("OneLlm.Capabilities");
    private static readonly Lazy<HttpClient> s_client = new(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectAsync,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Sends a request, following up to <paramref name="maxRedirects"/> redirects, each checked against <paramref name="caps"/>.</summary>
    /// <param name="caps">The grants. A request or redirect to a host they do not allow is refused before anything is sent.</param>
    /// <param name="method">The method.</param>
    /// <param name="uri">The first URI.</param>
    /// <param name="headers">Request headers. Credential-looking headers are dropped when a redirect leaves the original scheme, host and port.</param>
    /// <param name="body">A UTF-8 text body, or null.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="maxRedirects">How many redirects to follow. Default 5.</param>
    /// <returns>The final response, with its headers read and its body not yet read. The caller disposes it.</returns>
    /// <exception cref="CapabilityMissingException">A URI is not allowed by <paramref name="caps"/>.</exception>
    /// <exception cref="HttpRequestException">The connection failed, resolved to an address the grants refuse, or too many redirects.</exception>
    public static async Task<HttpResponseMessage> SendAsync(
        ICapabilities caps,
        HttpMethod method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string? body,
        CancellationToken cancellationToken,
        int maxRedirects = 5)
    {
        Uri origin = uri;
        HttpMethod current = method;
        string? currentBody = body;
        for (int hop = 0; ; hop++)
        {
            Require(caps, uri);
            using var request = new HttpRequestMessage(current, uri);
            request.Options.Set(s_capsKey, caps);
            bool sameOrigin = SameOrigin(origin, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "OneLlm/" + AssemblyMarker.Version);
            string? contentType = null;
            foreach (KeyValuePair<string, string> h in headers)
            {
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = h.Value;
                }
                else if ((sameOrigin || !IsCredentialHeader(h.Key)) && !string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            if (currentBody is not null && current != HttpMethod.Get && current != HttpMethod.Head)
            {
                request.Content = new StringContent(currentBody, Encoding.UTF8);
                if (contentType is not null)
                {
                    // StringContent already carries a default text/plain type; replace it rather than send two values.
                    request.Content.Headers.Remove("Content-Type");
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            HttpResponseMessage response = await s_client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not Uri location)
            {
                return response;
            }

            response.Dispose();
            if (hop >= maxRedirects)
            {
                throw new HttpRequestException($"Stopped after {maxRedirects} redirects.");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            // RFC 9110 15.4: 303 always becomes GET; 301 and 302 turn POST into GET. 307 and 308 keep the method and body.
            if (response.StatusCode == HttpStatusCode.SeeOther || (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && current == HttpMethod.Post))
            {
                current = HttpMethod.Get;
                currentBody = null;
            }
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of a response body.</summary>
    /// <param name="response">The response.</param>
    /// <param name="maxBytes">The most bytes to keep.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read, and whether the body was longer.</returns>
    public static async Task<(byte[] Bytes, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int n = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return (buffer.ToArray(), false);
            }

            int room = maxBytes - (int)buffer.Length;
            if (n > room)
            {
                buffer.Write(chunk, 0, room);
                return (buffer.ToArray(), true);
            }

            buffer.Write(chunk, 0, n);
        }
    }

    /// <summary>Throws <see cref="CapabilityMissingException"/> unless the grants allow a request to <paramref name="uri"/>.</summary>
    /// <param name="caps">The grants.</param>
    /// <param name="uri">The URI about to be requested.</param>
    /// <exception cref="CapabilityMissingException">The grants do not allow it.</exception>
    public static void Require(ICapabilities caps, Uri uri)
    {
        if (!caps.IsNetworkAllowed(uri))
        {
            string host = uri.IsAbsoluteUri ? uri.Host : uri.ToString();
            throw new CapabilityMissingException($"Network access to {host}", $"that host to the runtime, with Capabilities.Network(\"{host}\")");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(EgressPolicy.CanonicalHost(a), EgressPolicy.CanonicalHost(b), StringComparison.Ordinal)
        && a.Port == b.Port;

    private static bool IsCredentialHeader(string name) =>
        name.Contains("auth", StringComparison.OrdinalIgnoreCase)
        || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    // Resolves the host here, refuses it when any address is outside the grants, and connects to an address that was checked.
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        HttpRequestMessage? request = context.InitialRequestMessage;
        if (request is null || !request.Options.TryGetValue(s_capsKey, out ICapabilities? caps) || request.RequestUri is not Uri uri)
        {
            throw new HttpRequestException("This client only sends requests that carry their grants.");
        }

        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"The host '{host}' did not resolve.");
        }

        foreach (IPAddress address in addresses)
        {
            if (!caps.IsAddressAllowed(uri, address))
            {
                throw new HttpRequestException($"Network access to '{host}' was refused: it resolves to an address the runtime does not grant.");
            }
        }

        Exception? last = null;
        foreach (IPAddress address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw new HttpRequestException($"Could not connect to '{host}'.", last);
    }
}
