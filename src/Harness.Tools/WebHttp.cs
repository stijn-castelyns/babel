using System.Net;
using System.Net.Sockets;

namespace Harness.Tools;

/// <summary>
/// The HTTP clients behind the web tools. Both run in the daemon, outside the sandbox, so <see cref="Fetch"/> only connects
/// to public addresses: the agent picks its URLs, and the daemon can reach its own API, the LAN, the tailnet and the cloud
/// metadata endpoint. The address is checked when the connection is made, so a name that re-resolves cannot slip past.
/// </summary>
internal static class WebHttp
{
    /// <summary>An honest user agent; DuckDuckGo answers it, while imitating a browser earns its bot check.</summary>
    public const string UserAgent = "harness/1.0";

    private static readonly IWebProxy Proxy = HttpClient.DefaultProxy;

    /// <summary>For search engines: fixed, owner-configured endpoints (a SearXNG instance may well be on localhost).</summary>
    public static HttpClient Search { get; } = Create(guarded: false, TimeSpan.FromSeconds(20));

    /// <summary>For agent-chosen URLs: public addresses only, redirects handled by the caller.</summary>
    public static HttpClient Fetch { get; } = Create(guarded: true, TimeSpan.FromSeconds(30));

    private static HttpClient Create(bool guarded, TimeSpan timeout)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = !guarded,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            Proxy = Proxy,
        };
        if (guarded) handler.ConnectCallback = ConnectPublicAsync;
        HttpClient client = new(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>Why <paramref name="uri"/> may not be fetched, or null. Gives a clear answer before any connection is tried.</summary>
    public static async Task<string?> RefusalAsync(Uri uri, CancellationToken ct)
    {
        string host = uri.DnsSafeHost;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal)) addresses = [literal];
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException)
            {
                // Behind a proxy the daemon may not resolve outside names itself; the proxy does.
                return ThroughProxy(uri) ? null : $"could not resolve {host}.";
            }
        }
        if (addresses.Length == 0) return $"could not resolve {host}.";
        return addresses.FirstOrDefault(a => !PublicAddress.IsPublic(a)) is { } blocked
            ? $"{host} is {blocked}, which is not a public address; web_fetch only reaches the public internet."
            : null;
    }

    private static bool ThroughProxy(Uri uri) => !Proxy.IsBypassed(uri) && Proxy.GetProxy(uri) is { } p && p != uri;

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        DnsEndPoint endpoint = context.DnsEndPoint;
        string host = endpoint.Host.Trim('[', ']');
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);

        // The owner's proxy may sit on a private address; everything else must be public.
        Uri target = context.InitialRequestMessage.RequestUri!;
        bool toProxy = ThroughProxy(target) && Proxy.GetProxy(target) is { } proxy
            && string.Equals(proxy.DnsSafeHost, host, StringComparison.OrdinalIgnoreCase) && proxy.Port == endpoint.Port;
        if (!toProxy) addresses = [.. addresses.Where(PublicAddress.IsPublic)];
        if (addresses.Length == 0) throw new HttpRequestException($"{host} is not a public address; web_fetch only reaches the public internet.");

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Tells public internet addresses from loopback, private, link-local, carrier-grade NAT and reserved ones.</summary>
internal static class PublicAddress
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 or >= 224                  // "this" network, private, loopback, multicast and reserved
                || (b[0] == 100 && (b[1] & 0xC0) == 64)               // 100.64/10: carrier-grade NAT, Tailscale
                || (b[0] == 169 && b[1] == 254)                       // link-local, cloud metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)            // IETF protocol assignments
                || (b[0] == 198 && (b[1] & 0xFE) == 18));             // benchmarking
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address that decides.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return IsPublic(new IPAddress(b.AsSpan(12, 4)));
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b.AsSpan(2, 4)));
        return (b[0] & 0xE0) == 0x20;                                 // global unicast 2000::/3; excludes ::1, fc00::/7, fe80::/10, ff00::/8
    }
}
