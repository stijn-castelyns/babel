using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Harness.Client;
using Harness.Core;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Server.Auth;

public sealed class HarnessUser : IdentityUser;

/// <summary>ASP.NET Core Identity in <c>identity.db</c>, schema version 3 (passkeys).</summary>
public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : IdentityDbContext<HarnessUser>(options);

public static class Roles
{
    public const string Owner = "owner", Operator = "operator", Viewer = "viewer";
    public static readonly IReadOnlyList<string> All = [Owner, Operator, Viewer];

    /// <summary>What a role may do at most. Approve and admin also need a recent passkey step-up.</summary>
    public static IReadOnlyList<string> Scopes(string role) => role switch
    {
        Owner => ApiScopes.All,
        Operator => [ApiScopes.Read, ApiScopes.Run, ApiScopes.Approve],
        _ => [ApiScopes.Read],
    };
}

/// <summary>Claims the harness adds to the sign-in cookie.</summary>
public static class HarnessClaims
{
    /// <summary>How the session signed in: <c>passkey</c> or <c>password</c>.</summary>
    public const string Method = "harness:amr";
    /// <summary>Unix seconds of the last passkey assertion; approvals, trigger fires and admin calls need it to be recent.</summary>
    public const string StepUp = "harness:stepup";
    public static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The one-time setup code printed on first start. Holding it (only someone who can read the daemon's log or run
/// <c>harness admin setup</c> on the machine can) is what lets the first user be created; once a user exists it is refused.
/// </summary>
public sealed class SetupCode
{
    private byte[]? _hash;

    public string Issue()
    {
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return code;
    }

    public bool Matches(string? code) =>
        code is not null && _hash is { } hash && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    public void Clear() => _hash = null;
}

public static class IdentitySetup
{
    public static IServiceCollection AddHarnessIdentity(this IServiceCollection services, HarnessPaths paths, string? publicHost, bool secureCookies)
    {
        string file = Path.Combine(paths.Home, "identity.db");
        services.AddDbContext<IdentityDb>(o => o.UseSqlite($"Data Source={file}"));
        services.AddSingleton<SetupCode>();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddIdentityCore<HarnessUser>(o =>
            {
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireDigit = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<IdentityDb>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.Configure<IdentityPasskeyOptions>(o =>
        {
            if (publicHost is not null) o.ServerDomain = publicHost;
            o.UserVerificationRequirement = "required";
            o.ResidentKeyRequirement = "required";
        });
        // A password reset or cleared passkeys (new security stamp) ends existing sessions on their next request.
        services.Configure<SecurityStampValidatorOptions>(o =>
        {
            o.ValidationInterval = TimeSpan.Zero;
            // The validator rebuilds the principal from the store; keep how the session signed in and its step-up time.
            o.OnRefreshingPrincipal = context =>
            {
                if (context.NewPrincipal?.Identity is ClaimsIdentity fresh && context.CurrentPrincipal is { } current)
                    foreach (Claim claim in current.Claims.Where(c => c.Type is HarnessClaims.Method or HarnessClaims.StepUp))
                        if (!fresh.HasClaim(c => c.Type == claim.Type)) fresh.AddClaim(new Claim(claim.Type, claim.Value));
                return Task.CompletedTask;
            };
        });
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "harness";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromDays(14);
            // An API answers with status codes, never a redirect to a login page.
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        return services;
    }

    /// <summary>Creates the schema and the roles. Returns a setup code when there is no user yet.</summary>
    public static async Task<string?> InitialiseAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        IdentityDb db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        await db.Database.EnsureCreatedAsync();
        string file = db.Database.GetDbConnection().DataSource;
        if (!OperatingSystem.IsWindows() && File.Exists(file)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        RoleManager<IdentityRole> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (string role in Roles.All)
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));
        return await db.Users.AnyAsync() ? null : services.GetRequiredService<SetupCode>().Issue();
    }

    /// <summary>The caller a sign-in cookie stands for: the role's scopes, minus approve and admin without a recent step-up.</summary>
    public static Caller? CookieCaller(ClaimsPrincipal user, DateTimeOffset now)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        string role = Roles.All.FirstOrDefault(user.IsInRole) ?? Roles.Viewer;
        HashSet<string> scopes = [.. Roles.Scopes(role)];
        bool fresh = user.FindFirst(HarnessClaims.StepUp)?.Value is { } v
            && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)
            && now - DateTimeOffset.FromUnixTimeSeconds(at) < HarnessClaims.StepUpWindow;
        if (!fresh)
        {
            scopes.Remove(ApiScopes.Approve);
            scopes.Remove(ApiScopes.Admin);
        }
        return new Caller(user.Identity.Name ?? "user", scopes, Local: false) { Cookie = true, SteppedUp = fresh, Role = role };
    }
}
