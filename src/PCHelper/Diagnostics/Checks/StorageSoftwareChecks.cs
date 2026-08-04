using System.Text;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Gesundheitszustand der Datentraeger plus Datentraegerfehler im Protokoll.</summary>
public sealed class StorageHealthCheck : ICheck
{
    public string Name => "Datentraeger-Gesundheit";
    public string Category => "Datentraeger";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Storage };

    private static readonly string[] DiskProviders = { "disk", "Disk", "nvme", "stornvme", "storahci", "Ntfs", "volmgr" };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var p = ctx.Profile;

        var sb = new StringBuilder();
        foreach (var d in p.Disks)
            sb.AppendLine($"{d.Model}\n    Groesse: {d.SizeGb:0} GB   Typ: {d.BusType} {d.MediaType}   Status: {d.HealthStatus}");

        var unhealthy = p.Disks.Where(d =>
            !string.IsNullOrEmpty(d.HealthStatus) &&
            !d.HealthStatus.Equals("Fehlerfrei", StringComparison.OrdinalIgnoreCase) &&
            !d.HealthStatus.Equals("unbekannt", StringComparison.OrdinalIgnoreCase)).ToList();

        findings.Add(unhealthy.Count > 0
            ? new Finding
            {
                Id = "disk-health", Category = Category, Title = "Datentraeger meldet Probleme",
                Severity = Severity.Critical,
                Summary = $"Betroffen: {string.Join(", ", unhealthy.Select(d => d.Model))}.",
                Detail = sb.ToString().TrimEnd(),
                Recommendation = "Sofort eine Sicherung der wichtigen Daten anlegen. Danach die SMART-Werte mit " +
                                 "CrystalDiskInfo oder dem Hersteller-Werkzeug (Samsung Magician, Seagate SeaTools) pruefen.",
                Causes = new Dictionary<Cause, double> { [Cause.Storage] = 0.8 },
            }
            : new Finding
            {
                Id = "disk-health", Category = Category, Title = "Datentraeger melden keinen Fehler",
                Severity = Severity.Ok,
                Summary = $"{p.Disks.Count} Datentraeger, Status jeweils unauffaellig.",
                Detail = sb.ToString().TrimEnd(),
            });

        var diskErrors = EventLogService.Query("System",
            EventLogService.XpathProviders(DiskProviders, ctx.LookbackDays), 80)
            .Where(e => e.Level is "Fehler" or "Kritisch" or "Warnung")
            .ToList();

        if (diskErrors.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "disk-events",
                Category = Category,
                Title = "Datentraeger-Fehler im Ereignisprotokoll",
                Severity = diskErrors.Count > 5 ? Severity.Warning : Severity.Info,
                Summary = $"{diskErrors.Count} Meldungen der Speichertreiber in den letzten {ctx.LookbackDays} Tagen.",
                Detail = string.Join("\n", diskErrors.Take(25).Select(e => e.ToString())),
                Occurrences = diskErrors.Count,
                LastOccurrence = diskErrors.Max(e => e.Time),
                Recommendation = "Ereignis-ID 129 oder 153 bedeutet, dass eine Anfrage an den Datentraeger " +
                                 "zurueckgesetzt werden musste. Das friert das System kurz komplett ein und kann " +
                                 "wie ein Anzeigeproblem wirken. Firmware der SSD und Chipsatztreiber pruefen.",
                Causes = new Dictionary<Cause, double> { [Cause.Storage] = 0.5 },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Erkennt Overlay-, Tuning- und RGB-Software, die tief ins System eingreift.</summary>
public sealed class OverlaySoftwareCheck : ICheck
{
    public string Name => "Overlay- und Tuning-Software";
    public string Category => "Software";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Software, Cause.GpuDriver };

    /// <summary>Prozessname -> was die Software macht und warum sie relevant ist.</summary>
    private static readonly (string Process, string Label, string Why)[] Watchlist =
    {
        ("MSIAfterburner", "MSI Afterburner", "veraendert Takt und Spannung der Grafikkarte"),
        ("RTSS", "RivaTuner Statistics Server", "blendet ein Overlay in jedes Spiel ein"),
        ("EVGAPrecision", "EVGA Precision", "veraendert Takt und Spannung der Grafikkarte"),
        ("iCUE", "Corsair iCUE", "steuert Luefter, Pumpe und Beleuchtung ueber eigene Treiber"),
        ("ArmouryCrate", "ASUS Armoury Crate", "steuert Mainboard, Luefter und Beleuchtung ueber eigene Treiber"),
        ("AsusCertService", "ASUS Systemdienst", "gehoert zu Armoury Crate"),
        ("AISuite", "ASUS AI Suite", "uebertaktet und steuert Spannungen; bekannt fuer Systemkonflikte"),
        ("SignalRgb", "SignalRGB", "steuert Beleuchtung ueber eigene Treiber"),
        ("OpenRGB", "OpenRGB", "steuert Beleuchtung ueber eigene Treiber"),
        ("LightingService", "Beleuchtungsdienst", "steuert RGB-Hardware"),
        ("NZXT CAM", "NZXT CAM", "Ueberwachung und Luefter-/Pumpensteuerung"),
        ("XtuService", "Intel XTU", "Uebertaktungswerkzeug"),
        ("RyzenMaster", "AMD Ryzen Master", "Uebertaktungswerkzeug"),
        ("ThrottleStop", "ThrottleStop", "veraendert CPU-Leistungsgrenzen"),
        ("Wallpaper Engine", "Wallpaper Engine", "rendert dauerhaft auf dem Desktop"),
        ("obs64", "OBS Studio", "haengt sich in die Grafikausgabe ein"),
    };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        var hits = new List<(string Label, string Why, bool Running, bool Autostart)>();

        foreach (var (proc, label, why) in Watchlist)
        {
            bool running = p.RunningProcesses.Any(r => r.Contains(proc.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                                                       || r.Contains(proc, StringComparison.OrdinalIgnoreCase));
            bool autostart = p.StartupEntries.Any(s => s.Contains(proc, StringComparison.OrdinalIgnoreCase));
            if (running || autostart) hits.Add((label, why, running, autostart));
        }

        if (hits.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "overlay-software", Category = Category, Title = "Keine kritische Tuning-Software aktiv",
                    Severity = Severity.Ok,
                    Summary = "Es laeuft keine der bekannten Overlay-, Uebertaktungs- oder RGB-Anwendungen.",
                }
            });
        }

        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            var state = (h.Running, h.Autostart) switch
            {
                (true, true) => "laeuft, startet mit Windows",
                (true, false) => "laeuft",
                _ => "im Autostart eingetragen",
            };
            sb.AppendLine($"- {h.Label} ({state})\n    {h.Why}");
        }

        var f = new Finding
        {
            Id = "overlay-software",
            Category = Category,
            Title = "Overlay- oder Tuning-Software aktiv",
            Severity = Severity.Warning,
            Summary = $"Gefunden: {string.Join(", ", hits.Select(h => h.Label))}.",
            Detail = sb.ToString().TrimEnd() +
                     "\n\nDiese Programme sind fuer sich genommen in Ordnung. Sie greifen aber tief in Grafiktreiber " +
                     "und Hardwaresteuerung ein und stehen bei sporadischen Schwarzbildern regelmaessig am Anfang " +
                     "der Ursachenkette.",
            Recommendation =
                "Sauberer Ausschlusstest:\n" +
                "1) Alle genannten Programme beenden und aus dem Autostart nehmen (Task-Manager > Autostart).\n" +
                "2) Neu starten und zwei bis drei Tage normal nutzen.\n" +
                "3) Bleibt das Schwarzbild aus, die Programme einzeln wieder aktivieren, um den Verursacher zu finden.\n\n" +
                "Besonders wichtig: Wenn Afterburner eine eigene Spannungs-/Taktkurve laedt, ist die Grafikkarte " +
                "dauerhaft uebertaktet oder untervoltet - auch wenn das Fenster geschlossen ist.",
            Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.6, [Cause.GpuDriver] = 0.3 },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Wendet die Wissensdatenbank auf das erhobene Systemprofil an.</summary>
public sealed class KnowledgeBaseCheck : ICheck
{
    public string Name => "Bekannte Problemmuster";
    public string Category => "Bekannte Faelle";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = ctx.Knowledge.Evaluate(ctx.Profile).ToList();

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "kb-none", Category = Category, Title = "Keine bekannten Problemmuster zutreffend",
                Severity = Severity.Ok,
                Summary = $"Kein Eintrag der Wissensdatenbank passt auf diese Hardwarekombination " +
                          $"({ctx.Knowledge.Issues.Count} Eintraege geprueft, Quelle: {ctx.Knowledge.Source}).",
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}
