using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PCHelper.Monitoring;

namespace PCHelper.Diagnostics.Checks;

/// <summary>
/// Wertet den Neustart-Verlauf aus.
///
/// Bei einem Schwarzbild, das sich nur durch einen Neustart beheben laesst,
/// ist jeder Neustart selbst ein Fehlermarker. Haeufige Neustarts sind damit
/// ein direktes Mass fuer die Haeufigkeit des Problems.
/// </summary>
public sealed class RestartHistoryCheck : ICheck
{
    public string Name => "Neustart-Verlauf";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } =
        new[] { Cause.PowerSupply, Cause.OperatingSystem, Cause.Thermal, Cause.Memory };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var sessions = BootHistory.Read(ctx.LookbackDays);
        var (total, unexpected, median) = BootHistory.Summarize(sessions);

        var confirmed = new IncidentStore().LoadAll(200)
            .Where(i => i.Kind == IncidentKind.BlackscreenRestart &&
                        (DateTime.Now - i.Time).TotalDays <= ctx.LookbackDays)
            .ToList();

        if (total == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "restart-history", Category = Category, Title = "Neustart-Verlauf nicht auswertbar",
                    Severity = Severity.Info,
                    Summary = "Im Ereignisprotokoll wurden keine Startvorgaenge gefunden.",
                    Detail = "Geprueft wurden die Ereignisse 12/13 von Microsoft-Windows-Kernel-General " +
                             "sowie 6005/6006 der Quelle EventLog.",
                }
            });
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{total} Startvorgaenge in den letzten {ctx.LookbackDays} Tagen " +
                      $"(rund {total / (double)ctx.LookbackDays:0.#} pro Tag).");
        sb.AppendLine($"Davon ohne ordentliches Herunterfahren: {unexpected}");
        if (median is not null)
            sb.AppendLine($"Typische Sitzungsdauer: {MonitorService.FormatUptime(median.Value)}");
        if (confirmed.Count > 0)
            sb.AppendLine($"Als Schwarzbild bestaetigte Neustarts: {confirmed.Count}");

        sb.AppendLine();
        sb.AppendLine("Verlauf (neueste zuerst):");
        foreach (var s in sessions.Take(25))
            sb.AppendLine($"  {s.StartText}  ->  {s.EndText}   ({s.DurationText}, {s.EndKindText})" +
                          (s.Reason is null ? "" : $"\n      Ausgeloest durch: {s.Reason}"));

        sb.AppendLine();
        sb.AppendLine("Das fortlaufende Sitzungsprotokoll liegt unter:");
        sb.AppendLine("  " + SessionLog.Path);

        double perDay = total / (double)ctx.LookbackDays;

        Severity severity;
        string title, summary, recommendation;

        if (confirmed.Count > 0)
        {
            severity = Severity.Critical;
            title = "Neustarts wegen Schwarzbild bestaetigt";
            summary = $"{confirmed.Count} Neustart(s) waren noetig, um ein Schwarzbild zu beheben - " +
                      $"zuletzt am {confirmed.Max(c => c.Time):dd.MM.yyyy HH:mm}.";
            recommendation =
                "Diese Zeitpunkte sind die belastbarste Spur, die es zu diesem Fehlerbild gibt.\n\n" +
                "Im Bericht stehen zu jedem dieser Neustarts die Messwerte der zehn Minuten davor sowie " +
                "die Eintraege aus dem Ereignisprotokoll rund um den Zeitpunkt. Dort ist zu pruefen, ob " +
                "kurz zuvor die Anzeigekonfiguration wechselte (dann: Signalstrecke) oder ein " +
                "Grafiktreiber-Ereignis auftrat (dann: Treiber).";
        }
        else if (unexpected > 0)
        {
            severity = Severity.Warning;
            title = "Sitzungen ohne ordentliches Herunterfahren";
            summary = $"{unexpected} von {total} Sitzungen endeten ohne protokolliertes Herunterfahren.";
            recommendation =
                "Das passiert, wenn der Rechner ueber den Netzschalter ausgeschaltet oder zurueckgesetzt wird - " +
                "also genau dann, wenn bei einem Schwarzbild nichts anderes mehr geht.\n\n" +
                "Wenn das zutrifft: Beim naechsten Mal die Rueckfrage im Bereich 'Dauerueberwachung' mit " +
                "'Ja, Schwarzbild' beantworten. Dann wird der Zeitpunkt fest mit den Messwerten verknuepft.";
        }
        else if (perDay >= 2.0)
        {
            severity = Severity.Warning;
            title = "Auffaellig haeufige Neustarts";
            summary = $"Im Schnitt {perDay:0.#} Startvorgaenge pro Tag.";
            recommendation =
                "Haeufige Neustarts sind bei diesem Fehlerbild ein Mass fuer die Haeufigkeit des Problems. " +
                "Wenn ein Teil davon noetig war, um ein Schwarzbild loszuwerden, sollte das ueber die " +
                "Rueckfrage im Bereich 'Dauerueberwachung' bestaetigt werden.";
        }
        else
        {
            severity = Severity.Ok;
            title = "Neustart-Verhalten unauffaellig";
            summary = $"{total} Startvorgaenge, alle sauber beendet.";
            recommendation = "";
        }

        var f = new Finding
        {
            Id = "restart-history",
            Category = Category,
            Title = title,
            Severity = severity,
            Summary = summary,
            Detail = sb.ToString().TrimEnd(),
            Occurrences = total,
            LastOccurrence = sessions.FirstOrDefault()?.Start,
            Recommendation = string.IsNullOrEmpty(recommendation) ? null : recommendation,
            Causes = severity switch
            {
                Severity.Critical => new Dictionary<Cause, double>
                {
                    [Cause.DisplayLink] = 0.5, [Cause.GpuDriver] = 0.5,
                },
                Severity.Warning => new Dictionary<Cause, double>
                {
                    [Cause.DisplayLink] = 0.3, [Cause.GpuDriver] = 0.3, [Cause.PowerSupply] = 0.2,
                },
                _ => new Dictionary<Cause, double>(),
            },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>
/// Unerwartete Neustarts und Abschaltungen (Kernel-Power 41 / EventLog 6008).
///
/// Wichtig ist die Aufschluesselung: Ereignis 41 sagt in seinen Datenfeldern,
/// ob ein Bluescreen vorausging (BugcheckCode) und ob der Netzschalter gedrueckt
/// wurde (PowerButtonTimestamp). Ohne diese Unterscheidung wirkt die reine
/// Gesamtzahl viel dramatischer, als sie ist.
/// </summary>
public sealed class UnexpectedShutdownCheck : ICheck
{
    public string Name => "Unerwartete Neustarts";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.PowerSupply, Cause.Thermal, Cause.Memory };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var kernel41 = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-Kernel-Power", ctx.LookbackDays, 41), 200, includeData: true);

        var dirty6008 = EventLogService.Query("System",
            EventLogService.Xpath("EventLog", ctx.LookbackDays, 6008), 100);

        var total = kernel41.Count + dirty6008.Count;

        if (total == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "unexpected-shutdown", Category = Category,
                    Title = "Keine unerwarteten Abschaltungen",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen wurde der Rechner immer sauber heruntergefahren.",
                    Detail = "Das ist ein wichtiger Hinweis: Wenn der Rechner nie hart ausgegangen ist, scheidet ein " +
                             "Zusammenbruch der Stromversorgung als Ursache weitgehend aus. Das Problem liegt dann " +
                             "eher bei Bildsignal oder Grafiktreiber.",
                }
            });
        }

        // Aufschluesselung der Ereignisse 41 anhand ihrer Datenfelder.
        var withBugcheck = new List<LogEvent>();
        var byPowerButton = new List<LogEvent>();
        var silent = new List<LogEvent>();

        foreach (var e in kernel41)
        {
            var code = BugCheckCodes.TryParseFromData(e.DataValue("BugcheckCode")) ?? 0;
            var buttonPressed = ParseNonZero(e.DataValue("PowerButtonTimestamp"))
                                || e.DataValue("LongPowerButtonPressDetected") is "1" or "true";

            if (code != 0) withBugcheck.Add(e);
            else if (buttonPressed) byPowerButton.Add(e);
            else silent.Add(e);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{kernel41.Count} Ereignisse 'Kernel-Power 41' in den letzten {ctx.LookbackDays} Tagen.");
        sb.AppendLine("Aufschluesselung nach den Datenfeldern des Ereignisses:");
        sb.AppendLine($"  {withBugcheck.Count,4}x  mit vorausgegangenem Bluescreen (BugcheckCode gesetzt)");
        sb.AppendLine($"  {byPowerButton.Count,4}x  Netzschalter wurde gedrueckt (PowerButtonTimestamp gesetzt)");
        sb.AppendLine($"  {silent.Count,4}x  ohne Vorwarnung - Einfrieren oder Stromverlust");
        if (dirty6008.Count > 0)
            sb.AppendLine($"  {dirty6008.Count,4}x  zusaetzlich Ereignis 6008 (unerwartetes Herunterfahren)");

        sb.AppendLine();
        sb.AppendLine("Wichtig: Jeder Bluescreen erzeugt zusaetzlich ein Ereignis 41. Die Bluescreens aus dem " +
                      "Befund 'Bluescreens vorhanden' stecken in dieser Zahl also bereits mit drin und sind " +
                      "keine zusaetzlichen Vorfaelle.");
        sb.AppendLine();
        sb.AppendLine("Zeitpunkte (neueste zuerst):");
        foreach (var e in kernel41.Take(25))
        {
            var code = BugCheckCodes.TryParseFromData(e.DataValue("BugcheckCode")) ?? 0;
            var art = code != 0
                ? "Bluescreen " + BugCheckCodes.Describe(code).CodeText
                : ParseNonZero(e.DataValue("PowerButtonTimestamp")) ? "Netzschalter" : "ohne Vorwarnung";
            sb.AppendLine($"  {e.Time:dd.MM.yyyy HH:mm:ss}   {art}");
        }

        var last = kernel41.Concat(dirty6008).Max(e => e.Time);

        // Die Bewertung richtet sich danach, was wirklich passiert ist.
        Severity severity;
        string summary, recommendation;
        Dictionary<Cause, double> causes;

        if (silent.Count > 0)
        {
            severity = Severity.Critical;
            summary = $"{kernel41.Count} unerwartete Abschaltungen, davon {silent.Count} ohne jede Vorwarnung " +
                      $"(kein Bluescreen, kein Netzschalter) - zuletzt am {last:dd.MM.yyyy HH:mm}.";
            recommendation =
                "Abschaltungen ohne Vorwarnung sind das ernsteste Muster: Der Rechner geht schlagartig aus oder " +
                "friert komplett ein. Typische Ursachen in dieser Reihenfolge:\n\n" +
                "1) Stromversorgung. Alle PCIe-Stromkabel der Grafikkarte fest einstecken, getrennte Kabelstraenge " +
                "statt Daisy-Chain verwenden, den 12V-2x6-Stecker auf vollstaendiges Einrasten pruefen. " +
                "Steckdosenleiste testweise umgehen.\n" +
                "2) Speicher- bzw. CPU-Instabilitaet. EXPO/XMP im BIOS deaktivieren und beobachten.\n" +
                "3) Ueberhitzung. Die Dauerueberwachung dieser App zeigt, ob es kurz davor heiss wurde.\n\n" +
                "Wichtig zur Unterscheidung: Lief waehrend des Ausfalls der Ton weiter (Musik, Discord-Gespraech), " +
                "war der Rechner nicht stromlos, sondern nur die Grafikkarte haengte. Erst der erzwungene Neustart " +
                "erzeugt dann dieses Ereignis. Das ist ein GPU-Haenger und kein Netzteilproblem - siehe den Befund " +
                "'Muster eines GPU-Haengers'.";
            causes = new Dictionary<Cause, double>
            {
                [Cause.PowerSupply] = 0.6, [Cause.Memory] = 0.3, [Cause.Thermal] = 0.2, [Cause.GpuHardware] = 0.2,
            };
        }
        else if (byPowerButton.Count > 0)
        {
            severity = Severity.Warning;
            summary = $"{kernel41.Count} unerwartete Abschaltungen, davon {byPowerButton.Count} durch Druecken " +
                      $"des Netzschalters - zuletzt am {last:dd.MM.yyyy HH:mm}.";
            recommendation =
                "Diese Abschaltungen hat jemand selbst ausgeloest: Der Netzschalter wurde gedrueckt, weil sich das " +
                "System nicht mehr bedienen liess. Genau das passiert bei einem Schwarzbild.\n\n" +
                "Das heisst: Die Stromversorgung ist hier NICHT der Verdaechtige - der Rechner ist nicht von allein " +
                "ausgegangen. Die Ursache liegt davor, im Schwarzbild selbst. Weiter bei Grafiktreiber und " +
                "Monitorverbindung suchen.\n\n" +
                "Die Anzahl ist zugleich ein gutes Mass fuer die Haeufigkeit des Problems.";
            causes = new Dictionary<Cause, double> { [Cause.GpuDriver] = 0.4, [Cause.DisplayLink] = 0.4 };
        }
        else
        {
            severity = Severity.Warning;
            summary = $"{kernel41.Count} unerwartete Abschaltungen, alle im Zusammenhang mit einem Bluescreen - " +
                      $"zuletzt am {last:dd.MM.yyyy HH:mm}.";
            recommendation =
                "Alle diese Ereignisse gehoeren zu Bluescreens und sind daher keine eigenstaendigen Vorfaelle. " +
                "Massgeblich ist der Befund 'Bluescreens vorhanden' mit den dortigen Stoppcodes.";
            causes = new Dictionary<Cause, double> { [Cause.Memory] = 0.2 };
        }

        var f = new Finding
        {
            Id = "unexpected-shutdown",
            Category = Category,
            Title = "Rechner ist unerwartet ausgegangen bzw. neu gestartet",
            Severity = severity,
            Summary = summary,
            Detail = sb.ToString().TrimEnd(),
            Occurrences = total,
            LastOccurrence = last,
            Recommendation = recommendation,
            Causes = causes,
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }

    private static bool ParseNonZero(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && long.TryParse(value, out var v)
           && v != 0;
}

/// <summary>Bluescreens und zugehoerige Absturzabbilder.</summary>
public sealed class BugCheckCheck : ICheck
{
    public string Name => "Bluescreens";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } =
        new[] { Cause.Memory, Cause.DeviceDriver, Cause.OperatingSystem, Cause.GpuDriver, Cause.Storage };

    /// <summary>Ein einzelner Absturz mit Zeitpunkt, Stoppcode und den vier Parametern (sofern ermittelbar).</summary>
    private sealed record Crash(DateTime Time, uint? Code, string Source, ulong[]? Params = null);

    /// <summary>Anbieter der Bugcheck-Ereignisse: Der Klartextname und der Quellname, unter dem sie im Protokoll erscheinen.</summary>
    private static readonly string[] BugcheckProviders = { "Microsoft-Windows-WER-SystemErrorReporting", "BugCheck" };

    private static readonly Regex DriverName = new(@"[\w\-\.]+\.sys", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        // 1001 = Bugcheck mit Stoppcode. 1019 nennt zusaetzlich den "moeglicherweise verknuepften Treiber".
        var logged = EventLogService.Query("System",
            EventLogService.XpathProviders(BugcheckProviders, ctx.LookbackDays, 1001, 1019),
            200, includeData: true);
        var bugchecks = logged.Where(e => e.Id == 1001).ToList();

        var drivers = logged
            .Where(e => e.Id == 1019 || e.Id == 1001)
            .SelectMany(e => DriverName.Matches(e.Message).Select(m => m.Value.ToLowerInvariant()))
            .GroupBy(d => d)
            .OrderByDescending(g => g.Count())
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();

        var dumps = ListDumps(@"C:\Windows\Minidump", "*.dmp");

        if (bugchecks.Count == 0 && dumps.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "bugcheck", Category = Category, Title = "Keine Bluescreens",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen wurde kein Bluescreen protokolliert.",
                }
            });
        }

        var crashes = CollectCrashes(bugchecks, ctx.LookbackDays);
        var decoded = crashes.Where(c => c.Code is not null).ToList();

        // Nach Stoppcode gruppieren - das ist die eigentliche Auswertung.
        var groups = decoded
            .GroupBy(c => c.Code!.Value)
            .Select(g => new
            {
                Info = BugCheckCodes.Describe(g.Key),
                Count = g.Count(),
                Last = g.Max(c => c.Time),
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{crashes.Count} Bluescreens in den letzten {ctx.LookbackDays} Tagen.");
        sb.AppendLine();

        if (groups.Count > 0)
        {
            sb.AppendLine("Nach Stoppcode aufgeschluesselt:");
            sb.AppendLine();
            foreach (var g in groups)
            {
                sb.AppendLine($"  {g.Count}x  {g.Info.Display}");
                sb.AppendLine($"        {g.Info.Meaning}");
                sb.AppendLine($"        Zuletzt am {g.Last:dd.MM.yyyy HH:mm}");
                sb.AppendLine($"        Vorgehen: {g.Info.Advice}");
                sb.AppendLine();
            }
        }

        var undecoded = crashes.Count - decoded.Count;
        if (undecoded > 0)
            sb.AppendLine($"Bei {undecoded} Abstuerzen liess sich kein Stoppcode aus dem Ereignis lesen.\n");

        if (drivers.Count > 0)
        {
            sb.AppendLine("Vom Bugcheck genannte Treiber (Ereignis 1019 bzw. 1001):");
            foreach (var (name, count) in drivers)
                sb.AppendLine($"  {count}x  {name}");
            sb.AppendLine();
        }

        sb.AppendLine("Einzelne Zeitpunkte (neueste zuerst):");
        foreach (var c in crashes.OrderByDescending(c => c.Time).Take(25))
        {
            sb.AppendLine($"  {c.Time:dd.MM.yyyy HH:mm:ss}   " +
                          (c.Code is null ? "Stoppcode unbekannt" : BugCheckCodes.Describe(c.Code.Value).Display));

            var param = c.Code is { } code ? BugCheckCodes.DescribeGpuHangParameters(code, c.Params) : null;
            if (param is not null) sb.AppendLine($"        {param}");
        }

        if (dumps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{dumps.Count} Absturzabbilder unter C:\\Windows\\Minidump:");
            foreach (var d in dumps.Take(15))
                sb.AppendLine($"  {d.LastWriteTime:dd.MM.yyyy HH:mm}  {d.Name}  ({d.Length / 1024} KB)");
        }

        // Ursachengewichte aus den tatsaechlich aufgetretenen Stoppcodes ableiten,
        // gewichtet nach ihrer Haeufigkeit.
        var causes = new Dictionary<Cause, double>();
        if (decoded.Count > 0)
        {
            foreach (var g in groups)
            {
                double share = g.Count / (double)decoded.Count;
                foreach (var (cause, weight) in g.Info.Causes)
                    causes[cause] = causes.GetValueOrDefault(cause) + weight * share;
            }
        }
        else
        {
            causes[Cause.Memory] = 0.4;
            causes[Cause.GpuDriver] = 0.3;
            causes[Cause.Software] = 0.2;
        }

        var top = groups.FirstOrDefault();
        var mixed = groups.Count >= 3;

        var recommendation = new StringBuilder();
        if (top is not null)
        {
            recommendation.AppendLine($"Haeufigster Stoppcode: {top.Info.Display} ({top.Count} von {decoded.Count}).");
            recommendation.AppendLine($"{top.Info.Meaning}");
            recommendation.AppendLine();
            recommendation.AppendLine(top.Info.Advice);
        }

        if (drivers.Count > 0 && top is not null && BugCheckCodes.IsGpuHang(top.Info.Code))
        {
            recommendation.AppendLine();
            recommendation.AppendLine(
                $"Der Bugcheck nennt den Treiber {drivers[0].Name}. Bei einem Grafiktreiber heisst das nur, dass er " +
                "der Karte beim Haengen zugesehen hat - es beweist keinen Treiberfehler. Haengt die Karte bei jedem " +
                "Belastungstest gleich schnell, spricht das eher fuer die Hardware (Karte, PCIe-Anbindung, Stromanschluss).");
        }

        if (mixed)
        {
            recommendation.AppendLine();
            recommendation.AppendLine(
                $"Wichtig: Es traten {groups.Count} verschiedene Stoppcodes auf. Wahllos wechselnde Stoppcodes " +
                "sprechen fast immer fuer instabile Hardware und nicht fuer einen einzelnen defekten Treiber. " +
                "In dieser Reihenfolge vorgehen: EXPO/XMP im BIOS deaktivieren, BIOS aktualisieren, " +
                "anschliessend MemTest86 ueber mehrere Durchlaeufe.");
        }

        recommendation.AppendLine();
        recommendation.Append(
            "Bei einem neuen Rechner mit Herstellergarantie: Diese Auswertung zusammen mit dem Bericht " +
            "dem Haendler vorlegen. Sie ist als Mangelnachweis belastbar.");

        var f = new Finding
        {
            Id = "bugcheck",
            Category = Category,
            Title = top is null
                ? "Bluescreens vorhanden"
                : $"Bluescreens vorhanden - haeufigster Stoppcode {top.Info.Name}",
            Severity = Severity.Critical,
            Summary = decoded.Count > 0
                ? $"{crashes.Count} Bluescreens in den letzten {ctx.LookbackDays} Tagen, " +
                  $"{groups.Count} verschiedene Stoppcode(s), haeufigster: {top!.Info.Name} ({top.Count}x)."
                : $"{Math.Max(crashes.Count, dumps.Count)} Bluescreens bzw. Absturzabbilder in den letzten {ctx.LookbackDays} Tagen.",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = Math.Max(crashes.Count, dumps.Count),
            LastOccurrence = crashes.Count > 0 ? crashes.Max(c => c.Time) : dumps.Max(d => d.LastWriteTime),
            Recommendation = recommendation.ToString().Trim(),
            Causes = causes,
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }

    /// <summary>
    /// Sammelt die Abstuerze aus Ereignis 1001 und ergaenzt sie um Bluescreens,
    /// die nur ueber Kernel-Power 41 belegt sind (kommt vor, wenn Windows den
    /// Fehlerbericht nicht mehr schreiben konnte).
    /// </summary>
    private static List<Crash> CollectCrashes(IReadOnlyList<LogEvent> bugchecks, int days)
    {
        var crashes = bugchecks
            .Select(e => new Crash(
                e.Time,
                BugCheckCodes.TryParseFromMessage(e.Message)
                    ?? BugCheckCodes.TryParseFromData(e.DataValue("param1")),
                "Ereignis 1001",
                BugCheckCodes.TryParseParameters(e.Message)))
            .ToList();

        var kernel41 = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-Kernel-Power", days, 41), 200, includeData: true);

        foreach (var e in kernel41)
        {
            var code = BugCheckCodes.TryParseFromData(e.DataValue("BugcheckCode"));
            if (code is null or 0) continue;

            // Derselbe Absturz taucht in beiden Protokollen auf - nicht doppelt zaehlen.
            bool alreadyKnown = crashes.Any(c => Math.Abs((c.Time - e.Time).TotalMinutes) <= 10);
            if (!alreadyKnown) crashes.Add(new Crash(e.Time, code, "Ereignis 41"));
        }

        return crashes.OrderByDescending(c => c.Time).ToList();
    }

    internal static List<FileInfo> ListDumps(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir)
                .GetFiles(pattern, SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch
        {
            // Ohne Adminrechte ist das Verzeichnis teils nicht lesbar - kein Fehlerfall.
            return new List<FileInfo>();
        }
    }
}

/// <summary>
/// Live-Kernel-Berichte: Windows schreibt diese, wenn eine Komponente haengt,
/// das System aber weiterlaeuft. Genau das passiert bei vielen Schwarzbildern.
/// </summary>
public sealed class LiveKernelReportCheck : ICheck
{
    public string Name => "Live-Kernel-Berichte";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.GpuDriver, Cause.DeviceDriver, Cause.DisplayLink };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var reports = BugCheckCheck.ListDumps(@"C:\Windows\LiveKernelReports", "*.dmp");
        var recent = reports.Where(r => (DateTime.Now - r.LastWriteTime).TotalDays <= ctx.LookbackDays).ToList();

        if (recent.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "live-kernel", Category = Category, Title = "Keine Live-Kernel-Berichte",
                    Severity = Severity.Ok,
                    Summary = "Windows hat keine Haenger einzelner Komponenten aufgezeichnet.",
                    Detail = "Geprueft wurde C:\\Windows\\LiveKernelReports. Ohne Administratorrechte ist dieser Ordner " +
                             "teilweise nicht lesbar - in dem Fall ist das Ergebnis nicht aussagekraeftig.",
                }
            });
        }

        var watchdog = recent.Where(r =>
            r.FullName.Contains("WATCHDOG", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        foreach (var r in recent.Take(25))
            sb.AppendLine($"{r.LastWriteTime:dd.MM.yyyy HH:mm}  {r.Directory?.Name}\\{r.Name}  ({r.Length / 1024 / 1024} MB)");

        var f = new Finding
        {
            Id = "live-kernel",
            Category = Category,
            Title = watchdog.Count > 0
                ? "Anzeige-Watchdog hat zugeschlagen (Live-Kernel-Bericht)"
                : "Live-Kernel-Berichte vorhanden",
            Severity = Severity.Critical,
            Summary = $"{recent.Count} Berichte in den letzten {ctx.LookbackDays} Tagen" +
                      (watchdog.Count > 0 ? $", davon {watchdog.Count} zur Anzeige/zum Grafiktreiber" : "") +
                      $" - zuletzt am {recent.Max(r => r.LastWriteTime):dd.MM.yyyy HH:mm}.",
            Detail = sb.ToString().TrimEnd() +
                     "\n\nWindows legt diese Berichte an, wenn eine Komponente nicht mehr reagiert, das System aber " +
                     "weiterlaeuft - ohne Bluescreen. Fuer 'Bild weg, Ton laeuft weiter' ist das der passende Fingerabdruck.",
            Occurrences = recent.Count,
            LastOccurrence = recent.Max(r => r.LastWriteTime),
            Recommendation =
                "Der Zeitstempel ist der entscheidende Anhaltspunkt: Er sollte mit den gemeldeten Schwarzbildern " +
                "uebereinstimmen. Wenn ja, haengt der Grafiktreiber und die Ursache liegt bei GPU/Treiber - " +
                "nicht beim Kabel.\n\n" +
                "Vorgehen: Grafiktreiber mit DDU vollstaendig entfernen und neu installieren, jegliche GPU-Uebertaktung " +
                "zuruecknehmen, Overlay-Software beenden.",
            Causes = new Dictionary<Cause, double> { [Cause.GpuDriver] = 0.8, [Cause.DisplayLink] = 0.2 },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>
/// Erkennt das Muster eines GPU-Haengers: Die Grafikkarte antwortet nicht mehr (Bild schwarz),
/// Windows und der Ton laufen aber weiter, bis der Rechner hart ausgeschaltet wird.
///
/// Die Einzelbefunde (Bluescreen 0x116, Kernel-Power 41 ohne Stoppcode, Live-Kernel-Bericht) sprechen
/// jeder fuer sich meist nur von "Treiber". Erst zusammen, ohne WHEA und ohne wiederhergestellte
/// Treiber-Resets, zeigen sie auf die Hardware der Karte. Dieser Befund fuehrt sie zusammen und
/// haengt die Blackbox der Ueberwachung (gpu-blackbox.csv) als Beleg an.
/// </summary>
public sealed class GpuHangPatternCheck : ICheck
{
    public string Name => "Muster eines GPU-Haengers";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } =
        new[] { Cause.GpuDriver, Cause.GpuHardware, Cause.DisplayLink, Cause.PowerSupply };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var since = DateTime.Now.AddDays(-ctx.LookbackDays);

        // 1) Bluescreens mit GPU-Stoppcode (0x116, 0x117, 0x141 ...).
        var bugchecks = EventLogService.Query("System",
            EventLogService.XpathProviders(
                new[] { "Microsoft-Windows-WER-SystemErrorReporting", "BugCheck" }, ctx.LookbackDays, 1001),
            100, includeData: true);

        var gpuCrashes = bugchecks
            .Where(e => (BugCheckCodes.TryParseFromMessage(e.Message)
                         ?? BugCheckCodes.TryParseFromData(e.DataValue("param1"))) is { } c && BugCheckCodes.IsGpuHang(c))
            .Select(e => (e.Time, Code: (BugCheckCodes.TryParseFromMessage(e.Message)
                                        ?? BugCheckCodes.TryParseFromData(e.DataValue("param1")))!.Value,
                          Params: BugCheckCodes.TryParseParameters(e.Message)))
            .ToList();

        // 2) Harte Ausschaltungen ohne Stoppcode: Kernel-Power 41 mit BugcheckCode 0.
        var kernel41 = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-Kernel-Power", ctx.LookbackDays, 41), 200, includeData: true);
        var hardOffs = kernel41
            .Where(e => (BugCheckCodes.TryParseFromData(e.DataValue("BugcheckCode")) ?? 0) == 0)
            .ToList();

        // 3) Wiederhergestellte Treiber-Resets (Display 4101) - die "harmlose" Variante.
        var tdr = EventLogService.Query("System",
            EventLogService.XpathProviders(new[] { "Display", "nvlddmkm", "amdkmdag", "amdwddmg" }, ctx.LookbackDays, 4101), 100);

        // 4) Live-Kernel-Berichte der Anzeige (Watchdog).
        var watchdog = BugCheckCheck.ListDumps(@"C:\Windows\LiveKernelReports", "*.dmp")
            .Where(r => r.LastWriteTime >= since &&
                        (r.FullName.Contains("WATCHDOG", StringComparison.OrdinalIgnoreCase) ||
                         r.Name.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 5) Hardwarefehler, die Windows selbst gemeldet bekommen hat.
        var whea = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-WHEA-Logger", ctx.LookbackDays), 50);

        // 6) Die eigene Ueberwachung: Karte nicht mehr abfragbar, samt Blackbox-Werten.
        var lostRows = GpuBlackbox.Read(since, DateTime.Now.AddMinutes(1))
            .Where(r => r[12].StartsWith(GpuBlackbox.LostMarker, StringComparison.Ordinal))
            .ToList();
        var lostTimes = lostRows.Select(r => ParseStamp(r[0])).Where(t => t is not null).Select(t => t!.Value).ToList();
        var lostIncidents = new IncidentStore().LoadAll(200)
            .Where(i => i.Kind == IncidentKind.GpuUnresponsive && i.Time >= since)
            .ToList();
        int monitorLost = Math.Max(lostTimes.Count, lostIncidents.Count);

        int degraded = CountLinkDegradedUnderLoad(since);

        int evidence = (gpuCrashes.Count > 0 ? 1 : 0) + (watchdog.Count > 0 ? 1 : 0) + (monitorLost > 0 ? 1 : 0);

        if (evidence == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "gpu-hang-pattern", Category = Category, Title = "Kein Muster eines GPU-Haengers",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen gibt es weder GPU-Bluescreens noch Anzeige-Watchdog-Berichte " +
                              "noch eine Grafikkarte, die der Ueberwachung die Antwort verweigert haette.",
                }
            });
        }

        var sb = new StringBuilder();
        sb.AppendLine("Belege in den letzten " + ctx.LookbackDays + " Tagen:");
        sb.AppendLine($"  {gpuCrashes.Count,4}x  Bluescreen mit GPU-Stoppcode (0x116 / 0x117 / 0x141 ...)");
        sb.AppendLine($"  {hardOffs.Count,4}x  harte Ausschaltung ohne Stoppcode (Kernel-Power 41, BugcheckCode 0)");
        sb.AppendLine($"  {tdr.Count,4}x  wiederhergestellter Treiber-Reset (Display 4101)");
        sb.AppendLine($"  {watchdog.Count,4}x  Live-Kernel-Bericht der Anzeige (Watchdog)");
        sb.AppendLine($"  {monitorLost,4}x  Grafikkarte antwortete der Ueberwachung nicht mehr");
        sb.AppendLine($"  {whea.Count,4}x  Hardwarefehler-Meldung von Windows (WHEA)");
        if (degraded > 0)
            sb.AppendLine($"  {degraded,4}x  PCIe-Link unter Last unter dem Maximum (gpu-blackbox.csv)");

        if (gpuCrashes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GPU-Bluescreens (neueste zuerst):");
            foreach (var c in gpuCrashes.OrderByDescending(c => c.Time).Take(10))
            {
                sb.AppendLine($"  {c.Time:dd.MM.yyyy HH:mm:ss}   {BugCheckCodes.Describe(c.Code).Display}");
                var p = BugCheckCodes.DescribeGpuHangParameters(c.Code, c.Params);
                if (p is not null) sb.AppendLine($"        {p}");
            }
        }

        if (lostTimes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(GpuBlackbox.Describe(lostTimes.Max(), 8) ?? "Blackbox ohne Werte vor dem Ausfall.");
        }

        // Bewertung: Mehrere unabhaengige Belege oder ein von der Ueberwachung selbst beobachteter Ausfall sind kritisch.
        bool critical = evidence >= 2 || monitorLost > 0 || gpuCrashes.Count >= 2;

        // Ohne WHEA-Meldung und ohne erfolgreiche Treiber-Resets bleibt die Hardware der Karte der Hauptverdacht.
        bool driverRecovers = tdr.Count > 0 && tdr.Count >= gpuCrashes.Count;

        var summary = new StringBuilder("GPU-Haenger: ");
        var parts = new List<string>();
        if (gpuCrashes.Count > 0) parts.Add($"{gpuCrashes.Count} GPU-Bluescreen(s)");
        if (hardOffs.Count > 0) parts.Add($"{hardOffs.Count} harte Ausschaltung(en)");
        if (watchdog.Count > 0) parts.Add($"{watchdog.Count} Watchdog-Bericht(e)");
        if (monitorLost > 0) parts.Add($"{monitorLost}x Karte antwortet nicht mehr (Ueberwachung)");
        summary.Append(string.Join(", ", parts)).Append('.');

        var recommendation =
            "Das Bild wird schwarz, Windows und der Ton laufen aber weiter: Die Grafikkarte haengt, der Rechner selbst nicht. " +
            "Ein Grafiktreiber, der dabei im Bluescreen genannt wird, hat der Karte nur beim Haengen zugesehen. " +
            (driverRecovers
                ? "Da Windows den Treiber mehrfach erfolgreich zuruecksetzen konnte, ist ein Treiberproblem hier durchaus moeglich.\n\n"
                : "Da der Treiber nicht wieder auf die Beine kommt und keine Hardwarefehler gemeldet werden, ist die Karte selbst " +
                  "(oder ihre PCIe-Anbindung bzw. Stromversorgung) der Hauptverdacht.\n\n") +
            "Vorgehen in dieser Reihenfolge - nach jedem Schritt mit demselben Belastungstest pruefen:\n\n" +
            "1) PCIe-Geschwindigkeit im BIOS fuer den Grafikkartenslot fest auf Gen 4 stellen (nicht 'Auto'). " +
            "Bei einer PCIe-5.0-Karte an einer neuen Plattform ist das die haeufigste Loesung.\n" +
            "2) Leistungsgrenze der Karte auf 60-70 % senken (MSI Afterburner oder 'nvidia-smi -pl'). " +
            "Haengt sie dann nicht mehr, ist Stromversorgung oder Netzteil die Spur.\n" +
            "3) Den 12V-2x6-Stecker der Karte abziehen und neu einstecken, bis er hoerbar und ohne Spalt einrastet. " +
            "Stecker auf Verfaerbung oder Schmelzspuren pruefen. Nur ein eigenes Kabel des Netzteils verwenden, keinen Adapter mit Verlaengerung.\n" +
            "4) Die Karte in einen anderen Rechner oder eine andere Karte in diesen Rechner setzen. " +
            "Haengt dieselbe Karte im anderen Rechner, ist sie defekt - dann Garantie/Umtausch mit diesem Bericht.\n" +
            "5) Erst danach den Treiber mit DDU neu installieren. Als erste und einzige Massnahme reicht das bei diesem Muster nicht.\n\n" +
            "Die Datei gpu-blackbox.csv (Ordner der Anwendungsdaten) enthaelt Takt, Leistung, Temperatur und PCIe-Link " +
            "sekundengenau bis zum Ausfall. Faellt der PCIe-Link vor dem Haenger ab oder springt die Leistung auf das Limit, " +
            "zeigt das, welcher der Schritte oben zuerst dran ist.";

        var causes = new Dictionary<Cause, double>
        {
            [Cause.GpuHardware] = driverRecovers ? 0.5 : 0.8,
            [Cause.PowerSupply] = 0.3,
            [Cause.GpuDriver] = driverRecovers ? 0.5 : 0.3,
            [Cause.Bios] = 0.2,
        };

        var times = gpuCrashes.Select(c => c.Time).Concat(lostTimes).Concat(watchdog.Select(w => w.LastWriteTime)).ToList();

        var f = new Finding
        {
            Id = "gpu-hang-pattern",
            Category = Category,
            Title = "Muster eines GPU-Haengers",
            Severity = critical ? Severity.Critical : Severity.Warning,
            Summary = summary.ToString(),
            Detail = sb.ToString().TrimEnd(),
            Occurrences = Math.Max(gpuCrashes.Count + watchdog.Count + monitorLost, 1),
            LastOccurrence = times.Count > 0 ? times.Max() : hardOffs.Count > 0 ? hardOffs.Max(e => e.Time) : null,
            Recommendation = recommendation,
            Causes = causes,
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }

    private static DateTime? ParseStamp(string s)
        => DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    /// <summary>Messpunkte unter hoher Last, bei denen der PCIe-Link unter seinem Maximum lief.</summary>
    private static int CountLinkDegradedUnderLoad(DateTime since)
    {
        int count = 0;
        foreach (var r in GpuBlackbox.Read(since, DateTime.Now.AddMinutes(1)))
        {
            if (r[12].Length > 0) continue;
            if (!double.TryParse(r[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var load) || load < 50) continue;

            if (Degraded(r[7]) || Degraded(r[8])) count++;
        }
        return count;

        static bool Degraded(string pair)
        {
            var p = pair.Split('/');
            return p.Length == 2 && int.TryParse(p[0], out var now) && int.TryParse(p[1], out var max) && now < max;
        }
    }
}

/// <summary>WHEA-Ereignisse: von der Hardware selbst gemeldete Fehler.</summary>
public sealed class WheaCheck : ICheck
{
    public string Name => "Hardwarefehler (WHEA)";
    public string Category => "Stabilitaet";
    public IReadOnlyList<Cause> Topics { get; } =
        new[] { Cause.PowerSupply, Cause.Memory, Cause.Bios, Cause.Thermal, Cause.Cpu };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var events = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-WHEA-Logger", ctx.LookbackDays), 100);

        if (events.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "whea", Category = Category, Title = "Keine Hardwarefehler gemeldet",
                    Severity = Severity.Ok,
                    Summary = $"Die Hardware hat in den letzten {ctx.LookbackDays} Tagen keine Fehler an Windows gemeldet (WHEA).",
                }
            });
        }

        var fatal = events.Where(e => e.Level is "Fehler" or "Kritisch").ToList();
        var corrected = events.Where(e => e.Level is "Warnung" or "Information").ToList();

        var sb = new StringBuilder();
        foreach (var e in events.Take(30)) sb.AppendLine(e.ToString());

        var f = new Finding
        {
            Id = "whea",
            Category = Category,
            Title = fatal.Count > 0 ? "Schwerwiegende Hardwarefehler gemeldet (WHEA)" : "Korrigierte Hardwarefehler (WHEA)",
            Severity = fatal.Count > 0 ? Severity.Critical : Severity.Warning,
            Summary = $"{events.Count} WHEA-Ereignisse" +
                      (fatal.Count > 0 ? $", davon {fatal.Count} schwerwiegend" : $" ({corrected.Count} korrigiert)") +
                      $", zuletzt am {events.Max(e => e.Time):dd.MM.yyyy HH:mm}.",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = events.Count,
            LastOccurrence = events.Max(e => e.Time),
            Recommendation =
                "WHEA-Meldungen kommen direkt von der Hardware und sind sehr ernst zu nehmen.\n" +
                "- 'Korrigierter Hardwarefehler' an einem PCIe-Geraet: haeufig die Anbindung der Grafikkarte. " +
                "Karte im Steckplatz neu setzen; testweise PCIe-Geschwindigkeit im BIOS auf Gen 4 begrenzen.\n" +
                "- Cache-/Speicherfehler der CPU: fast immer Uebertaktung. EXPO/XMP und alle Curve-Optimizer-Werte " +
                "im BIOS zuruecksetzen und erneut beobachten.",
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Memory] = 0.5, [Cause.Bios] = 0.3, [Cause.PowerSupply] = 0.2, [Cause.GpuDriver] = 0.2,
            },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Meldungen ueber thermische Drosselung des Prozessors.</summary>
