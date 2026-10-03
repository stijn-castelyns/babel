using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Harness.Client;
using Harness.Core;
using Microsoft.Data.Sqlite;

namespace Harness.Server.Auth;

/// <summary>Who made an API request and what it may do.</summary>
public sealed record Caller(string Name, IReadOnlySet<string> Scopes, bool Local, string? TokenId = null)
{
    public static readonly Caller Socket = new("local", new HashSet<string>(ApiScopes.All), Local: true);

    public bool Has(string scope) => Scopes.Contains(scope);
}

/// <summary>
/// API tokens and device pairings in <c>auth.db</c>. Tokens are <c>hst_&lt;id&gt;_&lt;secret&gt;</c>; only a SHA-256 of the
/// secret is stored. A pairing keeps a hash of its device code and mints its token when the approved device polls, so no
/// usable secret is ever at rest.
/// </summary>
public sealed class AuthStore
{
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private const string Prefix = "hst_";
    // No 0/O/1/I/L, so a code read aloud or off a phone is not mistyped.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public AuthStore(HarnessPaths paths, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        string file = paths.AuthDatabase;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        using SqliteConnection c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS tokens (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, scopes TEXT NOT NULL, secret_hash TEXT NOT NULL, origin TEXT,
                created_at TEXT NOT NULL, last_used_at TEXT, expires_at TEXT, revoked_at TEXT);
            CREATE TABLE IF NOT EXISTS pairings (
                user_code TEXT PRIMARY KEY, device_hash TEXT NOT NULL UNIQUE, name TEXT NOT NULL, address TEXT,
                created_at TEXT NOT NULL, expires_at TEXT NOT NULL, status TEXT NOT NULL, scopes TEXT, token_days INTEGER,
                token_id TEXT);
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private SqliteConnection Open()
    {
        SqliteConnection c = new(_connectionString);
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Random(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? When(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    // ---- tokens ----

    public CreatedTokenDto CreateToken(string name, IReadOnlyList<string> scopes, int? expiresInDays = null, string? origin = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A token needs a name.");
        foreach (string s in scopes) ApiScopes.Parse(s);
        string id = Core.Ids.New("t_");
        string secret = Random(32);
        DateTimeOffset now = Now;
        DateTimeOffset? expires = expiresInDays is int days ? now.AddDays(days) : null;
        using SqliteConnection c = Open();
        Exec(c, "INSERT INTO tokens (id, name, scopes, secret_hash, origin, created_at, expires_at) VALUES ($id, $name, $scopes, $hash, $origin, $created, $expires)",
            ("$id", id), ("$name", name.Trim()), ("$scopes", string.Join(',', scopes)), ("$hash", Hash(secret)), ("$origin", origin),
            ("$created", Iso(now)), ("$expires", expires is { } e ? Iso(e) : null));
        return new CreatedTokenDto(new TokenDto(id, name.Trim(), scopes, now, null, expires, null, origin), $"{Prefix}{id}_{secret}");
    }

    public IReadOnlyList<TokenDto> Tokens()
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, scopes, created_at, last_used_at, expires_at, revoked_at, origin FROM tokens ORDER BY created_at";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<TokenDto> tokens = [];
        while (r.Read())
            tokens.Add(new TokenDto(r.GetString(0), r.GetString(1), r.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries),
                When(r, 3)!.Value, When(r, 4), When(r, 5), When(r, 6), r.IsDBNull(7) ? null : r.GetString(7)));
        return tokens;
    }

    public bool Revoke(string id)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", Iso(Now));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>The caller a bearer token stands for, or null when it is unknown, wrong, revoked or expired.</summary>
    public Caller? Validate(string bearer)
    {
        if (!bearer.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        int split = bearer.IndexOf('_', Prefix.Length + 2);   // ids are t_xxxx: skip their own underscore
        if (split < 0) return null;
        string id = bearer[Prefix.Length..split], secret = bearer[(split + 1)..];
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, scopes, secret_hash, expires_at, revoked_at, last_used_at FROM tokens WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        byte[] expected = Encoding.ASCII.GetBytes(r.GetString(2)), actual = Encoding.ASCII.GetBytes(Hash(secret));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;
        DateTimeOffset now = Now;
        if (!r.IsDBNull(4) || (When(r, 3) is { } expires && expires <= now)) return null;
        Caller caller = new(r.GetString(0), new HashSet<string>(r.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries)), Local: false, id);
        // Record use, at most once a minute per token.
        if (When(r, 5) is not { } last || now - last > TimeSpan.FromMinutes(1))
        {
            r.Close();
            Exec(c, "UPDATE tokens SET last_used_at = $now WHERE id = $id", ("$now", Iso(now)), ("$id", id));
        }
        return caller;
    }

    // ---- device pairing ----

    public PairStartResponse StartPairing(string? name, string? address)
    {
        string device = Random(32);
        DateTimeOffset now = Now;
        using SqliteConnection c = Open();
        Exec(c, "DELETE FROM pairings WHERE expires_at < $cutoff", ("$cutoff", Iso(now.AddDays(-1))));
        for (int attempt = 0; ; attempt++)
        {
            string code = UserCode();
            try
            {
                Exec(c, "INSERT INTO pairings (user_code, device_hash, name, address, created_at, expires_at, status) VALUES ($code, $hash, $name, $address, $created, $expires, 'pending')",
                    ("$code", code), ("$hash", Hash(device)), ("$name", string.IsNullOrWhiteSpace(name) ? "device" : name.Trim()), ("$address", address),
                    ("$created", Iso(now)), ("$expires", Iso(now + PairingLifetime)));
                return new PairStartResponse(device, code, (int)PairingLifetime.TotalSeconds, 2);
            }
            catch (SqliteException) when (attempt < 5) { }   // a user-code collision: draw again
        }
    }

    private static string UserCode()
    {
        Span<char> chars = stackalloc char[9];
        for (int i = 0; i < 9; i++) chars[i] = i == 4 ? '-' : CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public static string NormaliseCode(string code)
    {
        string bare = new([.. code.ToUpperInvariant().Where(char.IsLetterOrDigit)]);
        return bare.Length == 8 ? bare[..4] + "-" + bare[4..] : bare;
    }

    public IReadOnlyList<PairingDto> Pairings(bool pendingOnly = true)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT user_code, name, address, created_at, expires_at, status FROM pairings" +
            (pendingOnly ? " WHERE status = 'pending' AND expires_at > $now" : "") + " ORDER BY created_at";
        cmd.Parameters.AddWithValue("$now", Iso(Now));
        using SqliteDataReader r = cmd.ExecuteReader();
        List<PairingDto> list = [];
        while (r.Read())
            list.Add(new PairingDto(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), When(r, 3)!.Value, When(r, 4)!.Value, r.GetString(5)));
        return list;
    }

    /// <summary>Approves or denies a pending pairing by its user code. False when there is no such pending, unexpired pairing.</summary>
    public bool DecidePairing(string userCode, bool approved, IReadOnlyList<string>? scopes, int? tokenDays)
    {
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE pairings SET status = $status, scopes = $scopes, token_days = $days WHERE user_code = $code AND status = 'pending' AND expires_at > $now";
        cmd.Parameters.AddWithValue("$status", approved ? "approved" : "denied");
        cmd.Parameters.AddWithValue("$scopes", string.Join(',', scopes ?? [ApiScopes.Read, ApiScopes.Run]));
        cmd.Parameters.AddWithValue("$days", (object?)tokenDays ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", NormaliseCode(userCode));
        cmd.Parameters.AddWithValue("$now", Iso(Now));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// The device's poll. An approved pairing mints its token here, exactly once (the row moves to <c>completed</c> in the
    /// same statement that claims it), so a replayed device code gets nothing.
    /// </summary>
    public PairPollResponse Poll(string deviceCode)
    {
        string hash = Hash(deviceCode);
        using SqliteConnection c = Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT user_code, name, expires_at, status, scopes, token_days FROM pairings WHERE device_hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        string code, name, status;
        DateTimeOffset expires;
        string? scopes;
        int? days;
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            if (!r.Read()) return new PairPollResponse("expired");
            (code, name, expires, status) = (r.GetString(0), r.GetString(1), When(r, 2)!.Value, r.GetString(3));
            scopes = r.IsDBNull(4) ? null : r.GetString(4);
            days = r.IsDBNull(5) ? null : r.GetInt32(5);
        }
        if (status == "pending") return new PairPollResponse(Now >= expires ? "expired" : "pending");
        if (status != "approved") return new PairPollResponse(status == "denied" ? "denied" : "expired");

        using SqliteCommand claim = c.CreateCommand();
        claim.CommandText = "UPDATE pairings SET status = 'completed' WHERE user_code = $code AND status = 'approved'";
        claim.Parameters.AddWithValue("$code", code);
        if (claim.ExecuteNonQuery() == 0) return new PairPollResponse("expired");
        IReadOnlyList<string> granted = ApiScopes.Parse(scopes);
        CreatedTokenDto token = CreateToken(name, granted, days, origin: $"pairing {code}");
        Exec(c, "UPDATE pairings SET token_id = $id WHERE user_code = $code", ("$id", token.Info.Id), ("$code", code));
        return new PairPollResponse("approved", token.Token, granted);
    }
}
