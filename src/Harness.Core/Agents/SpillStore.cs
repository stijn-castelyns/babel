using System.Text;

namespace Harness.Core.Agents;

/// <summary>Writes oversized tool output to <c>.harness/spill/&lt;id&gt;.txt</c> in the workspace and returns a head-and-tail view.</summary>
public static class SpillStore
{
    public const int HeadChars = 6_000;
    public const int TailChars = 4_000;

    public static string SpillDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, ".harness", "spill");

    /// <summary>Returns <paramref name="text"/> unchanged when it fits, else spills it and returns head, tail and the file path.</summary>
    public static string Fit(string text, int threshold, string workspaceRoot, string label)
    {
        if (text.Length <= threshold) return text;
        string dir = SpillDirectory(workspaceRoot);
        Directory.CreateDirectory(dir);
        string id = Ids.New("spill_");
        string file = Path.Combine(dir, id + ".txt");
        File.WriteAllText(file, text);
        string rel = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
        int lines = text.AsSpan().Count('\n') + 1;
        return new StringBuilder()
            .Append($"{label}: output too large ({text.Length:N0} chars, {lines:N0} lines) · full output saved to {rel} · read it with offset/limit\n")
            .Append(text.AsSpan(0, HeadChars))
            .Append($"\n… [{text.Length - HeadChars - TailChars:N0} chars omitted] …\n")
            .Append(text.AsSpan(text.Length - TailChars))
            .ToString();
    }
}
