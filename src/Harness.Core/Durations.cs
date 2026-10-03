using System.Globalization;

namespace Harness.Core;

/// <summary>Durations such as <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>, or a <see cref="TimeSpan"/> string.</summary>
public static class Durations
{
    public static TimeSpan Parse(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            switch (text[^1])
            {
                case 's': return TimeSpan.FromSeconds(n);
                case 'm': return TimeSpan.FromMinutes(n);
                case 'h': return TimeSpan.FromHours(n);
                case 'd': return TimeSpan.FromDays(n);
            }
        return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
    }
}
