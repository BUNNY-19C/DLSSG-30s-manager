using System.Net;
using System.Net.Sockets;

namespace DLSSGManager;

/// <summary>
/// Where the downloader may connect.
///
/// Two layers. Before a request is sent, the URL must be HTTPS on an allow-listed host
/// (<see cref="IsAllowedUrl"/>); every redirect hop is checked the same way. When the socket is opened,
/// <see cref="ConnectAsync"/> resolves the host itself, refuses non-public addresses and connects to
/// exactly the address it checked — so DNS cannot answer one thing to the check and another to the
/// connection. A connection to the system proxy the user configured is allowed as such: on networks
/// where GitHub resolves to loopback (hosts-file accelerators) the request still goes out through the
/// proxy, which the old pre-resolution check had to skip entirely.
///
/// Content is never trusted because of where it came from; <see cref="PayloadVerifier"/> checks every
/// byte against the release manifest.
/// </summary>
public static class DownloadPolicy
{
    /// <summary>Hosts the downloader may ever contact.</summary>
    private static readonly string[] AllowedHosts =
    {
        // GitHub-operated
        "github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "api.github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        // Third-party mirrors. Their content is accepted only when it matches the release manifest.
        "cdn.jsdelivr.net",
        "gh-proxy.com",
        "ghfast.top",
        "downloads.sourceforge.net",
    };

    /// <summary>
    /// Host families accepted by suffix. SourceForge's redirector hands each request to one of its
    /// regional mirrors (<c>master.dl.sourceforge.net</c>, <c>netix.dl.sourceforge.net</c>, …), so the
    /// final host cannot be listed in advance. The match is on a whole-label boundary.
    /// </summary>
    private static readonly string[] AllowedHostSuffixes = { ".dl.sourceforge.net" };

    public const int MaxRedirects = 5;

    public static bool IsAllowedHost(string host) =>
        AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        AllowedHostSuffixes.Any(suffix => host.Length > suffix.Length &&
                                          host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>HTTPS, a DNS name (not a literal address), on an allow-listed host.</summary>
    public static bool IsAllowedUrl(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.HostNameType == UriHostNameType.Dns &&
        IsAllowedHost(uri.Host);

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>: opens the TCP connection for a request.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        // For a tunnelled request the handler connects to the proxy and passes a synthetic CONNECT
        // message; its target is the proxy itself, so match the endpoint against the proxies in use.
        var request = context.InitialRequestMessage;
        var proxy = request.Method == HttpMethod.Connect ? ConfiguredProxyAt(endpoint) : ProxyFor(request.RequestUri);
        var targets = ConnectTargets(endpoint, addresses, proxy);
        if (targets.Count == 0)
        {
            AppPaths.Log($"[connect] refused {endpoint.Host}:{endpoint.Port} for {request.Method} {request.RequestUri} (proxy: {proxy?.ToString() ?? "none"})");
            throw new HttpRequestException(Loc.T("Fetch.UrlRejected", endpoint.Host));
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(targets.ToArray(), endpoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The addresses a connection to <paramref name="endpoint"/> may use. The configured proxy is
    /// accepted at whatever address it has (it is the user's own choice, often 127.0.0.1); any other
    /// endpoint must be an allow-listed host, and only its public addresses are used.
    /// </summary>
    public static IReadOnlyList<IPAddress> ConnectTargets(DnsEndPoint endpoint, IReadOnlyList<IPAddress> resolved, Uri? proxy)
    {
        if (proxy is not null &&
            string.Equals(proxy.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
            proxy.Port == endpoint.Port)
            return resolved;

        if (!IsAllowedHost(endpoint.Host)) return Array.Empty<IPAddress>();
        return resolved.Where(IsPublicAddress).ToArray();
    }

    /// <summary>
    /// The system proxy at <paramref name="endpoint"/>, if the user configured one there for any of the
    /// allow-listed hosts. Only a proxy the system would actually route our traffic through qualifies, so
    /// a CONNECT to an arbitrary local port is still refused.
    /// </summary>
    private static Uri? ConfiguredProxyAt(DnsEndPoint endpoint)
    {
        foreach (var host in AllowedHosts)
        {
            var via = ProxyFor(new Uri("https://" + host + "/"));
            if (via is not null &&
                string.Equals(via.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
                via.Port == endpoint.Port)
                return via;
        }
        return null;
    }

    /// <summary>The system proxy that will carry a request to <paramref name="target"/>, or null for a direct connection.</summary>
    private static Uri? ProxyFor(Uri? target)
    {
        if (target is null) return null;
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(target)) return null;
            var via = proxy.GetProxy(target);
            return via is null || string.Equals(via.Authority, target.Authority, StringComparison.OrdinalIgnoreCase) ? null : via;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return IsPublicAddress(ip.MapToIPv4());
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Loopback)) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                 // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;  // fe80::/10 link local
            if (b[0] == 0xFF) return false;                           // ff00::/8 multicast
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 doc
            return true;
        }

        var o = ip.GetAddressBytes();
        return o[0] switch
        {
            0 => false,                                     // 0.0.0.0/8
            10 => false,                                    // private
            127 => false,                                   // loopback
            169 when o[1] == 254 => false,                  // link local
            172 when o[1] >= 16 && o[1] <= 31 => false,     // private
            192 when o[1] == 168 => false,                  // private
            192 when o[1] == 0 && o[2] == 0 => false,       // 192.0.0.0/24
            192 when o[1] == 0 && o[2] == 2 => false,       // TEST-NET-1
            198 when o[1] == 18 || o[1] == 19 => false,     // benchmarking
            198 when o[1] == 51 && o[2] == 100 => false,    // TEST-NET-2
            203 when o[1] == 0 && o[2] == 113 => false,     // TEST-NET-3
            100 when o[1] >= 64 && o[1] <= 127 => false,    // CGNAT
            >= 224 => false,                                // multicast + reserved
            _ => true,
        };
    }
}
