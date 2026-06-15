using System.Text.RegularExpressions;

namespace Ada.Core;

/// <summary>
/// Translates a natural-language schedule ("every weekday at 8", "every Friday at 5pm", "every 15
/// minutes") into a 5-field cron expression (spec §12.3). Returns null when it can't parse, so the
/// caller can ask a sharp question rather than guess. A raw cron string passes straight through.
/// </summary>
public static partial class NlCron
{
    public static string? ToCron(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (LooksLikeCron(t)) return t;

        var everyN = EveryMinutesRegex().Match(t);
        if (everyN.Success) return $"*/{everyN.Groups[1].Value} * * * *";
        if (t is "every hour" or "hourly") return "0 * * * *";

        var hour = ParseHour(t, out var minute);
        if (hour is null) return null;

        var dow = "*";
        if (t.Contains("weekday")) dow = "1-5";
        else if (t.Contains("weekend")) dow = "0,6";
        else { var day = ParseDay(t); if (day is not null) dow = day; }

        return $"{minute} {hour} * * {dow}";
    }

    private static bool LooksLikeCron(string t)
    {
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts.All(p => CronFieldRegex().IsMatch(p));
    }

    private static int? ParseHour(string t, out int minute)
    {
        minute = 0;
        if (t.Contains("noon")) return 12;
        if (t.Contains("midnight")) return 0;

        var m = AtTimeRegex().Match(t);
        if (!m.Success)
        {
            if (t.Contains("morning")) return 8;
            if (t.Contains("evening")) return 18;
            return null;
        }

        var hour = int.Parse(m.Groups[1].Value);
        if (m.Groups[2].Success) minute = int.Parse(m.Groups[2].Value);
        var ampm = m.Groups[3].Value;
        if (ampm == "pm" && hour < 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;
        return hour;
    }

    private static string? ParseDay(string t)
    {
        string[] days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
        for (var i = 0; i < days.Length; i++)
            if (t.Contains(days[i])) return i.ToString();
        return null;
    }

    [GeneratedRegex(@"every (\d+) minute")] private static partial Regex EveryMinutesRegex();
    [GeneratedRegex(@"at (\d{1,2})(?::(\d{2}))?\s*(am|pm)?")] private static partial Regex AtTimeRegex();
    [GeneratedRegex(@"^[\d*/,\-]+$")] private static partial Regex CronFieldRegex();
}
