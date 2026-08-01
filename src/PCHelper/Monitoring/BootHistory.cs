using PCHelper.Diagnostics;

namespace PCHelper.Monitoring;

public enum SessionEndKind
{
    /// <summary>Ordentlich heruntergefahren bzw. neu gestartet.</summary>
    Clean,
    /// <summary>Kein Herunterfahren protokolliert - der Rechner ist hart ausgefallen.</summary>
    Unexpected,
    /// <summary>Die laufende Sitzung.</summary>
    Running,
}

/// <summary>Eine Windows-Sitzung vom Hochfahren bis zum Herunterfahren.</summary>
public sealed class BootSession
{
    public required DateTime Start { get; init; }
    public DateTime? End { get; init; }
    public required SessionEndKind EndKind { get; init; }

    /// <summary>Grund bzw. ausloesendes Programm, sofern Windows es protokolliert hat.</summary>
    public string? Reason { get; init; }

    public TimeSpan? Duration => End is null ? null : End - Start;

    public string StartText => Start.ToString("dd.MM.yyyy HH:mm");
    public string EndText => End is null ? "laeuft noch" : End.Value.ToString("dd.MM.yyyy HH:mm");

    public string DurationText => Duration is null
        ? $"seit {(int)(DateTime.Now - Start).TotalHours} Std"
        : Duration.Value.TotalHours >= 1
            ? $"{(int)Duration.Value.TotalHours} Std {Duration.Value.Minutes} Min"
            : $"{(int)Duration.Value.TotalMinutes} Min";

    public string EndKindText => EndKind switch
    {
        SessionEndKind.Clean => "Neustart / Herunterfahren",
        SessionEndKind.Unexpected => "Hart ausgefallen",
        _ => "laeuft",
    };

    public bool IsUnexpected => EndKind == SessionEndKind.Unexpected;
}

/// <summary>
/// Rekonstruiert aus dem Windows-Ereignisprotokoll, wann der Rechner
/// hoch- und heruntergefahren wurde.
///
/// Das ist unabhaengig davon, ob PC Helper zu dem Zeitpunkt schon lief -
/// die Historie reicht also auch in die Zeit vor der Installation zurueck.
/// </summary>
public static class BootHistory
{
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    private const int OsStarted = 12;
    private const int OsShuttingDown = 13;

    /// <summary>Liefert die Sitzungen der letzten <paramref name="days"/> Tage, neueste zuerst.</summary>
    public static List<BootSession> Read(int days = 30, int maxSessions = 60)
    {
        var starts = EventLogService.Query("System", EventLogService.Xpath(KernelGeneral, days, OsStarted), 200)
            .Select(e => e.Time).ToList();
        var stops = EventLogService.Query("System", EventLogService.Xpath(KernelGeneral, days, OsShuttingDown), 200)
            .Select(e => e.Time).ToList();

        // Rueckfallebene fuer Systeme, auf denen Kernel-General nichts liefert.
        if (starts.Count == 0)
        {
            starts = EventLogService.Query("System", EventLogService.Xpath("EventLog", days, 6005), 200)
                .Select(e => e.Time).ToList();
            stops = EventLogService.Query("System", EventLogService.Xpath("EventLog", days, 6006), 200)
                .Select(e => e.Time).ToList();
        }

        // Ereignis 1074 nennt das Programm bzw. den Benutzer, der das Herunterfahren ausgeloest hat.
        var initiated = EventLogService.Query("System", EventLogService.Xpath("User32", days, 1074), 100);

        starts.Sort();
        stops.Sort();

        var sessions = new List<BootSession>();
        for (int i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var nextStart = i + 1 < starts.Count ? starts[i + 1] : (DateTime?)null;

            // Passendes Herunterfahren: nach dem Start, aber vor dem naechsten Start.
            DateTime? end = stops
                .Where(s => s > start && (nextStart is null || s < nextStart.Value))
                .Select(s => (DateTime?)s)
                .LastOrDefault();

            SessionEndKind kind;
            if (end is not null) kind = SessionEndKind.Clean;
            else if (nextStart is not null) kind = SessionEndKind.Unexpected;
            else kind = SessionEndKind.Running;

            // Ohne sauberes Ende gilt der naechste Start als Zeitpunkt des Ausfalls.
            if (kind == SessionEndKind.Unexpected) end = nextStart;

            string? reason = null;
            if (end is not null)
            {
                var match = initiated.FirstOrDefault(e =>
                    Math.Abs((e.Time - end.Value).TotalMinutes) <= 5);
                reason = match is null ? null : ExtractReason(match.Message);
            }

            sessions.Add(new BootSession
            {
                Start = start,
                End = end,
                EndKind = kind,
                Reason = reason,
            });
        }

        return sessions.OrderByDescending(s => s.Start).Take(maxSessions).ToList();
    }

    /// <summary>Kurze Kennzahlen ueber das Neustartverhalten.</summary>
    public static (int Total, int Unexpected, TimeSpan? MedianUptime) Summarize(IReadOnlyList<BootSession> sessions)
    {
        var completed = sessions.Where(s => s.Duration is not null).Select(s => s.Duration!.Value).OrderBy(d => d).ToList();
        TimeSpan? median = completed.Count == 0 ? null : completed[completed.Count / 2];
        return (sessions.Count, sessions.Count(s => s.IsUnexpected), median);
    }

    /// <summary>Kuerzt die oft sehr lange 1074-Meldung auf das Wesentliche.</summary>
    private static string ExtractReason(string message)
    {
        var text = message.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length > 160 ? text[..160] + " ..." : text;
    }
}
