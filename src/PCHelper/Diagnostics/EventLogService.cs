using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using PCHelper.Core;

namespace PCHelper.Diagnostics;

/// <summary>Ein aufbereiteter Eintrag aus dem Windows-Ereignisprotokoll.</summary>
public sealed record LogEvent(DateTime Time, int Id, string Provider, string Level, string Message)
{
    /// <summary>
    /// Benannte Datenfelder des Ereignisses (nur gefuellt, wenn beim Abruf
    /// ausdruecklich angefordert). Beispiel: BugcheckCode bei Kernel-Power 41.
    /// </summary>
    public IReadOnlyDictionary<string, string> Data { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? DataValue(string name)
        => Data.TryGetValue(name, out var v) ? v : null;

    public string Short
    {
        get
        {
            var m = Message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (m.Length > 220) m = m[..220] + " ...";
            return m;
        }
    }

    public override string ToString() => $"{Time:dd.MM.yyyy HH:mm:ss}  [{Provider} / ID {Id}]  {Short}";
}

/// <summary>Abfragen auf das Windows-Ereignisprotokoll (System/Anwendung).</summary>
public static class EventLogService
{
    private static readonly Regex NamedData =
        new("<Data Name=['\"](?<name>[^'\"]+)['\"]\\s*>(?<value>.*?)</Data>",
            RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Fuehrt eine XPath-Abfrage auf einem Protokoll aus.
    /// Fehler (fehlende Rechte, beschaedigtes Protokoll) fuehren zu einer leeren Liste.
    /// </summary>
    /// <param name="includeData">
    /// Zusaetzlich die benannten Datenfelder aus dem Ereignis-XML lesen.
    /// Kostet Rechenzeit, daher nur dort einschalten, wo die Werte gebraucht werden.
    /// </param>
    public static List<LogEvent> Query(string logName, string xpath, int max = 100, bool includeData = false)
    {
        var list = new List<LogEvent>();
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            for (int i = 0; i < max; i++)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); }
                catch (EventLogException) { break; }

                if (rec is null) break;
                using (rec)
                {
                    string message;
                    try { message = rec.FormatDescription() ?? DescribeFallback(rec); }
                    catch { message = DescribeFallback(rec); }

                    list.Add(new LogEvent(
                        rec.TimeCreated ?? DateTime.MinValue,
                        rec.Id,
                        rec.ProviderName ?? "?",
                        LevelName(rec.Level),
                        message.Trim())
                    {
                        Data = includeData ? ReadNamedData(rec) : EmptyData,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"Keine Berechtigung fuer Protokoll '{logName}'.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Ereignisprotokoll '{logName}' konnte nicht gelesen werden: {ex.Message}");
        }
        return list;
    }

    /// <summary>Baut eine XPath-Abfrage: Anbieter + optionale Ereignis-IDs + Zeitfenster in Tagen.</summary>
    public static string Xpath(string? provider, int days, params int[] ids)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(provider))
            conditions.Add($"Provider[@Name='{provider}']");

        if (ids.Length == 1)
            conditions.Add($"EventID={ids[0]}");
        else if (ids.Length > 1)
            conditions.Add("(" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")");

        if (days > 0)
        {
            long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
            // Hinweis: EventLogQuery erwartet rohes XPath, KEINE XML-Entitaeten.
            // "&lt;=" statt "<=" fuehrt zu "Die angegebene Abfrage ist ungueltig".
            conditions.Add($"TimeCreated[timediff(@SystemTime) <= {ms}]");
        }

        return conditions.Count == 0 ? "*" : $"*[System[{string.Join(" and ", conditions)}]]";
    }

    /// <summary>XPath fuer mehrere Anbieter gleichzeitig.</summary>
    public static string XpathProviders(IEnumerable<string> providers, int days, params int[] ids)
    {
        var provExpr = "(" + string.Join(" or ", providers.Select(p => $"Provider[@Name='{p}']")) + ")";
        var conditions = new List<string> { provExpr };

        if (ids.Length == 1) conditions.Add($"EventID={ids[0]}");
        else if (ids.Length > 1) conditions.Add("(" + string.Join(" or ", ids.Select(i => $"EventID={i}")) + ")");

        if (days > 0)
        {
            long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
            conditions.Add($"TimeCreated[timediff(@SystemTime) <= {ms}]");
        }

        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    /// <summary>Alle Fehler und kritischen Ereignisse eines Zeitraums (Level 1 und 2).</summary>
    public static List<LogEvent> CriticalAndErrors(string logName, int days, int max = 150)
    {
        long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        var xpath = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        return Query(logName, xpath, max);
    }

    /// <summary>Ereignisse in einem Zeitfenster um einen Zeitpunkt herum (fuer Vorfallberichte).</summary>
    public static List<LogEvent> Around(string logName, DateTime moment, TimeSpan window, int max = 80)
    {
        var from = moment - window;
        var to = moment + window;
        var xpath =
            $"*[System[TimeCreated[@SystemTime>='{from.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000Z}' " +
            $"and @SystemTime<='{to.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000Z}']]]";
        return Query(logName, xpath, max);
    }

    private static readonly Dictionary<string, string> EmptyData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Liest die benannten Datenfelder aus dem Ereignis-XML.</summary>
    private static IReadOnlyDictionary<string, string> ReadNamedData(EventRecord rec)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Match m in NamedData.Matches(rec.ToXml()))
                data[m.Groups["name"].Value] = m.Groups["value"].Value.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ereignisdaten (ID {rec.Id}) nicht lesbar: {ex.Message}");
        }
        return data;
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "Kritisch",
        2 => "Fehler",
        3 => "Warnung",
        4 => "Information",
        5 => "Ausfuehrlich",
        _ => "-"
    };

    private static string DescribeFallback(EventRecord rec)
    {
        var sb = new StringBuilder();
        sb.Append("(Beschreibung nicht verfuegbar)");
        try
        {
            if (rec.Properties.Count > 0)
            {
                sb.Append(" Daten: ");
                sb.Append(string.Join(", ", rec.Properties.Take(8).Select(p => p.Value?.ToString() ?? "")));
            }
        }
        catch { /* Eigenschaften nicht lesbar */ }
        return sb.ToString();
    }
}
