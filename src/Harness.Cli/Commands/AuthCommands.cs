using System.CommandLine;
using Harness.Client;

namespace Harness.Cli.Commands;

/// <summary><c>harness login/logout</c> on a remote machine; <c>harness tokens</c> and <c>harness pair</c> on the daemon's own.</summary>
internal static class AuthCommands
{
    public static IEnumerable<Command> Create()
    {
        yield return Login();
        yield return Logout();
        yield return Tokens();
        yield return Pair();
    }

    private static Command Login()
    {
        Argument<string> url = new("url") { Description = "Daemon API URL, for example https://box.tailnet.ts.net:7443" };
        Option<string?> name = new("--name") { Description = "Name for this device (default: the machine name)" };
        Command login = new("login", "Pair this machine with a remote daemon: approve the code it shows on the daemon's machine.") { url, name };
        login.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            Uri target = new(p.GetValue(url)!.TrimEnd('/') + "/");
            using HarnessClient anonymous = HarnessClient.ForUrl(target, null);
            PairStartResponse start = await anonymous.StartPairingAsync(p.GetValue(name) ?? Environment.MachineName, ct);
            Console.WriteLine($"Pairing code: {start.UserCode}");
            Console.WriteLine($"On the daemon's machine run:  harness pair approve {start.UserCode} --scope read,run,approve");
            Console.WriteLine($"(or approve it in the web app). Waiting up to {start.ExpiresInSeconds / 60} minutes…");
            DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresInSeconds);
            while (DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, start.IntervalSeconds)), ct);
                PairPollResponse poll = await anonymous.PollPairingAsync(start.DeviceCode, ct);
                switch (poll.Status)
                {
                    case "pending": continue;
                    case "approved" when poll.Token is { } token:
                        new Credentials(CliContext.Paths(p)).Set(target, token, p.GetValue(name));
                        Console.WriteLine($"Logged in to {Credentials.Key(target)} with scopes {string.Join(", ", poll.Scopes ?? [])}.");
                        Console.WriteLine($"Use it with --remote {target.ToString().TrimEnd('/')} or HARNESS_URL.");
                        return 0;
                    default:
                        throw new CliException($"Pairing {poll.Status}.");
                }
            }
            throw new CliException("The pairing code expired before it was approved.");
        }));
        return login;
    }

    private static Command Logout()
    {
        Argument<string?> url = new("url") { Description = "Daemon URL (default: --remote or HARNESS_URL)", Arity = ArgumentArity.ZeroOrOne };
        Command logout = new("logout", "Revoke this machine's token on a remote daemon and forget it.") { url };
        logout.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            string raw = p.GetValue(url) ?? p.GetValue(CliContext.Remote) ?? Environment.GetEnvironmentVariable("HARNESS_URL")
                ?? throw new CliException("Which daemon? Pass its URL.");
            Uri target = new(raw.TrimEnd('/') + "/");
            Credentials store = new(CliContext.Paths(p));
            if (store.TokenFor(target) is not { } token) throw new CliException($"Not logged in to {Credentials.Key(target)}.");
            try
            {
                using HarnessClient c = HarnessClient.ForUrl(target, token);
                await c.RevokeSelfAsync(ct);
            }
            catch (Exception ex) when (ex is HarnessApiException or HttpRequestException)
            {
                Console.Error.WriteLine($"warning: could not revoke the token on the daemon ({ex.Message}); forgetting it here anyway.");
            }
            store.Remove(target);
            Console.WriteLine($"Logged out of {Credentials.Key(target)}.");
            return 0;
        }));
        return logout;
    }

    private static Command Tokens()
    {
        Command tokens = new("tokens", "Scoped API tokens (managed over the local socket only).");

        Command ls = new("ls", "List tokens.");
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<TokenDto> rows = await c.TokensAsync(ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["TOKEN", "NAME", "SCOPES", "CREATED", "LAST USED", "EXPIRES", "STATE"], rows.Select(t => new[]
            {
                t.Id, t.Name, string.Join(',', t.Scopes), Output.Ago(t.CreatedAt), Output.Ago(t.LastUsedAt),
                t.ExpiresAt?.ToString("yyyy-MM-dd") ?? "never",
                t.RevokedAt is not null ? "revoked" : t.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "active",
            }));
            return 0;
        }));
        tokens.Subcommands.Add(ls);

        Argument<string> name = new("name") { Description = "What the token is for, for example ci or phone-script" };
        Option<string> scope = new("--scope") { Description = "Comma-separated scopes: read, run, approve, admin", DefaultValueFactory = _ => "read" };
        Option<int?> days = new("--days") { Description = "Expire after this many days (default: never)" };
        Command create = new("create", "Create a token; it is printed once.") { name, scope, days };
        create.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            CreatedTokenDto t = await c.CreateTokenAsync(new CreateTokenRequest(p.GetValue(name)!, ApiScopes.Parse(p.GetValue(scope)), p.GetValue(days)), ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(t); return 0; }
            Console.Error.WriteLine($"{t.Info.Id} · {string.Join(',', t.Info.Scopes)} · store it now, it is not shown again:");
            Console.WriteLine(t.Token);
            return 0;
        }));
        tokens.Subcommands.Add(create);

        Argument<string> id = new("id") { Description = "Token id (t_…)" };
        Command revoke = new("revoke", "Revoke a token.") { id };
        revoke.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            await c.RevokeTokenAsync(p.GetValue(id)!, ct);
            Console.WriteLine("revoked");
            return 0;
        }));
        tokens.Subcommands.Add(revoke);
        return tokens;
    }

    private static Command Pair()
    {
        Command pair = new("pair", "Approve or deny devices that ran 'harness login' (over the local socket only).");

        Command ls = new("ls", "List pending pairing requests.");
        ls.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            IReadOnlyList<PairingDto> rows = await c.PairingsAsync(ct);
            if (p.GetValue(CliContext.Json)) { Output.Json(rows); return 0; }
            Output.Table(p, ["CODE", "DEVICE", "FROM", "REQUESTED", "EXPIRES"], rows.Select(r => new[]
            {
                r.UserCode, r.Name, r.Address ?? "-", Output.Ago(r.CreatedAt), $"in {(int)Math.Max(0, (r.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes)}m",
            }));
            return 0;
        }));
        pair.Action = ls.Action;
        pair.Subcommands.Add(ls);

        Argument<string> code = new("code") { Description = "The code the device shows, for example KMPT-7QXR" };
        Option<string> scope = new("--scope") { Description = "Comma-separated scopes: read, run, approve, admin", DefaultValueFactory = _ => "read,run" };
        Option<int?> days = new("--days") { Description = "Expire the device's token after this many days" };
        Command approve = new("approve", "Approve a pairing; the device receives a token with these scopes.") { code, scope, days };
        approve.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            await c.DecidePairingAsync(p.GetValue(code)!, new ApprovePairingRequest(true, ApiScopes.Parse(p.GetValue(scope)), p.GetValue(days)), ct);
            Console.WriteLine("approved");
            return 0;
        }));
        pair.Subcommands.Add(approve);

        Command deny = new("deny", "Deny a pairing.") { code };
        deny.SetAction((p, ct) => HarnessCli.Guard(async () =>
        {
            using HarnessClient c = CliContext.Connect(p);
            await c.DecidePairingAsync(p.GetValue(code)!, new ApprovePairingRequest(false), ct);
            Console.WriteLine("denied");
            return 0;
        }));
        pair.Subcommands.Add(deny);
        return pair;
    }
}
