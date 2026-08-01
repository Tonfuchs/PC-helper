using System.Text;
using PCHelper.Core;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Bewertet die Speicherbestueckung und erkennt aktives EXPO/XMP.</summary>
public sealed class MemoryConfigCheck : ICheck
{
    public string Name => "Arbeitsspeicher-Konfiguration";
    public string Category => "Arbeitsspeicher";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        if (p.MemoryModules.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "memory-config", Category = Category, Title = "Speicherbestueckung nicht auslesbar",
                    Severity = Severity.Info,
                    Summary = "Ueber WMI konnten keine Speichermodule ermittelt werden.",
                }
            });
        }

        var sb = new StringBuilder();
        foreach (var m in p.MemoryModules)
            sb.AppendLine($"{m.Slot}: {m.CapacityGb:0.#} GB {m.TypeName}, {m.Manufacturer} {m.PartNumber}\n" +
                          $"      betrieben mit {m.ConfiguredSpeedMhz} MT/s (SPD-Nennwert: {m.RatedSpeedMhz} MT/s)");

        var findings = new List<Finding>();
        var maxConfigured = p.MemoryModules.Max(m => m.ConfiguredSpeedMhz);
        var isDdr5 = p.MemoryModules.Any(m => m.SmbiosMemoryType == 34);

        if (p.MemoryOverclocked)
        {
            findings.Add(new Finding
            {
                Id = "memory-expo",
                Category = Category,
                Title = "EXPO/XMP ist aktiv - der Speicher laeuft uebertaktet",
                Severity = Severity.Warning,
                Summary = $"Der Speicher laeuft mit {maxConfigured} MT/s und damit oberhalb des " +
                          $"{(isDdr5 ? "DDR5" : "DDR4")}-Standardtakts.",
                Detail = sb.ToString().TrimEnd() +
                         "\n\nEXPO (AMD) bzw. XMP (Intel) sind Uebertaktungsprofile. Sie sind ueblich und meistens " +
                         "unproblematisch - aber sie sind ausserhalb der garantierten Spezifikation. Ein grenzwertig " +
                         "stabiles Speicherprofil aeussert sich genau so, wie hier beschrieben: sporadisch, " +
                         "nicht reproduzierbar, mal Schwarzbild, mal Absturz, mal tagelang nichts.",
                Recommendation =
                    "Der schnellste und aussagekraeftigste Test ueberhaupt:\n\n" +
                    "1) Im BIOS EXPO/XMP auf 'Deaktiviert' bzw. 'Auto' stellen. Der Speicher laeuft dann mit " +
                    (isDdr5 ? "4800-5600" : "2133-3200") + " MT/s - etwas langsamer, aber garantiert spezifikationskonform.\n" +
                    "2) Zwei bis drei Tage normal nutzen.\n" +
                    "3a) Schwarzbilder weg -> die Ursache ist gefunden. Dann EXPO wieder aktivieren, aber eine Taktstufe " +
                    "niedriger, oder ein BIOS-Update einspielen.\n" +
                    "3b) Schwarzbilder weiterhin da -> Speicher ist entlastet, weiter bei Grafik/Anzeige suchen.\n\n" +
                    "Zusaetzlich: Der eingebaute Windows-Speichertest (siehe Bereich 'Werkzeuge') sollte fehlerfrei durchlaufen. " +
                    "Aussagekraeftiger ist allerdings MemTest86 ueber mehrere Durchlaeufe.",
                Causes = new Dictionary<Cause, double> { [Cause.Memory] = 0.7, [Cause.Bios] = 0.2 },
            });
        }
        else
        {
            findings.Add(new Finding
            {
                Id = "memory-expo",
                Category = Category,
                Title = "Speicher laeuft mit Standardtakt",
                Severity = Severity.Ok,
                Summary = $"{maxConfigured} MT/s - kein EXPO/XMP-Profil aktiv.",
                Detail = sb.ToString().TrimEnd() +
                         "\n\nDamit laeuft der Speicher innerhalb der Spezifikation. Als Ursache sporadischer " +
                         "Aussetzer ist er damit deutlich unwahrscheinlicher (aber nicht ausgeschlossen - " +
                         "defekte Module gibt es auch mit Standardtakt).",
            });
        }

        // Vier bestueckte Slots belasten den Speichercontroller bei DDR5 deutlich staerker.
        var populated = p.MemoryModules.Count;
        if (isDdr5 && populated >= 4)
        {
            findings.Add(new Finding
            {
                Id = "memory-slots",
                Category = Category,
                Title = "Vier DDR5-Module bestueckt",
                Severity = Severity.Info,
                Summary = $"{populated} Module belegt - das ist fuer den Speichercontroller anspruchsvoller als zwei.",
                Detail = sb.ToString().TrimEnd(),
                Recommendation = "Bei Instabilitaet testweise nur zwei Module (in den Slots A2/B2) betreiben. " +
                                 "Bei DDR5 sind vier Riegel oft nur mit reduziertem Takt stabil.",
                Causes = new Dictionary<Cause, double> { [Cause.Memory] = 0.3 },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Ergebnisse frueherer Windows-Speicherdiagnosen.</summary>
public sealed class MemoryDiagnosticsCheck : ICheck
{
    public string Name => "Speicherdiagnose";
    public string Category => "Arbeitsspeicher";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var results = EventLogService.Query("System",
            EventLogService.Xpath("Microsoft-Windows-MemoryDiagnostics-Results", 365), 10);

        if (results.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "memory-diag", Category = Category, Title = "Speichertest wurde noch nie ausgefuehrt",
                    Severity = Severity.Info,
                    Summary = "Es liegt kein Ergebnis der Windows-Speicherdiagnose vor.",
                    Recommendation = "Im Bereich 'Werkzeuge' die Windows-Speicherdiagnose starten. Sie prueft beim " +
                                     "naechsten Neustart den Arbeitsspeicher (Dauer je nach Umfang 15-60 Minuten).",
                    FixIds = Array.Empty<string>(),
                }
            });
        }

        var withErrors = results.Where(r => r.Id == 1202 ||
            r.Message.Contains("Fehler", StringComparison.OrdinalIgnoreCase) ||
            r.Message.Contains("error", StringComparison.OrdinalIgnoreCase)).ToList();

        var detail = string.Join("\n", results.Select(r => r.ToString()));

        var f = withErrors.Count > 0
            ? new Finding
            {
                Id = "memory-diag", Category = Category, Title = "Windows-Speicherdiagnose hat Fehler gefunden",
                Severity = Severity.Critical,
                Summary = "Ein frueherer Speichertest meldet Hardwarefehler.",
                Detail = detail,
                LastOccurrence = results.Max(r => r.Time),
                Recommendation = "Zuerst EXPO/XMP deaktivieren und erneut testen. Bleiben Fehler bestehen, die Module " +
                                 "einzeln testen, um den defekten Riegel zu finden - und diesen ersetzen (Garantie).",
                Causes = new Dictionary<Cause, double> { [Cause.Memory] = 0.9 },
            }
            : new Finding
            {
                Id = "memory-diag", Category = Category, Title = "Speichertest ohne Befund",
                Severity = Severity.Ok,
                Summary = $"Der letzte Test vom {results.Max(r => r.Time):d} meldet keine Fehler.",
                Detail = detail + "\n\nHinweis: Der Windows-Speichertest ist relativ oberflaechlich. " +
                         "Fuer eine belastbare Aussage sind mehrere Durchlaeufe mit MemTest86 noetig.",
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Der Windows-Schnellstart als Ursache konservierter Fehlerzustaende.</summary>
public sealed class FastStartupCheck : ICheck
{
    public string Name => "Windows-Schnellstart";
    public string Category => "Energie";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var f = ctx.Profile.FastStartupEnabled
            ? new Finding
            {
                Id = "fast-startup",
                Category = Category,
                Title = "Windows-Schnellstart ist aktiv",
                Severity = Severity.Warning,
                Summary = "Beim 'Herunterfahren' faehrt der Rechner nicht wirklich herunter.",
                Detail =
                    "Registrierung: HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power -> HiberbootEnabled = 1\n\n" +
                    "Mit aktivem Schnellstart speichert Windows beim Herunterfahren den Kernel samt Treiberzustaenden " +
                    "in eine Datei und laedt ihn beim Einschalten wieder. Ein Treiber, der sich verhakt hat, bleibt " +
                    "dadurch ueber Tage verhakt.\n\n" +
                    "Das erklaert ein typisches Muster: 'Mal ist es da, mal nicht' - und 'nach einem Neustart ist es weg, " +
                    "nach dem Ausschalten kommt es wieder'.",
                Recommendation = "Schnellstart deaktivieren. Der Rechner startet dann einige Sekunden langsamer, " +
                                 "dafuer ist jeder Start ein sauberer Start. Das ist eine der wirkungsvollsten " +
                                 "Massnahmen bei sporadischen Anzeige- und Treiberproblemen.",
                FixIds = new[] { "fast-startup-off" },
                Causes = new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.6, [Cause.GpuDriver] = 0.25 },
            }
            : new Finding
            {
                Id = "fast-startup", Category = Category, Title = "Windows-Schnellstart ist deaktiviert",
                Severity = Severity.Ok,
                Summary = "Jeder Startvorgang ist ein echter Kaltstart - so soll es bei Fehlersuche sein.",
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Energieeinstellungen, die Bildschirm und PCIe-Anbindung betreffen.</summary>
public sealed class PowerSettingsCheck : ICheck
{
    public string Name => "Energieeinstellungen";
    public string Category => "Energie";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        var findings = new List<Finding>();

        var detail = new StringBuilder();
        detail.AppendLine($"Aktiver Energiesparplan:        {p.PowerPlanName}");
        detail.AppendLine($"Bildschirm ausschalten nach:    {PowerApi.FormatTimeout(p.VideoIdleTimeoutSec)}");
        detail.AppendLine($"Energiesparmodus nach:          {PowerApi.FormatTimeout(p.StandbyIdleTimeoutSec)}");
        detail.AppendLine($"PCIe-Energieverwaltung (ASPM):  {AspmName(p.PciExpressAspm)}");
        detail.AppendLine($"Selektives USB-Energiesparen:   {(p.UsbSelectiveSuspend == 1 ? "aktiviert" : "deaktiviert")}");
        detail.AppendLine($"Minimaler Prozessorzustand:     {(p.ProcessorMinState is null ? "unbekannt" : p.ProcessorMinState + " %")}");

        // Bildschirm-Timeout: ein haeufiger Ausloeser, weil beim Aufwecken das
        // DisplayPort-Signal neu ausgehandelt werden muss.
        bool timeoutActive = p.VideoIdleTimeoutSec is > 0;
        findings.Add(timeoutActive
            ? new Finding
            {
                Id = "power-display-timeout",
                Category = Category,
                Title = "Bildschirm wird automatisch abgeschaltet",
                Severity = Severity.Warning,
                Summary = $"Windows schaltet den Bildschirm nach {PowerApi.FormatTimeout(p.VideoIdleTimeoutSec)} ab.",
                Detail = detail.ToString().TrimEnd() +
                         "\n\nBeim Abschalten und Wiedereinschalten wird die DisplayPort-Verbindung komplett neu " +
                         "ausgehandelt. Genau dabei kommt das Bild manchmal nicht zurueck - das Ergebnis ist ein " +
                         "Schwarzbild bei laufendem System und weiterlaufendem Ton.",
                Recommendation = "Zum Eingrenzen die automatische Bildschirmabschaltung voruebergehend auf 'Nie' setzen. " +
                                 "Wenn die Schwarzbilder danach ausbleiben, ist die DisplayPort-Aushandlung der Ausloeser " +
                                 "(Kabel, Monitor-Firmware oder Grafiktreiber).",
                FixIds = new[] { "display-timeout-never" },
                Causes = new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.5, [Cause.DisplayLink] = 0.4 },
            }
            : new Finding
            {
                Id = "power-display-timeout", Category = Category, Title = "Bildschirmabschaltung ist deaktiviert",
                Severity = Severity.Ok,
                Summary = "Windows schaltet den Bildschirm nicht automatisch ab.",
                Detail = detail.ToString().TrimEnd(),
            });

        // ASPM: Energiesparen auf der PCIe-Strecke - betrifft direkt die Grafikkarte.
        if (p.PciExpressAspm is > 0)
        {
            findings.Add(new Finding
            {
                Id = "power-aspm",
                Category = Category,
                Title = "PCIe-Energieverwaltung (ASPM) ist aktiv",
                Severity = Severity.Warning,
                Summary = $"Einstellung: {AspmName(p.PciExpressAspm)}.",
                Detail = detail.ToString().TrimEnd() +
                         "\n\nASPM versetzt die PCIe-Verbindung in Energiesparzustaende. Bei manchen Kombinationen aus " +
                         "Mainboard, BIOS und Grafikkarte fuehrt das Aufwachen aus diesen Zustaenden zu Aussetzern " +
                         "oder korrigierten PCIe-Fehlern (siehe auch WHEA-Meldungen).",
                Recommendation = "Zum Test ASPM abschalten. Der Mehrverbrauch ist im Desktopbetrieb vernachlaessigbar.",
                FixIds = new[] { "aspm-off" },
                Causes = new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.5, [Cause.GpuDriver] = 0.2 },
            });
        }
        else
        {
            findings.Add(new Finding
            {
                Id = "power-aspm", Category = Category, Title = "PCIe-Energieverwaltung ist abgeschaltet",
                Severity = Severity.Ok,
                Summary = "Die PCIe-Anbindung der Grafikkarte laeuft ohne Energiesparzustaende.",
                Detail = detail.ToString().TrimEnd(),
            });
        }

        // Energiesparmodus: bei Anzeigeproblemen ein haeufiger Ausloeser beim Aufwachen.
        if (p.StandbyIdleTimeoutSec is > 0)
        {
            findings.Add(new Finding
            {
                Id = "power-standby",
                Category = Category,
                Title = "Automatischer Energiesparmodus ist aktiv",
                Severity = Severity.Info,
                Summary = $"Der Rechner wechselt nach {PowerApi.FormatTimeout(p.StandbyIdleTimeoutSec)} in den Energiesparmodus.",
                Detail = detail.ToString().TrimEnd(),
                Recommendation = "Falls die Schwarzbilder typischerweise nach einer Pause auftreten, waehrend der " +
                                 "Fehlersuche den Energiesparmodus auf 'Nie' stellen.",
                FixIds = new[] { "sleep-never" },
                Causes = new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.3, [Cause.DisplayLink] = 0.2 },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }

    private static string AspmName(uint? value) => value switch
    {
        null => "unbekannt",
        0 => "Aus",
        1 => "Maessige Energieeinsparungen",
        2 => "Maximale Energieeinsparungen",
        _ => value.ToString()!,
    };
}
