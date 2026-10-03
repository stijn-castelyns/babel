using System.Text.Json.Nodes;
using Harness.Client;
using Harness.Tui.State;

namespace Harness.Tests;

public class TuiRenderTests
{
    private static EventDto E(long seq, string type, JsonObject data, string run = "r_1", string session = "s_1") =>
        new(seq, DateTimeOffset.UnixEpoch.AddSeconds(seq), run, session, type, data);

    private static string Text(IEnumerable<Line> lines) => string.Join('\n', lines.Select(l => l.Text.TrimEnd()));

    [Fact]
    public void Assistant_markdown_keeps_every_line()
    {
        TranscriptModel t = new("s_1");
        t.Apply(E(1, "TEXT_MESSAGE_END", new JsonObject
        {
            ["messageId"] = "m1",
            ["text"] = "Done. The tool said: **exit 0**\n\nHere is `inline code` and a block:\n```bash\necho done\n```\n- one\n- two",
        }));
        IReadOnlyList<Line> lines = new TranscriptRenderer().Render(t, 60);
        string text = Text(lines);
        Assert.Contains("Done. The tool said: exit 0", text);
        Assert.Contains("Here is inline code and a block:", text);
        Assert.Contains("echo done", text);
        Assert.Contains("- two", text);
        Assert.Contains(lines.SelectMany(l => l.Spans), s => s.Text == "inline code" && s.Style == Style.Code);
    }

    [Fact]
    public void Streamed_text_is_replaced_by_the_complete_message()
    {
        TranscriptModel t = new("s_1");
        TranscriptRenderer r = new();
        string full = "Done. **x**\n\nHere is `code`:\n```\necho\n```\n- two";
        t.Apply(E(1, "TEXT_MESSAGE_START", new JsonObject { ["messageId"] = "m1" }));
        string[] words = full.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            t.Apply(E(0, "TEXT_MESSAGE_CONTENT", new JsonObject { ["messageId"] = "m1", ["delta"] = (i == 0 ? "" : " ") + words[i] }));
            r.Render(t, 50);
        }
        t.Apply(E(2, "TEXT_MESSAGE_END", new JsonObject { ["messageId"] = "m1", ["text"] = full }));
        string text = Text(r.Render(t, 50));
        Assert.Contains("- two", text);
        Assert.DoesNotContain("▍", text);
    }
}
