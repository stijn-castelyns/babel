using System.Net;
using System.Net.Sockets;
using System.Text;
using Harness.Sdk;

namespace Harness.Sandbox.Egress;

/// <summary>
/// The allowlist of a <c>network: allowlist</c> sandbox: <c>github.com</c> (exactly that host), <c>*.github.com</c> (any
/// subdomain, not the domain itself), optionally with a port (<c>registry.local:8443</c>). Without a port, 80 and 443 are allowed.
/// </summary>
public sealed class EgressAllowlist(IEnumerable<string> patterns)
{
    private readonly (string Host, int? Port)[] _rules = [.. patterns.Select(Parse)];

    private static (string, int?) Parse(string pattern)
    {
        string p = pattern.Trim().ToLowerInvariant();
        int colon = p.LastIndexOf(':');
        if (colon > 0 && !p.StartsWith('[') && p.IndexOf(':') == colon && int.TryParse(p[(colon + 1)..], out int port)) return (p[..colon], port);
        if (p.StartsWith('[') && p.EndsWith("]", StringComparison.Ordinal)) p = p[1..^1];
        else if (p.StartsWith('[') && p.LastIndexOf("]:", StringComparison.Ordinal) is int end and > 0 && int.TryParse(p[(end + 2)..], out int v6port))
            return (p[1..end], v6port);
        return (p, null);
    }

    public bool Allows(string host, int port)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        foreach ((string rule, int? rulePort) in _rules)
        {
            bool hostMatches = rule.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(rule[1..], StringComparison.Ordinal) && host.Length > rule.Length - 1
                : host == rule;
            if (hostMatches && (rulePort is int p ? p == port : port is 80 or 443)) return true;
        }
        return false;
    }
}

