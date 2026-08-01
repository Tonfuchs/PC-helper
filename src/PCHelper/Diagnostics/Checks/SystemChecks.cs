using System.Text;
using Microsoft.Win32;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Bestandsaufnahme der Hardware - rein informativ, aber Grundlage jedes Berichts.</summary>
public sealed class SystemOverviewCheck : ICheck
{
    public string Name => "Systemuebersicht";
    public string Category => "System";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        var sb = new StringBuilder();

        sb.AppendLine($"Prozessor:        {p.CpuName} ({p.CpuCores} Kerne / {p.CpuThreads} Threads)");
        sb.AppendLine($"Mainboard:        {p.BoardManufacturer} {p.BoardProduct}");
        sb.AppendLine($"BIOS:             {p.BiosVersion}, vom {p.FormatBiosAge()}");
        sb.AppendLine($"Arbeitsspeicher:  {p.TotalMemoryGb:0.#} GB in {p.MemoryModules.Count} Modul(en)");

        foreach (var m in p.MemoryModules)
            sb.AppendLine($"  - {m.Slot}: {m.CapacityGb:0.#} GB {m.TypeName} {m.Manufacturer} {m.PartNumber}, " +
                          $"laeuft mit {m.ConfiguredSpeedMhz} MT/s (SPD-Nennwert {m.RatedSpeedMhz})");

        foreach (var g in p.Gpus)
            sb.AppendLine($"Grafik:           {g.Name}, Treiber {g.DriverDisplay}" +
                          (g.DriverDate is null ? "" : $" vom {g.DriverDate:d}"));

        foreach (var d in p.Displays)
            sb.AppendLine($"Bildschirm:       {d}");

        foreach (var d in p.Disks)
            sb.AppendLine($"Datentraeger:     {d.Model} ({d.SizeGb:0} GB, {d.BusType} {d.MediaType}, Status: {d.HealthStatus})");

        sb.AppendLine($"Betriebssystem:   {p.OsCaption} {p.OsDisplayVersion} (Build {p.OsBuild})");
        sb.AppendLine($"Windows-Start:    {(p.LastBootTime is null ? "unbekannt" : p.LastBootTime.Value.ToString("g"))}");
        sb.AppendLine($"Laufzeit:         {p.Uptime.Days} Tage, {p.Uptime.Hours} Std, {p.Uptime.Minutes} Min");
        sb.AppendLine($"Energieplan:      {p.PowerPlanName}");

