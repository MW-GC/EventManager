using System.Net;
using Microsoft.AspNetCore.Http;

namespace MW_GC.EventManager.API.Validation;

/// <summary>
/// The visitor's address for the anonymous like route (#101). This is the ONE place that decides
/// where the address comes from: <see cref="Sources"/> lists the sources in order and the first
/// that holds a usable address wins. Changing the source is a change to that one line.
/// <para>
/// Order (Woody, 2026-10-11): <c>CLIENT-IP</c>, then <c>X-Azure-ClientIP</c>, then the first entry of
/// <c>X-Forwarded-For</c> (later entries are proxies). <c>HttpContext.Connection.RemoteIpAddress</c> is
/// deliberately NOT a source: locally it is always the Functions host (127.0.0.1), and in Azure it may
/// be a front-end address that would make every visitor one.
/// </para>
/// <para>
/// Measured locally (Functions host + isolated worker): <c>CLIENT-IP</c> and <c>X-Azure-ClientIP</c> reach
/// the worker as sent; the host replaces a client's <c>X-Forwarded-For</c> with the address it saw, and
/// the worker's middleware moves that into <c>RemoteIpAddress</c> and removes the header. Locally nothing
/// sets these headers, so a caller can set them; that spoofing is accepted by design for this casual
/// feature. Whether a Static Web Apps linked backend receives the visitor's address in
/// <c>CLIENT-IP</c> is unverified.
/// </para>
/// <para>
/// A value may carry a port (<c>1.2.3.4:5678</c>, <c>[2001:db8::5]:443</c>); a bare IPv6 address
/// (<c>2001:db8::5</c>) keeps all its colons. An unparseable value counts as absent. The result is
/// normalised (an IPv4-mapped IPv6 address becomes IPv4, no scope id) so one visitor always gives the
/// same text. The raw value is never stored or logged: callers only hash the normalised address.
/// </para>
/// </summary>
internal static class ClientIp
{
    public const string ClientIpHeader = "CLIENT-IP";
    public const string AzureClientIpHeader = "X-Azure-ClientIP";
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>The sources tried, in order. The decision on where the address comes from lives here.</summary>
    public static readonly IReadOnlyList<ClientIpSource> Sources =
        [ClientIpSource.ClientIp, ClientIpSource.AzureClientIp, ClientIpSource.ForwardedFor];

    /// <summary>The normalised client address from <see cref="Sources"/>, or null when none holds a usable one.</summary>
    public static string? From(HttpRequest request) => From(request, Sources);

    /// <summary>The normalised client address from the first of <paramref name="sources"/> that holds a usable one.</summary>
    internal static string? From(HttpRequest request, IEnumerable<ClientIpSource> sources)
    {
        foreach (var source in sources)
        {
            if (Read(request, source) is { } address) return address;
        }
        return null;
    }

    /// <summary>The normalised address one source holds, or null.</summary>
    internal static string? Read(HttpRequest request, ClientIpSource source) => source switch
    {
        ClientIpSource.ClientIp => Parse(FirstEntry(request.Headers[ClientIpHeader].ToString())),
        ClientIpSource.AzureClientIp => Parse(FirstEntry(request.Headers[AzureClientIpHeader].ToString())),
        ClientIpSource.ForwardedFor => Parse(FirstEntry(request.Headers[ForwardedForHeader].ToString())),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    // Several header lines arrive joined with commas, so the first comma-separated entry is the client.
    // CR and LF are dropped before anything else looks at the value.
    private static string FirstEntry(string header)
    {
        header = header.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        var comma = header.IndexOf(',');
        return (comma < 0 ? header : header[..comma]).Trim();
    }

    /// <summary>One address with an optional port, IPv4 or IPv6, or null when it is not an address.</summary>
    internal static string? Parse(string entry)
    {
        if (entry.Length == 0) return null;

        string host;
        if (entry.StartsWith('['))
        {
            // [IPv6] or [IPv6]:port
            var close = entry.IndexOf(']');
            if (close < 0) return null;
            var rest = entry[(close + 1)..];
            if (rest.Length > 0 && !IsPort(rest)) return null;
            host = entry[1..close];
        }
        else if (entry.Count(c => c == ':') == 1)
        {
            // IPv4:port. A bare IPv6 address has at least two colons and keeps them all
            // (splitting on the first ':' would cut an IPv6 address apart).
            var colon = entry.IndexOf(':');
            if (!IsPort(entry[colon..])) return null;
            host = entry[..colon];
        }
        else
        {
            host = entry;
        }

        return IPAddress.TryParse(host, out var address) ? Normalise(address) : null;
    }

    private static string Normalise(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // Rebuild from the bytes to drop any scope id.
            address = new IPAddress(address.GetAddressBytes());
        }
        return address.ToString();
    }

    // ":" followed by 1 to 5 digits.
    private static bool IsPort(string text) =>
        text.Length is >= 2 and <= 6 && text[0] == ':' && text.Skip(1).All(char.IsAsciiDigit);
}

/// <summary>Where <see cref="ClientIp"/> may read the visitor's address from.</summary>
internal enum ClientIpSource
{
    /// <summary>The <c>CLIENT-IP</c> header.</summary>
    ClientIp,

    /// <summary>The <c>X-Azure-ClientIP</c> header.</summary>
    AzureClientIp,

    /// <summary>The first entry of the <c>X-Forwarded-For</c> header.</summary>
    ForwardedFor,
}