/// <summary>
/// The daemon's egress proxy for one sandbox: an HTTP proxy on a Unix socket that only connects to allowlisted hosts.
/// It speaks <c>CONNECT host:port</c> (HTTPS and anything else tunnelled) and absolute-form plain HTTP requests, which it
/// forwards with <c>Connection: close</c> so one connection cannot be reused for another host. Names are resolved here, in the
/// daemon. When the daemon itself sits behind a proxy (<c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c>, honouring <c>NO_PROXY</c>),
/// connections are chained through it.
/// </summary>
public sealed class EgressProxy : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private readonly Socket _listener;
    private readonly EgressAllowlist _allowlist;
    private readonly Action<EgressAttempt>? _log;
    private readonly UpstreamProxy? _upstream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    private readonly IPAddress? _onlyPeer;

    private EgressProxy(Socket listener, string? socketPath, IPAddress? onlyPeer, EgressAllowlist allowlist, Action<EgressAttempt>? log, UpstreamProxy? upstream)
    {
        _listener = listener;
        SocketPath = socketPath;
        _onlyPeer = onlyPeer;
        _allowlist = allowlist;
        _log = log;
        _upstream = upstream;
        _listener.Listen(64);
        _accepting = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The Unix socket a bubblewrap sandbox reaches the proxy on, when it listens on one.</summary>
    public string? SocketPath { get; }

    /// <summary>The TCP address a container reaches the proxy on, when it listens on one.</summary>
    public IPEndPoint? Endpoint => _listener.LocalEndPoint as IPEndPoint;

    /// <summary>Starts a proxy listening on <paramref name="socketPath"/>.</summary>
    public static EgressProxy Start(string socketPath, IEnumerable<string> allowHosts, Action<EgressAttempt>? log = null, bool useEnvironmentProxy = true)
    {
        if (File.Exists(socketPath)) File.Delete(socketPath);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        return new(listener, socketPath, null, new EgressAllowlist(allowHosts), log, useEnvironmentProxy ? UpstreamProxy.FromEnvironment() : null);
    }

    /// <summary>
    /// Starts a proxy on a TCP address (a container network's gateway), answering only connections from
    /// <paramref name="onlyPeer"/>, so other containers or local processes that can reach the address get nothing.
    /// </summary>
    public static EgressProxy StartTcp(IPAddress bind, IPAddress onlyPeer, IEnumerable<string> allowHosts, Action<EgressAttempt>? log = null, bool useEnvironmentProxy = true)
    {
        Socket listener = new(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(bind, 0));
        return new(listener, null, onlyPeer, new EgressAllowlist(allowHosts), log, useEnvironmentProxy ? UpstreamProxy.FromEnvironment() : null);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            if (_onlyPeer is not null && (client.RemoteEndPoint as IPEndPoint)?.Address.Equals(_onlyPeer) != true)
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(Socket clientSocket)
    {
        using Socket _ = clientSocket;
        await using NetworkStream client = new(clientSocket, ownsSocket: false);
        try
        {
            (string head, byte[] rest) = await ReadHeadAsync(client, _stop.Token);
            string[] lines = head.Split("\r\n");
            string[] request = lines[0].Split(' ');
            if (request.Length != 3) { await RespondAsync(client, 400, "Bad Request", "malformed request line"); return; }
            string method = request[0], target = request[1], version = request[2];

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySplitHostPort(target, 443, out string host, out int port)) { await RespondAsync(client, 400, "Bad Request", "CONNECT needs host:port"); return; }
                if (!Decide(host, port)) { await DenyAsync(client, host, port); return; }
                using Socket upstream = await ConnectAsync(host, port, tunnel: true);
                await using NetworkStream server = new(upstream, ownsSocket: false);
                await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), _stop.Token);
                if (rest.Length > 0) await server.WriteAsync(rest, _stop.Token);
                await PipeAsync(client, server);
                return;
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http")
            {
                await RespondAsync(client, 400, "Bad Request", "use an absolute http:// URL, or CONNECT for https");
                return;
            }
            if (!Decide(uri.IdnHost, uri.Port)) { await DenyAsync(client, uri.Host, uri.Port); return; }
            using Socket origin = await ConnectAsync(uri.IdnHost, uri.Port, tunnel: false);
            await using NetworkStream originStream = new(origin, ownsSocket: false);
            // Through an upstream proxy the absolute form stays; to the origin it becomes origin-form.
            StringBuilder forwarded = new($"{method} {(_upstream is not null && !_upstream.Bypass(uri.IdnHost) ? target : uri.PathAndQuery)} {version}\r\n");
            foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
            {
                string name = line.Split(':', 2)[0].Trim();
                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                    continue;
                forwarded.Append(line).Append("\r\n");
            }
            if (_upstream?.Authorization is { } auth && !_upstream.Bypass(uri.IdnHost)) forwarded.Append("Proxy-Authorization: ").Append(auth).Append("\r\n");
            forwarded.Append("Connection: close\r\n\r\n");
            await originStream.WriteAsync(Encoding.ASCII.GetBytes(forwarded.ToString()), _stop.Token);
            if (rest.Length > 0) await originStream.WriteAsync(rest, _stop.Token);
            await PipeAsync(client, originStream);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException)
        {
            try { await RespondAsync(client, 502, "Bad Gateway", ex.Message); } catch (Exception) { /* the client is gone */ }
        }
    }

    private bool Decide(string host, int port)
    {
        bool allowed = _allowlist.Allows(host, port);
        try { _log?.Invoke(new EgressAttempt(host, port, allowed)); }
        catch (Exception) { /* a logging failure must not change the decision */ }
        return allowed;
    }

    private Task DenyAsync(Stream client, string host, int port) =>
        RespondAsync(client, 403, "Forbidden", $"harness egress proxy: {host}:{port} is not in this sandbox's allowHosts");

    private async Task<Socket> ConnectAsync(string host, int port, bool tunnel)
    {
        if (_upstream is not null && !_upstream.Bypass(host))
        {
            Socket proxy = await DialAsync(_upstream.Host, _upstream.Port);
            if (!tunnel) return proxy;
            // Chain: ask the upstream proxy for a tunnel to the same destination.
            await using NetworkStream s = new(proxy, ownsSocket: false);
            string connect = $"CONNECT {FormatHost(host)}:{port} HTTP/1.1\r\nHost: {FormatHost(host)}:{port}\r\n"
                + (_upstream.Authorization is { } auth ? $"Proxy-Authorization: {auth}\r\n" : "") + "\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(connect), _stop.Token);
            (string head, byte[] rest) = await ReadHeadAsync(s, _stop.Token);
            if (!head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !head.StartsWith("HTTP/1.0 200", StringComparison.Ordinal))
            {
                proxy.Dispose();
                throw new IOException($"upstream proxy refused {host}:{port}: {head.Split("\r\n")[0]}");
            }
            if (rest.Length > 0) throw new IOException("upstream proxy sent data before the tunnel was used");
            return proxy;
        }
        return await DialAsync(host, port);
    }

    private async Task<Socket> DialAsync(string host, int port)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(host, port, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static async Task PipeAsync(Stream a, Stream b)
    {
        Task ab = CopyThenShutdownAsync(a, b);
        Task ba = CopyThenShutdownAsync(b, a);
        await Task.WhenAll(ab, ba);
    }

    private static async Task CopyThenShutdownAsync(Stream from, Stream to)
    {
        try
        {
            await from.CopyToAsync(to);
            if (to is NetworkStream ns) ns.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    /// <summary>Reads up to the blank line ending an HTTP head; returns the head and any bytes already read past it.</summary>
    internal static async Task<(string Head, byte[] Remainder)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) throw new IOException("connection closed before the request head was complete");
            length += read;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..length]);
            if (length == buffer.Length) throw new InvalidDataException("request head too large");
        }
    }

    private static async Task RespondAsync(Stream client, int status, string reason, string body)
    {
        byte[] text = Encoding.UTF8.GetBytes(body + "\n");
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {text.Length}\r\nConnection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head));
        await client.WriteAsync(text);
    }

    internal static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        port = defaultPort;
        if (target.StartsWith('['))
        {
            int close = target.IndexOf(']');
            host = close > 0 ? target[1..close] : "";
            return close > 0 && (close == target.Length - 1 || (target[close + 1] == ':' && int.TryParse(target[(close + 2)..], out port)));
        }
        int colon = target.LastIndexOf(':');
        host = colon >= 0 ? target[..colon] : target;
        return host.Length > 0 && (colon < 0 || int.TryParse(target[(colon + 1)..], out port)) && port is > 0 and < 65536;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { await _accepting; } catch (Exception) { /* stopping */ }
        if (SocketPath is not null) try { File.Delete(SocketPath); } catch (IOException) { }
        _stop.Dispose();
    }
}

