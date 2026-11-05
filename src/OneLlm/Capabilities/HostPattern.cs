using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OneLlm.Access;

/// <summary>
/// One entry of a network allow-list: a host name (<c>example.com</c>), every subdomain of a name (<c>*.example.com</c>, which does not
/// include <c>example.com</c> itself), an IP address (<c>203.0.113.7</c>, <c>[::1]</c>) or a range (<c>10.0.0.0/8</c>), each with an optional
/// <c>:port</c>. An entry without a port covers every port.
/// </summary>
/// <remarks>Only an address or range entry (or the name <c>localhost</c>) can grant loopback, private and link-local addresses.
/// A name entry that resolves to one of those is refused when the connection is made, which stops DNS rebinding.</remarks>
public sealed class HostPattern : IEquatable<HostPattern>
{
    private enum Kind
    {
        Name,
        Subdomains,
        Address,
        Range,
    }

    private readonly Kind _kind;
    private readonly string _name;
    private readonly IPAddress? _address;
    private readonly int _prefix;

    private HostPattern(string text, Kind kind, string name, IPAddress? address, int prefix, int? port)
    {
        Text = text;
        _kind = kind;
        _name = name;
        _address = address;
        _prefix = prefix;
        Port = port;
    }

    /// <summary>The entry as written, lower-cased.</summary>
    public string Text { get; }

    /// <summary>The port the entry is limited to, or null for every port.</summary>
    public int? Port { get; }

    /// <summary>True for an address or range entry, which can grant loopback, private and link-local addresses.</summary>
    public bool IsAddress => _kind is Kind.Address or Kind.Range;

    /// <summary>Parses an entry.</summary>
    /// <param name="text">The entry, for example <c>api.example.com</c>, <c>*.example.com:8443</c> or <c>10.0.0.0/8</c>.</param>
    /// <returns>The pattern.</returns>
    /// <exception cref="ArgumentException">The text is empty or not a valid host, address or range.</exception>
    public static HostPattern Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "*")
        {
            throw new ArgumentException("A network allow-list entry must name a host, an address or a range. Use Capabilities.AnyPublicNetwork to allow every public host.", nameof(text));
        }

        int? port = null;
        string host = t;
        if (t[0] == '[')
        {
            int close = t.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException($"'{text}' is not a valid entry.", nameof(text));
            }

            host = t[..(close + 1)];
            string rest = t[(close + 1)..];
            if (rest.Length > 0)
            {
                port = ParsePort(rest, text);
            }
        }
        else if (t.Count(c => c == ':') == 1)
        {
            int colon = t.IndexOf(':', StringComparison.Ordinal);
            host = t[..colon];
            port = ParsePort(t[colon..], text);
        }

        string bare = host.Trim('[', ']');
        int slash = bare.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (!IPAddress.TryParse(bare[..slash], out IPAddress? net)
                || !int.TryParse(bare[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
                || prefix < 0 || prefix > (net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
            {
                throw new ArgumentException($"'{text}' is not a valid address range.", nameof(text));
            }

            // A range written as an IPv4-mapped IPv6 address is the IPv4 range with the prefix shortened by the 96 mapping bits.
            int effective = net.IsIPv4MappedToIPv6 ? prefix - 96 : prefix;
            if (effective < 0)
            {
                throw new ArgumentException($"'{text}' is too wide an IPv4-mapped range.", nameof(text));
            }

            return new HostPattern(t, Kind.Range, "", Normalize(net), effective, port);
        }

        if (IPAddress.TryParse(bare, out IPAddress? ip))
        {
            return new HostPattern(t, Kind.Address, "", Normalize(ip), 0, port);
        }

        bool wildcard = bare.StartsWith("*.", StringComparison.Ordinal);
        string name = CanonicalName(wildcard ? bare[2..] : bare, text);
        return new HostPattern(t, wildcard ? Kind.Subdomains : Kind.Name, name, null, 0, port);
    }

    /// <summary>Whether this entry covers a request to <paramref name="host"/> on <paramref name="port"/>.</summary>
    /// <param name="host">The URI's host: a lower-case name, or an IP literal without brackets.</param>
    /// <param name="port">The URI's port.</param>
    /// <returns>True when the entry grants it.</returns>
    public bool Matches(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (Port is int p && p != port)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return MatchesAddress(ip);
        }

        return _kind switch
        {
            Kind.Name => string.Equals(host, _name, StringComparison.Ordinal),
            Kind.Subdomains => host.Length > _name.Length + 1 && host.EndsWith("." + _name, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Whether this entry is an address or range entry that contains <paramref name="address"/>.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it does.</returns>
    public bool MatchesAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress a = Normalize(address);
        return _kind switch
        {
            Kind.Address => a.Equals(_address),
            Kind.Range => InRange(a, _address!, _prefix),
            _ => false,
        };
    }

    /// <summary>Whether every request this entry grants is also granted by <paramref name="outer"/>.</summary>
    /// <param name="outer">The wider entry.</param>
    /// <returns>True when <paramref name="outer"/> covers this entry.</returns>
    public bool IsCoveredBy(HostPattern outer)
    {
        ArgumentNullException.ThrowIfNull(outer);
        if (outer.Port is int op && Port != op)
        {
            return false;
        }

        switch (_kind)
        {
            case Kind.Name:
                return outer._kind switch
                {
                    Kind.Name => _name == outer._name,
                    Kind.Subdomains => _name.EndsWith("." + outer._name, StringComparison.Ordinal),
                    _ => false,
                };
            case Kind.Subdomains:
                return outer._kind switch
                {
                    Kind.Subdomains => _name == outer._name || _name.EndsWith("." + outer._name, StringComparison.Ordinal),
                    _ => false,
                };
            case Kind.Address:
                return outer.MatchesAddress(_address!);
            default:
                return outer._kind == Kind.Range
                    && _prefix >= outer._prefix
                    && _address!.AddressFamily == outer._address!.AddressFamily
                    && InRange(_address, outer._address, outer._prefix);
        }
    }

    /// <inheritdoc />
    public bool Equals(HostPattern? other) => other is not null && Text == other.Text;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as HostPattern);

    /// <inheritdoc />
    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>Turns an IPv4-mapped IPv6 address into the IPv4 address it stands for, so both spellings compare equal.</summary>
    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        byte[] a = address.GetAddressBytes();
        byte[] n = network.GetAddressBytes();
        int bits = prefix;
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private static int ParsePort(string rest, string original)
    {
        if (rest.Length < 2 || rest[0] != ':'
            || !int.TryParse(rest.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"'{original}' has an invalid port.", nameof(original));
        }

        return port;
    }

    private static string CanonicalName(string name, string original)
    {
        string n = name.TrimEnd('.');
        if (n.Length == 0 || n.Contains('*', StringComparison.Ordinal) || n.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{original}' is not a valid host name.", nameof(original));
        }

        try
        {
            return new Uri("http://" + n + "/").IdnHost.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            throw new ArgumentException($"'{original}' is not a valid host name.", nameof(original));
        }
    }
}
