using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Harness.Client;
using Harness.Core;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Server;

/// <summary>
/// The daemon: one API on up to three listeners. The local Unix socket (mode 0600) needs no other authentication;
/// the API listener needs a bearer token; the webhook listener serves only <c>/hooks/*</c>.
/// </summary>
public static class HarnessServer
{
    public static WebApplication Build(HarnessPaths paths, Action<IServiceCollection>? configure = null)
    {
        paths.EnsureCreated();
        ConfigCatalog catalog = new(paths);
        ListenersConfig listeners = catalog.Config.Listeners;
        SecretStore secrets = new(paths);
        string socket = listeners.Socket is { Length: > 0 } s ? HarnessPaths.ExpandHome(s) : paths.Socket;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = paths.Home });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Agents.AI", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.AI", LogLevel.Warning);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = HarnessClient.Json.PropertyNamingPolicy;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddHarnessRuntime(paths);
        builder.Services.AddHarnessTriggers();
        builder.Services.AddSingleton(sp => new Auth.AuthStore(paths));
        Uri? apiUri = listeners.Api is { Length: > 0 } a ? new Uri(a) : null;
        string? publicHost = listeners.PublicHost ?? (apiUri?.Host is "127.0.0.1" or "::1" ? "localhost" : apiUri?.Host);
        Auth.IdentitySetup.AddHarnessIdentity(builder.Services, paths, publicHost, secureCookies: apiUri?.Scheme == "https");
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TriggerEngine>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Retention>());
        configure?.Invoke(builder.Services);

        if (File.Exists(socket)) File.Delete(socket);   // stale socket from a previous run
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenUnixSocket(socket, o => o.Tag(Listener.Socket));
            if (listeners.Api is { Length: > 0 } api)
            {
                Uri uri = new(api);
                k.Listen(System.Net.IPAddress.Parse(uri.Host == "localhost" ? "127.0.0.1" : uri.Host), uri.Port, o =>
                {
                    o.Tag(Listener.Api);
                    if (uri.Scheme == "https")
                    {
                        string cert = listeners.ApiCertificate ?? throw new ConfigException("An https API listener needs listeners.apiCertificate (a PFX file).");
                        o.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(HarnessPaths.ExpandHome(cert), secrets.Resolve(listeners.ApiCertificatePassword)));
                    }
                });
            }
            if (listeners.Webhooks is { Length: > 0 } hooks)
            {
                Uri uri = new(hooks);
                k.Listen(System.Net.IPAddress.Parse(uri.Host == "localhost" ? "127.0.0.1" : uri.Host), uri.Port, o => o.Tag(Listener.Webhooks));
            }
            k.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
        });

        WebApplication app = builder.Build();
        string? apiToken = secrets.Resolve(listeners.ApiToken);

        Auth.AuthStore auth = app.Services.GetRequiredService<Auth.AuthStore>();
        Auth.AddressLimiter pairStarts = new(10, TimeSpan.FromMinutes(10)), pairPolls = new(120, TimeSpan.FromMinutes(1)),
            signIns = new(20, TimeSpan.FromMinutes(10));
        string? setupCode = Auth.IdentitySetup.InitialiseAsync(app.Services).GetAwaiter().GetResult();

        app.UseAuthentication();

        // Listener separation, authentication and per-route scopes.
        app.Use(async (http, next) =>
        {
            string? kind = Listener.Kind(http);
            bool hooksPath = http.Request.Path.StartsWithSegments("/hooks");
            if (kind is null || (kind == Listener.Webhooks) != hooksPath)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (kind == Listener.Socket)
            {
                http.Items[Auth.Callers.Key] = Auth.Caller.Socket;
                await next();
                return;
            }
            if (hooksPath)
            {
                await next();
                return;
            }
            string required = Auth.AccessPolicy.Required(http.Request.Method, http.Request.Path);
            string path = http.Request.Path.Value ?? "";
            if (required == Auth.AccessPolicy.Anonymous)
            {
                Auth.AddressLimiter? limiter = path.StartsWith("/api/pair/token", StringComparison.Ordinal) ? pairPolls
                    : path.StartsWith("/api/pair", StringComparison.Ordinal) ? pairStarts
                    : path is "/auth/password" or "/auth/setup" or "/auth/passkey/signin" ? signIns : null;
                if (limiter?.Allow(http.Connection.RemoteIpAddress) == false)
                {
                    await Deny(http, StatusCodes.Status429TooManyRequests, "Too many requests; try again later.");
                    return;
                }
                if (path.StartsWith("/auth/", StringComparison.Ordinal) && !SameOrigin(http))
                {
                    await Deny(http, StatusCodes.Status403Forbidden, "Cross-origin request refused.");
                    return;
                }
                await next();
                return;
            }
            Auth.Caller? caller = TokenMatches(http, apiToken)
                ? new Auth.Caller("api-token", new HashSet<string>(ApiScopes.All), Local: false)
                : Bearer(http) is { } bearer ? auth.Validate(bearer)
                : Auth.IdentitySetup.CookieCaller(http.User, DateTimeOffset.UtcNow);
            if (caller is { Cookie: true } && !SameOrigin(http))
            {
                await Deny(http, StatusCodes.Status403Forbidden, "Cross-origin request refused.");
                return;
            }
            if (caller is null)
            {
                await Deny(http, StatusCodes.Status401Unauthorized, "Sign in, or send a bearer token ('harness login' pairs a device).");
                return;
            }
            if (required == Auth.AccessPolicy.LocalOnly)
            {
                await Deny(http, StatusCodes.Status403Forbidden, "This is only available over the daemon's local socket.");
                return;
            }
            bool stepUpMissing = caller.Cookie && !caller.SteppedUp
                && ((required is ApiScopes.Approve or ApiScopes.Admin && caller.Role is { } role && Auth.Roles.Scopes(role).Contains(required))
                    || Auth.StepUp.Required(http.Request.Method, http.Request.Path));
            if (stepUpMissing)
            {
                await Deny(http, StatusCodes.Status403Forbidden, "step-up required: sign in with your passkey again");
                return;
            }
            if (required != Auth.AccessPolicy.Authenticated && !caller.Has(required))
            {
                await Deny(http, StatusCodes.Status403Forbidden, caller.Cookie ? $"Your role cannot do this ('{required}')." : $"This token lacks the '{required}' scope.");
                return;
            }
            http.Items[Auth.Callers.Key] = caller;
            await next();
        });
        app.Use(async (http, next) =>
        {
            try { await next(); }
            catch (Exception ex) when (!http.Response.HasStarted && ex is not OperationCanceledException)
            {
                http.Response.StatusCode = ex switch
                {
                    KeyNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
                    UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
                    SessionBusyException => StatusCodes.Status409Conflict,
                    ConfigException or ArgumentException or InvalidOperationException => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                };
                await http.Response.WriteAsJsonAsync(new ErrorDto(ex.Message), HarnessClient.Json);
            }
        });

        ApiEndpoints.Map(app);
        Auth.AuthEndpoints.Map(app);
        Auth.IdentityEndpoints.Map(app);
        // The web app is not built yet; the page gives the browser an origin for sign-in and passkeys.
        app.MapGet("/", () => Results.Content("<!doctype html><meta charset=utf-8><title>harness</title><p>harness daemon. The web app is not built yet.</p>", "text/html"));
        app.Map("/hooks/{**path}", (string path, HttpContext http, TriggerEngine triggers) => triggers.HandleWebhookAsync(path, http));

        // Before any hosted service starts: the trigger engine replays queued events, and must see the runs they belonged to as failed.
        int failed = app.Services.GetRequiredService<HarnessDb>().FailInterruptedRuns();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (!OperatingSystem.IsWindows() && File.Exists(socket))
                File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Harness");
            log.LogInformation("harness daemon ready · home {Home} · socket {Socket}{Api}{Hooks}", paths.Home, socket,
                listeners.Api is null ? "" : $" · api {listeners.Api}", listeners.Webhooks is null ? "" : $" · webhooks {listeners.Webhooks}");
            if (failed > 0) log.LogWarning("{Count} run(s) were active when the daemon last stopped and are now marked failed", failed);
            int parked = app.Services.GetRequiredService<RunOrchestrator>().RehydrateParkedRuns();
            if (parked > 0) log.LogInformation("{Count} run(s) are still waiting for approval from before the restart", parked);
            if (setupCode is not null && listeners.Api is not null)
                log.LogWarning("No user yet. Create the owner at {Url}setup?code={Code} (or get a new code with 'harness admin setup')",
                    listeners.Api.EndsWith('/') ? listeners.Api : listeners.Api + "/", setupCode);
            if (listeners.Api is not null && auth.Tokens().All(t => t.RevokedAt is not null) && apiToken is null)
                log.LogInformation("The API listener accepts scoped tokens; pair a device with 'harness login {Api}' and approve it with 'harness pair approve'", listeners.Api);
        });
        app.Lifetime.ApplicationStopping.Register(() =>
            app.Services.GetRequiredService<RunOrchestrator>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)));
        app.Lifetime.ApplicationStopped.Register(() => { try { File.Delete(socket); } catch (IOException) { } });
        return app;
    }

    private static string? Bearer(HttpContext http)
    {
        string? header = http.Request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : null;
    }

    private static bool TokenMatches(HttpContext http, string? expected) =>
        expected is not null && Bearer(http) is { } given
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// Browser requests that change something must come from the daemon's own pages. SameSite=Strict cookies already stop
    /// most cross-site requests; this also stops a same-site but different-origin page. Requests without an Origin header
    /// (GETs, non-browser clients) pass.
    /// </summary>
    private static bool SameOrigin(HttpContext http)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) return true;
        string? origin = http.Request.Headers.Origin.FirstOrDefault();
        if (origin is null) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? o)
            && string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && o.Scheme == http.Request.Scheme;
    }

    private static Task Deny(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ErrorDto(message), HarnessClient.Json);
    }
}