/// <summary>The proxy the daemon itself must use, from <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c> (and <c>NO_PROXY</c>).</summary>
internal sealed class UpstreamProxy
{
    private readonly string[] _noProxy;

    private UpstreamProxy(Uri uri, string? noProxy)
    {
        Host = uri.IdnHost;
        Port = uri.Port;
        if (uri.UserInfo.Length > 0)
            Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
        _noProxy = [.. (noProxy ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.ToLowerInvariant())];
    }

    public string Host { get; }
    public int Port { get; }
    public string? Authorization { get; }

    public static UpstreamProxy? FromEnvironment()
    {
        string? value = Env("HTTPS_PROXY") ?? Env("HTTP_PROXY") ?? Env("ALL_PROXY");
        if (value is null || !Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "http")
            return null;
        return new UpstreamProxy(uri, Env("NO_PROXY"));
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } upper ? upper
        : Environment.GetEnvironmentVariable(name.ToLowerInvariant()) is { Length: > 0 } lower ? lower : null;

    /// <summary>True when <c>NO_PROXY</c> says to connect to <paramref name="host"/> directly.</summary>
    public bool Bypass(string host)
    {
        host = host.ToLowerInvariant();
        IPAddress.TryParse(host, out IPAddress? ip);
        foreach (string entry in _noProxy)
        {
            if (entry == "*") return true;
            if (ip is not null && entry.Contains('/') && IPNetwork.TryParse(entry, out IPNetwork network) && network.Contains(ip)) return true;
            string suffix = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[1..] : entry.StartsWith('.') ? entry : "." + entry;
            if (host == entry.TrimStart('*', '.') || host.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