public sealed class ThermalThrottleCheck : ICheck
{
    public string Name => "Thermische Drosselung";
    public string Category => "Temperatur";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Thermal, Cause.Cpu };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var events = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-Kernel-Processor-Power", ctx.LookbackDays, 37, 86, 87), 60);

        if (events.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "thermal-throttle", Category = Category, Title = "Keine thermische Drosselung protokolliert",
                    Severity = Severity.Ok,
                    Summary = "Der Prozessor wurde nicht wegen Ueberhitzung gedrosselt.",
                }
            });
        }

        var sb = new StringBuilder();
        foreach (var e in events.Take(20)) sb.AppendLine(e.ToString());

        var f = new Finding
        {
            Id = "thermal-throttle",
            Category = Category,
            Title = "Prozessor wurde gedrosselt",
            Severity = Severity.Warning,
            Summary = $"{events.Count} Drosselungsereignisse, zuletzt am {events.Max(e => e.Time):dd.MM.yyyy HH:mm}.",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = events.Count,
            LastOccurrence = events.Max(e => e.Time),
            Recommendation = "Kuehlung pruefen: Sitzt der CPU-Kuehler richtig, laufen alle Luefter, ist das Gehaeuse " +
                             "durchlueftet und staubfrei? Bei einer Wasserkuehlung zusaetzlich pruefen, ob die Pumpe " +
                             "laeuft und im BIOS auf Volllast eingestellt ist.",
            Causes = new Dictionary<Cause, double> { [Cause.Thermal] = 0.7 },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}
