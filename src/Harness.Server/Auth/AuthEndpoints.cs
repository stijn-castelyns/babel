using Harness.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harness.Server.Auth;

/// <summary>Scoped tokens and device pairing. Who may call what is decided by <see cref="AccessPolicy"/> before these run.</summary>
/// <summary>The JSON a browser's <c>PushSubscription.toJSON()</c> produces.</summary>
public sealed record PushSubscriptionRequest(string Endpoint, PushKeys Keys);
public sealed record PushKeys(string P256dh, string Auth);
public sealed record PushUnsubscribeRequest(string Endpoint);

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/whoami", (HttpContext http) =>
        {
            Caller c = http.Caller();
            return new WhoAmIDto(c.Name, [.. ApiScopes.All.Where(c.Scopes.Contains)], c.Local, c.TokenId);
        });

        // ---- tokens (local socket only, except revoking your own) ----

        api.MapGet("/tokens", (AuthStore store) => store.Tokens());

        api.MapPost("/tokens", (CreateTokenRequest body, AuthStore store) =>
            Results.Ok(store.CreateToken(body.Name, body.Scopes is { Count: > 0 } s ? ApiScopes.Parse(string.Join(',', s)) : [ApiScopes.Read], body.ExpiresInDays, "cli")));

        api.MapDelete("/tokens/self", (HttpContext http, AuthStore store) =>
            http.Caller().TokenId is { } id && store.Revoke(id) ? Results.NoContent() : Results.BadRequest(new ErrorDto("This request did not use a revocable token.")));

        api.MapDelete("/tokens/{id}", (string id, AuthStore store) =>
            store.Revoke(id) ? Results.NoContent() : Results.NotFound(new ErrorDto($"No active token '{id}'.")));

        // ---- Web Push ----

        api.MapGet("/push/key", (Push.VapidKeys keys) => new { publicKey = keys.PublicKey });

        api.MapPost("/push/subscriptions", (PushSubscriptionRequest body, HttpContext http, AuthStore store) =>
        {
            store.AddPushSubscription(new Push.PushSubscription(body.Endpoint, body.Keys.P256dh, body.Keys.Auth), http.Caller().Name);
            return Results.Created("/api/push/subscriptions", null);
        });

        api.MapPost("/push/unsubscribe", (PushUnsubscribeRequest body, AuthStore store) =>
            store.RemovePushSubscription(body.Endpoint) ? Results.NoContent() : Results.NotFound(new ErrorDto("No such subscription.")));

        api.MapPost("/push/test", async (Push.PushSender sender, CancellationToken ct) =>
            Results.Ok(new { delivered = await sender.SendAsync(new { title = "harness", body = "Notifications work.", url = "/#/settings" }, false, ct) }));

        // ---- device pairing ----

        api.MapPost("/pair", (PairStartRequest? body, HttpContext http, AuthStore store) =>
            Results.Ok(store.StartPairing(body?.Name, http.Connection.RemoteIpAddress?.ToString())));

        api.MapPost("/pair/token", (PairPollRequest body, AuthStore store) => Results.Ok(store.Poll(body.DeviceCode)));

        api.MapGet("/pairings", (AuthStore store) => store.Pairings());

        api.MapPost("/pairings/{code}", (string code, ApprovePairingRequest body, AuthStore store) =>
        {
            IReadOnlyList<string>? scopes = body.Scopes is { Count: > 0 } s ? ApiScopes.Parse(string.Join(',', s)) : null;
            return store.DecidePairing(code, body.Approved, scopes, body.ExpiresInDays)
                ? Results.Ok()
                : Results.NotFound(new ErrorDto($"No pending pairing with code '{AuthStore.NormaliseCode(code)}' (it may have expired)."));
        });
    }
}
