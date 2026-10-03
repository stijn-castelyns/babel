using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Harness.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Harness.Server.Auth;

public sealed record SetupRequest(string Code, string UserName, string Password);
public sealed record PasswordSignInRequest(string UserName, string Password, bool Remember = false);
public sealed record PasskeyOptionsRequest(string? UserName = null);
public sealed record PasskeyCredentialRequest(string Credential, string? Name = null);
public sealed record AuthStatusDto(bool SetupNeeded, bool SignedIn, string? User, string? Role, string? Method, bool SteppedUp, int Passkeys);
public sealed record PasskeyDto(string Id, string? Name, DateTimeOffset CreatedAt);

/// <summary>
/// Browser sign-in for the PWA: first-user setup, password and passkey sign-in, passkey registration and step-up.
/// Signing in with a passkey (or asserting one again while signed in) stamps the cookie with the step-up time that
/// approvals, trigger fires and admin calls need. Recovery (<c>/api/admin/*</c>) is local-socket only.
/// </summary>
internal static class IdentityEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder auth = app.MapGroup("/auth");

        auth.MapGet("/status", async (HttpContext http, UserManager<HarnessUser> users) =>
        {
            bool setup = !await users.Users.AnyAsync();
            if (IdentitySetup.CookieCaller(http.User, DateTimeOffset.UtcNow) is not { } c) return new AuthStatusDto(setup, false, null, null, null, false, 0);
            HarnessUser? user = await users.GetUserAsync(http.User);
            int passkeys = user is null ? 0 : (await users.GetPasskeysAsync(user)).Count;
            return new AuthStatusDto(setup, user is not null, c.Name, c.Role, http.User.FindFirst(HarnessClaims.Method)?.Value, c.SteppedUp, passkeys);
        });

        auth.MapPost("/setup", async (SetupRequest body, SetupCode code, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            if (await users.Users.AnyAsync() || !code.Matches(body.Code)) return Results.Json(new ErrorDto("Setup is closed or the code is wrong."), statusCode: 403);
            HarnessUser user = new() { UserName = body.UserName.Trim() };
            IdentityResult created = await users.CreateAsync(user, body.Password);
            if (!created.Succeeded) return Results.BadRequest(new ErrorDto(string.Join(" ", created.Errors.Select(e => e.Description))));
            await users.AddToRoleAsync(user, Roles.Owner);
            code.Clear();
            await signIn.SignInWithClaimsAsync(user, isPersistent: true, [new Claim(HarnessClaims.Method, "password")]);
            return Results.Ok();
        });

        auth.MapPost("/password", async (PasswordSignInRequest body, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            HarnessUser? user = await users.FindByNameAsync(body.UserName.Trim());
            // The same answer whether the user exists or not.
            if (user is null) return Results.Json(new ErrorDto("Wrong user name or password."), statusCode: 401);
            SignInResult result = await signIn.CheckPasswordSignInAsync(user, body.Password, lockoutOnFailure: true);
            if (result.IsLockedOut) return Results.Json(new ErrorDto("Too many attempts; try again in 15 minutes or sign in with a passkey."), statusCode: 429);
            if (!result.Succeeded) return Results.Json(new ErrorDto("Wrong user name or password."), statusCode: 401);
            await signIn.SignInWithClaimsAsync(user, body.Remember, [new Claim(HarnessClaims.Method, "password")]);
            return Results.Ok();
        });

        // Sign-in and step-up share one flow: request options, then the browser's assertion.
        auth.MapPost("/passkey/request-options", async (PasskeyOptionsRequest? body, HttpContext http, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            HarnessUser? user = http.User.Identity?.IsAuthenticated == true ? await users.GetUserAsync(http.User)
                : body?.UserName is { Length: > 0 } name ? await users.FindByNameAsync(name) : null;
            return Results.Content(await signIn.MakePasskeyRequestOptionsAsync(user), "application/json");
        });

        auth.MapPost("/passkey/signin", async (PasskeyCredentialRequest body, HttpContext http, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            PasskeyAssertionResult<HarnessUser> result = await signIn.PerformPasskeyAssertionAsync(body.Credential);
            if (!result.Succeeded) return Results.Json(new ErrorDto("The passkey was not accepted: " + result.Failure?.Message), statusCode: 401);
            HarnessUser user = result.User;
            // A step-up must come from the person already signed in, not swap the session to someone else.
            if (http.User.Identity?.IsAuthenticated == true && users.GetUserId(http.User) != user.Id)
                return Results.Json(new ErrorDto("That passkey belongs to another user."), statusCode: 403);
            if (await users.IsLockedOutAsync(user)) return Results.Json(new ErrorDto("This user is locked out."), statusCode: 403);
            await users.AddOrUpdatePasskeyAsync(user, result.Passkey);   // the new signature counter
            await users.ResetAccessFailedCountAsync(user);
            await signIn.SignInWithClaimsAsync(user, isPersistent: true,
            [
                new Claim(HarnessClaims.Method, "passkey"),
                new Claim(HarnessClaims.StepUp, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ]);
            return Results.Ok();
        });

        auth.MapPost("/passkey/creation-options", async (HttpContext http, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            if (await users.GetUserAsync(http.User) is not { } user) return Results.Unauthorized();
            if (!await CanAddPasskey(http, users, user)) return StepUpNeeded();
            PasskeyUserEntity entity = new() { Id = user.Id, Name = user.UserName!, DisplayName = user.UserName! };
            return Results.Content(await signIn.MakePasskeyCreationOptionsAsync(entity), "application/json");
        });

        auth.MapPost("/passkey/register", async (PasskeyCredentialRequest body, HttpContext http, UserManager<HarnessUser> users, SignInManager<HarnessUser> signIn) =>
        {
            if (await users.GetUserAsync(http.User) is not { } user) return Results.Unauthorized();
            if (!await CanAddPasskey(http, users, user)) return StepUpNeeded();
            PasskeyAttestationResult result = await signIn.PerformPasskeyAttestationAsync(body.Credential);
            if (!result.Succeeded) return Results.BadRequest(new ErrorDto("The passkey could not be registered: " + result.Failure?.Message));
            if (result.UserEntity.Id != user.Id) return Results.BadRequest(new ErrorDto("The passkey was created for another user."));
            result.Passkey.Name = string.IsNullOrWhiteSpace(body.Name) ? "passkey" : body.Name.Trim();
            IdentityResult saved = await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
            return saved.Succeeded ? Results.Ok() : Results.BadRequest(new ErrorDto(string.Join(" ", saved.Errors.Select(e => e.Description))));
        });

        auth.MapGet("/passkeys", async (HttpContext http, UserManager<HarnessUser> users) =>
            await users.GetUserAsync(http.User) is { } user
                ? Results.Ok((await users.GetPasskeysAsync(user)).Select(p => new PasskeyDto(Convert.ToBase64String(p.CredentialId), p.Name, p.CreatedAt)))
                : Results.Unauthorized());

        auth.MapDelete("/passkeys/{id}", async (string id, HttpContext http, UserManager<HarnessUser> users) =>
        {
            if (await users.GetUserAsync(http.User) is not { } user) return Results.Unauthorized();
            if (IdentitySetup.CookieCaller(http.User, DateTimeOffset.UtcNow) is not { SteppedUp: true }) return StepUpNeeded();
            IdentityResult removed = await users.RemovePasskeyAsync(user, Convert.FromBase64String(id));
            return removed.Succeeded ? Results.NoContent() : Results.NotFound(new ErrorDto("No such passkey."));
        });

        auth.MapPost("/signout", async (SignInManager<HarnessUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Ok();
        });

        // ---- recovery over the local socket ----

        RouteGroupBuilder admin = app.MapGroup("/api/admin");

        admin.MapGet("/users", async (UserManager<HarnessUser> users) =>
        {
            List<UserSummaryDto> list = [];
            foreach (HarnessUser u in await users.Users.OrderBy(u => u.UserName).ToListAsync())
                list.Add(new UserSummaryDto(u.UserName!, [.. await users.GetRolesAsync(u)], (await users.GetPasskeysAsync(u)).Count, await users.IsLockedOutAsync(u)));
            return list;
        });

        admin.MapPost("/users/{name}/reset-password", async (string name, UserManager<HarnessUser> users) =>
        {
            if (await users.FindByNameAsync(name) is not { } user) return Results.NotFound(new ErrorDto($"No user '{name}'."));
            string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(15)).Replace('+', 'k').Replace('/', 'q');
            await users.RemovePasswordAsync(user);
            IdentityResult set = await users.AddPasswordAsync(user, password);
            if (!set.Succeeded) return Results.BadRequest(new ErrorDto(string.Join(" ", set.Errors.Select(e => e.Description))));
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await users.UpdateSecurityStampAsync(user);   // signs out every existing session
            return Results.Ok(new TemporaryPasswordDto(user.UserName!, password));
        });

        admin.MapPost("/users/{name}/clear-passkeys", async (string name, UserManager<HarnessUser> users) =>
        {
            if (await users.FindByNameAsync(name) is not { } user) return Results.NotFound(new ErrorDto($"No user '{name}'."));
            foreach (UserPasskeyInfo p in await users.GetPasskeysAsync(user)) await users.RemovePasskeyAsync(user, p.CredentialId);
            await users.UpdateSecurityStampAsync(user);
            return Results.Ok();
        });

        admin.MapPost("/setup", async (UserManager<HarnessUser> users, SetupCode code) =>
            await users.Users.AnyAsync() ? Results.Conflict(new ErrorDto("A user exists already; setup is closed.")) : Results.Ok(new SetupCodeDto(code.Issue())));
    }

    /// <summary>The first passkey can be added from a password session; any further one needs a step-up with an existing passkey.</summary>
    private static async Task<bool> CanAddPasskey(HttpContext http, UserManager<HarnessUser> users, HarnessUser user) =>
        (await users.GetPasskeysAsync(user)).Count == 0 || IdentitySetup.CookieCaller(http.User, DateTimeOffset.UtcNow) is { SteppedUp: true };

    private static IResult StepUpNeeded() => Results.Json(new ErrorDto("step-up required: sign in with your passkey again"), statusCode: 403);
}
