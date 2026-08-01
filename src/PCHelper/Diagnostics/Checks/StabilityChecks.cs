using System.IO;
using System.Text;
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

/// <summary>Unerwartete Neustarts und Abschaltungen (Kernel-Power 41 / EventLog 6008).</summary>
public sealed class UnexpectedShutdownCheck : ICheck
{
    public string Name => "Unerwartete Neustarts";
    public string Category => "Stabilitaet";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var kernel41 = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-Kernel-Power", ctx.LookbackDays, 41), 60);

        var dirty6008 = EventLogService.Query("System",
            EventLogService.Xpath("EventLog", ctx.LookbackDays, 6008), 60);

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

        var sb = new StringBuilder();
        if (kernel41.Count > 0)
        {
            sb.AppendLine($"Kernel-Power 41 ({kernel41.Count}x) - das System wurde neu gestartet, ohne vorher sauber herunterzufahren:");
            foreach (var e in kernel41.Take(15)) sb.AppendLine("  " + e);
            sb.AppendLine();
        }
        if (dirty6008.Count > 0)
        {
            sb.AppendLine($"Ereignis 6008 ({dirty6008.Count}x) - unerwartetes Herunterfahren:");
            foreach (var e in dirty6008.Take(15)) sb.AppendLine("  " + e);
        }

        var last = kernel41.Concat(dirty6008).Max(e => e.Time);

        var f = new Finding
        {
            Id = "unexpected-shutdown",
            Category = Category,
            Title = "Rechner ist unerwartet ausgegangen bzw. neu gestartet",
            Severity = Severity.Critical,
            Summary = $"{total} unerwartete Abschaltungen in den letzten {ctx.LookbackDays} Tagen, zuletzt am {last:dd.MM.yyyy HH:mm}.",
            Detail = sb.ToString().TrimEnd() +
                     "\n\nEinordnung: Diese Ereignisse entstehen, wenn Windows keine Gelegenheit mehr hatte, sich " +
                     "ordentlich zu beenden - also bei hartem Ausschalten, Stromausfall, Netzteil-Schutzabschaltung " +
                     "oder einem Komplettabsturz ohne Bluescreen.",
            Occurrences = total,
            LastOccurrence = last,
            Recommendation =
                "Wichtige Abgrenzung zum Schwarzbild-Problem: Wenn beim Schwarzbild der TON WEITERLAEUFT, gehoeren diese " +
                "Ereignisse NICHT dazu - dann stammen sie von normalen harten Ausschaltvorgaengen (z. B. Netzschalter " +
                "gedrueckt, weil nichts mehr ging).\n\n" +
                "Wenn dagegen alles gleichzeitig ausgeht (Bild, Ton, Luefter), deutet das auf die Stromversorgung hin:\n" +
                "1) Alle PCIe-Stromkabel der Grafikkarte fest einstecken; getrennte Kabelstraenge statt Daisy-Chain verwenden.\n" +
                "2) Steckdosenleiste/Verlaengerung testweise umgehen.\n" +
                "3) Netzteil unter Last beobachten (Werte der Dauerueberwachung dieser App).",
            Causes = new Dictionary<Cause, double> { [Cause.PowerSupply] = 0.6, [Cause.Thermal] = 0.2, [Cause.Memory] = 0.2 },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Bluescreens und zugehoerige Absturzabbilder.</summary>
public sealed class BugCheckCheck : ICheck
{
    public string Name => "Bluescreens";
    public string Category => "Stabilitaet";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var bugchecks = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-WER-SystemErrorReporting", ctx.LookbackDays, 1001), 40);

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

        var sb = new StringBuilder();
        if (bugchecks.Count > 0)
        {
            sb.AppendLine($"{bugchecks.Count} Bluescreen-Meldungen:");
            foreach (var e in bugchecks.Take(15)) sb.AppendLine("  " + e);
            sb.AppendLine();
        }
        if (dumps.Count > 0)
        {
            sb.AppendLine($"{dumps.Count} Absturzabbilder unter C:\\Windows\\Minidump:");
            foreach (var d in dumps.Take(15)) sb.AppendLine($"  {d.LastWriteTime:dd.MM.yyyy HH:mm}  {d.Name}  ({d.Length / 1024} KB)");
        }

        var f = new Finding
        {
            Id = "bugcheck",
            Category = Category,
            Title = "Bluescreens vorhanden",
            Severity = Severity.Critical,
            Summary = bugchecks.Count > 0
                ? $"{bugchecks.Count} Bluescreens in den letzten {ctx.LookbackDays} Tagen."
                : $"{dumps.Count} Absturzabbilder gefunden.",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = Math.Max(bugchecks.Count, dumps.Count),
            LastOccurrence = bugchecks.Count > 0 ? bugchecks.Max(e => e.Time) : dumps.Max(d => d.LastWriteTime),
            Recommendation =
                "Der Stoppcode in der Meldung ist der entscheidende Hinweis:\n" +
                "- VIDEO_TDR_FAILURE / VIDEO_SCHEDULER_INTERNAL_ERROR -> Grafiktreiber oder Grafikkarte\n" +
                "- MEMORY_MANAGEMENT / PAGE_FAULT_IN_NONPAGED_AREA / IRQL_NOT_LESS_OR_EQUAL -> haeufig Arbeitsspeicher (EXPO testweise aus)\n" +
                "- WHEA_UNCORRECTABLE_ERROR -> Hardwarefehler, oft CPU/Speichercontroller oder Uebertaktung\n" +
                "- KERNEL_SECURITY_CHECK_FAILURE -> Treiber\n\n" +
                "Die Abbilder lassen sich mit dem kostenlosen Werkzeug WhoCrashed oder mit WinDbg auswerten.",
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Memory] = 0.4, [Cause.GpuDriver] = 0.3, [Cause.Software] = 0.2, [Cause.OperatingSystem] = 0.1,
            },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
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

/// <summary>WHEA-Ereignisse: von der Hardware selbst gemeldete Fehler.</summary>
public sealed class WheaCheck : ICheck
{
    public string Name => "Hardwarefehler (WHEA)";
    public string Category => "Stabilitaet";

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
