using System.CommandLine;
using System.Text.Json;
using Harness.Client;
using Spectre.Console;

namespace Harness.Cli;

/// <summary>Tables in a terminal, JSON with <c>--json</c>, plain text when piped or with <c>--plain</c>.</summary>
internal static class Output
{
    public static bool Fancy(ParseResult p) =>
        !p.GetValue(CliContext.Plain) && !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(HarnessClient.Json) { WriteIndented = true }));

    public static void Table(ParseResult p, string[] headers, IEnumerable<string[]> rows)
    {
        List<string[]> data = [.. rows];
        if (Fancy(p))
        {
            Table table = new Table().Border(TableBorder.Simple);
            foreach (string h in headers) table.AddColumn(new TableColumn($"[bold]{Markup.Escape(h)}[/]"));
            foreach (string[] row in data) table.AddRow([.. row.Select(c => Markup.Escape(c))]);
            AnsiConsole.Write(table);
            return;
        }
        int[] widths = [.. headers.Select((h, i) => Math.Max(h.Length, data.Count == 0 ? 0 : data.Max(r => r[i].Length)))];
        Console.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (string[] row in data) Console.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is null) return "-";
        TimeSpan d = DateTimeOffset.UtcNow - at.Value;
        return d.TotalSeconds < 60 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m" : d.TotalHours < 48 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalDays}d";
    }

    public static string Elapsed(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null) return "-";
        TimeSpan d = (end ?? DateTimeOffset.UtcNow) - start.Value;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}m" : $"{(int)d.TotalMinutes}m{d.Seconds:00}s";
    }

    public static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1_000 ? $"{n / 1000}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Short(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";
}
