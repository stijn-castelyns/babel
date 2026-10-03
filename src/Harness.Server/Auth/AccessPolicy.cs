using System.Collections.Concurrent;
using System.Net;
using Harness.Client;
using Microsoft.AspNetCore.Http;

namespace Harness.Server.Auth;

/// <summary>
/// The scope each API route needs. Reads need <c>read</c>; starting and steering work needs <c>run</c>; answering
/// approvals needs <c>approve</c>; maintenance needs <c>admin</c>. Minting tokens and approving pairings only happen over
/// the local socket, so the machine stays the root of trust. Starting and polling a pairing need nothing.
/// </summary>
public static class AccessPolicy
{
    public const string Anonymous = "anonymous", LocalOnly = "local", Authenticated = "authenticated";
    /// <summary>The local socket, or an owner's browser session after a passkey step-up (the PWA's settings screen).</summary>
    public const string LocalOrOwner = "local-or-owner";

    public static string Required(string method, PathString path)
    {
        string p = path.Value?.TrimEnd('/') ?? "";
        bool get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        // Browser sign-in checks its own cookie; the web app's files are public.
        if (p.StartsWith("/auth/", StringComparison.Ordinal) || !p.StartsWith("/api/", StringComparison.Ordinal)) return Anonymous;
        if (p.StartsWith("/api/admin/", StringComparison.Ordinal)) return LocalOnly;
        if (p is "/api/pair" or "/api/pair/token" && HttpMethods.IsPost(method)) return Anonymous;
        if (p == "/api/whoami") return Authenticated;
        // Subscribing a device to notifications changes nothing on the daemon; viewers may do it.
        if (p.StartsWith("/api/push/", StringComparison.Ordinal)) return ApiScopes.Read;
        if (p == "/api/tokens/self" && HttpMethods.IsDelete(method)) return Authenticated;   // a device can always log itself out
        // Minting tokens stays on the machine; listing and revoking them and deciding pairings is also open to the owner's browser.
        if (p == "/api/tokens" && HttpMethods.IsPost(method)) return LocalOnly;
        if (p.StartsWith("/api/tokens", StringComparison.Ordinal) || p.StartsWith("/api/pairings", StringComparison.Ordinal)) return LocalOrOwner;
        if (p is "/api/sessions/reindex" or "/api/retention/sweep" or "/api/triggers/reload" || p.StartsWith("/api/sandboxes/", StringComparison.Ordinal))
            return ApiScopes.Admin;
        if (!get && p.StartsWith("/api/runs/", StringComparison.Ordinal) && p.Contains("/approvals/", StringComparison.Ordinal)) return ApiScopes.Approve;
        return get ? ApiScopes.Read : ApiScopes.Run;
    }
}

public static class StepUp
{
    /// <summary>
    /// Firing a trigger only needs <c>run</c>, but from a browser session it also needs a recent passkey, as approvals and
    /// admin calls do (those get it by their scopes).
    /// </summary>
    public static bool Required(string method, PathString path) =>
        HttpMethods.IsPost(method) && path.Value is { } p && p.StartsWith("/api/triggers/", StringComparison.Ordinal) && p.TrimEnd('/').EndsWith("/fire", StringComparison.Ordinal);
}

/// <summary>A small per-address sliding window for the unauthenticated pairing endpoints.</summary>
public sealed class AddressLimiter(int limit, TimeSpan window, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool Allow(IPAddress? address)
    {
        string key = address?.ToString() ?? "?";
        DateTimeOffset now = _time.GetUtcNow();
        Queue<DateTimeOffset> q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

public static class Callers
{
    public const string Key = "harness.caller";

    /// <summary>The authenticated caller. Fails closed: a request that skipped authentication has none.</summary>
    public static Caller Caller(this HttpContext http) =>
        http.Items[Key] as Caller ?? throw new UnauthorizedAccessException("The request was not authenticated.");
}