        var finding = new Finding
        {
            Id = "sys-overview",
            Category = Category,
            Title = "Systemuebersicht",
            Severity = Severity.Info,
            Summary = p.OneLine,
            Detail = sb.ToString().TrimEnd(),
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}

/// <summary>Prueft, wie alt die BIOS-/Firmware-Version ist.</summary>
public sealed class BiosAgeCheck : ICheck
{
    public string Name => "BIOS-Stand";
    public string Category => "Mainboard";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var p = ctx.Profile;
        if (p.BiosDate is null)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "bios-age", Category = Category, Title = "BIOS-Datum nicht auslesbar",
                    Severity = Severity.Info,
                    Summary = "Das BIOS-Datum konnte nicht ermittelt werden.",
                    Recommendation = "Die BIOS-Version im BIOS-Setup oder mit msinfo32 pruefen und mit der Herstellerseite vergleichen.",
                }
            });
        }

        int days = (int)(DateTime.Now - p.BiosDate.Value).TotalDays;
        var severity = days switch { > 540 => Severity.Warning, > 270 => Severity.Info, _ => Severity.Ok };

        var detail =
            $"Board:        {p.BoardManufacturer} {p.BoardProduct}\n" +
            $"BIOS-Version: {p.BiosVersion}\n" +
            $"BIOS-Datum:   {p.BiosDate:d} ({days} Tage alt)";

        var f = new Finding
        {
            Id = "bios-age",
            Category = Category,
            Title = severity == Severity.Ok ? "BIOS-Version ist aktuell genug" : "BIOS-Version pruefen",
            Severity = severity,
            Summary = severity == Severity.Ok
                ? $"Das BIOS ist vom {p.BiosDate:d} und damit vergleichsweise aktuell."
                : $"Das installierte BIOS ist vom {p.BiosDate:d} - also rund {days} Tage alt.",
            Detail = detail,
            Recommendation = severity == Severity.Ok
                ? null
                : "Auf der Supportseite des Mainboard-Herstellers pruefen, ob eine neuere BIOS-Version vorliegt. " +
                  "Gerade bei AM5/DDR5 beheben BIOS-Updates regelmaessig Speicher- und PCIe-Probleme. " +
                  "Nach dem Update EXPO/XMP neu setzen.",
            Causes = severity == Severity.Ok
                ? new Dictionary<Cause, double>()
                : new Dictionary<Cause, double> { [Cause.Bios] = 0.5, [Cause.Memory] = 0.2 },
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Freier Speicherplatz auf den Systemlaufwerken.</summary>
public sealed class DiskSpaceCheck : ICheck
{
    public string Name => "Speicherplatz";
    public string Category => "Datentraeger";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var tight = ctx.Profile.Volumes.Where(v => v.FreePercent < 10 || v.FreeGb < 20).ToList();
        var detail = string.Join("\n", ctx.Profile.Volumes.Select(v =>
            $"{v.Letter}  {v.FreeGb:0.#} GB frei von {v.TotalGb:0.#} GB ({v.FreePercent:0.#} %)"));

        var f = tight.Count == 0
            ? new Finding
            {
                Id = "disk-space", Category = Category, Title = "Ausreichend Speicherplatz",
                Severity = Severity.Ok,
                Summary = "Auf allen Laufwerken ist genug freier Speicher vorhanden.",
                Detail = detail,
            }
            : new Finding
            {
                Id = "disk-space", Category = Category, Title = "Wenig freier Speicherplatz",
                Severity = Severity.Warning,
                Summary = $"Knapper Speicher auf: {string.Join(", ", tight.Select(t => t.Letter))}.",
                Detail = detail,
                Recommendation = "Mindestens 10-15 % des Laufwerks freihalten. Zu wenig Platz stoert die Auslagerungsdatei, " +
                                 "Absturzabbilder werden nicht mehr geschrieben und SSDs verlieren an Leistung.",
                Causes = new Dictionary<Cause, double> { [Cause.Storage] = 0.4, [Cause.OperatingSystem] = 0.2 },
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}

/// <summary>Ausstehender Neustart und Hinweise auf beschaedigte Systemdateien.</summary>
public sealed class SystemIntegrityCheck : ICheck
{
    public string Name => "Windows-Integritaet";
    public string Category => "Windows";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var reasons = new List<string>();

        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Komponentenspeicher (CBS) erwartet einen Neustart.");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update erwartet einen Neustart.");
        if (ValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
            reasons.Add("Es stehen Dateiumbenennungen fuer den naechsten Start an.");

        findings.Add(reasons.Count > 0
            ? new Finding
            {
                Id = "pending-reboot", Category = Category, Title = "Neustart steht aus",
                Severity = Severity.Warning,
                Summary = "Windows wartet auf einen Neustart, um Aenderungen abzuschliessen.",
                Detail = string.Join("\n", reasons),
                Recommendation = "Den Rechner ueber 'Neu starten' (nicht ueber 'Herunterfahren') durchstarten. " +
                                 "Solange Updates halbfertig sind, koennen Treiber in einem gemischten Zustand laufen.",
                Causes = new Dictionary<Cause, double> { [Cause.OperatingSystem] = 0.4 },
            }
            : new Finding
            {
                Id = "pending-reboot", Category = Category, Title = "Kein Neustart ausstehend",
                Severity = Severity.Ok,
                Summary = "Es sind keine halbfertigen Updates oder Installationen offen.",
            });

        // Ergebnis frueherer Systemdatei-Pruefungen aus dem Setup-Protokoll.
        var sfc = EventLogService.Query("Application",
            EventLogService.Xpath("Microsoft-Windows-Wininit", ctx.LookbackDays * 3), 5);

        findings.Add(new Finding
        {
            Id = "sfc-hint", Category = Category, Title = "Systemdateien pruefen (empfohlener Routineschritt)",
            Severity = Severity.Info,
            Summary = "Beschaedigte Systemdateien lassen sich mit zwei Windows-Bordmitteln pruefen und reparieren.",
            Detail = sfc.Count > 0
                ? "Fruehere Pruefprotokolle:\n" + string.Join("\n", sfc.Select(e => e.ToString()))
                : "Es liegen keine juengeren Protokolle einer Systemdateipruefung vor.",
            Recommendation = "Die Reparatur 'Systemdateien pruefen und reparieren' im Bereich 'Reparaturen' ausfuehren. " +
                             "Dauer etwa 10-20 Minuten, Aenderungen sind ungefaehrlich.",
            FixIds = new[] { "sfc-dism" },
        });

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }

    private static bool KeyExists(string path)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k is not null; }
        catch { return false; }
    }

    private static bool ValueExists(string path, string value)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(value) is not null; }
        catch { return false; }
    }
}

/// <summary>Verdichtete Uebersicht der haeufigsten Fehlerereignisse.</summary>
public sealed class RecentErrorsCheck : ICheck
{
    public string Name => "Fehlerprotokoll";
    public string Category => "Ereignisprotokoll";

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var events = EventLogService.CriticalAndErrors("System", ctx.LookbackDays, 400);
        if (events.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "recent-errors", Category = Category, Title = "Keine Systemfehler protokolliert",
                    Severity = Severity.Ok,
                    Summary = $"In den letzten {ctx.LookbackDays} Tagen sind keine Fehler im Systemprotokoll aufgelaufen.",
                }
            });
        }

        var groups = events
            .GroupBy(e => (e.Provider, e.Id))
            .OrderByDescending(g => g.Count())
            .Take(12)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{events.Count} Fehler/kritische Ereignisse in den letzten {ctx.LookbackDays} Tagen.");
        sb.AppendLine();
        sb.AppendLine("Haeufigste Quellen:");
        foreach (var g in groups)
        {
            var last = g.Max(e => e.Time);
            sb.AppendLine($"  {g.Count(),4}x  {g.Key.Provider} (ID {g.Key.Id})  - zuletzt {last:dd.MM.yyyy HH:mm}");
            sb.AppendLine($"        {g.First().Short}");
        }

        var f = new Finding
        {
            Id = "recent-errors",
            Category = Category,
            Title = "Fehlerereignisse im Systemprotokoll",
            Severity = events.Count > 50 ? Severity.Warning : Severity.Info,
            Summary = $"{events.Count} Fehlereintraege in den letzten {ctx.LookbackDays} Tagen, " +
                      $"haeufigster Verursacher: {groups[0].Key.Provider} (ID {groups[0].Key.Id}).",
            Detail = sb.ToString().TrimEnd(),
            Occurrences = events.Count,
            LastOccurrence = events.Max(e => e.Time),
            Recommendation = "Diese Liste ist die Rohdatenbasis. Auffaellige Eintraege werden in den " +
                             "spezifischen Pruefungen oben bereits einzeln bewertet.",
        };

        return Task.FromResult<IEnumerable<Finding>>(new[] { f });
    }
}
