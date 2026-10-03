using System.Security.Cryptography;

namespace Harness.Core;

/// <summary>Short, time-ordered identifiers: a prefix, 9 base-36 characters of milliseconds and 6 random ones.</summary>
public static class Ids
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string NewSessionId() => New("s_");
    public static string NewRunId() => New("r_");
    public static string NewApprovalId() => New("a_");

    public static string New(string prefix)
    {
        Span<char> chars = stackalloc char[15];
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 8; i >= 0; i--) { chars[i] = Alphabet[(int)(ms % 36)]; ms /= 36; }
        for (int i = 9; i < 15; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(36)];
        return prefix + new string(chars);
    }
}
