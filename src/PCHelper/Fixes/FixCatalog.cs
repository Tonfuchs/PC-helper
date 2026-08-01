using PCHelper.Core;

namespace PCHelper.Fixes;

public enum FixRisk { Gering, Mittel }

/// <summary>Eine nachvollziehbare, umkehrbare Systemaenderung.</summary>
public sealed class Fix
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }

    /// <summary>Was genau geaendert wird.</summary>
    public required string Description { get; init; }

    /// <summary>Warum das bei sporadischen Anzeigeproblemen hilft.</summary>
    public required string Why { get; init; }

    public FixRisk Risk { get; init; } = FixRisk.Gering;
    public bool NeedsReboot { get; init; }

    /// <summary>Auszufuehrende Kommandozeilen (laufen als Administrator).</summary>
    public required IReadOnlyList<string> Commands { get; init; }

    /// <summary>Kommandos, die die Aenderung wieder zuruecknehmen.</summary>
    public IReadOnlyList<string> RevertCommands { get; init; } = Array.Empty<string>();

    /// <summary>Geschaetzte Laufzeit als Hinweistext.</summary>
    public string Duration { get; init; } = "wenige Sekunden";

    public bool CanRevert => RevertCommands.Count > 0;

    public string CommandPreview => string.Join(Environment.NewLine, Commands);
    public string RevertPreview => string.Join(Environment.NewLine, RevertCommands);
}

/// <summary>Alle verfuegbaren Reparaturen. Neue Eintraege hier ergaenzen.</summary>
public static class FixCatalog
{
    private const string PowerScheme = "SCHEME_CURRENT";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SettingAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string SettingUsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string GraphicsDriversKey = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string PowerKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public static IReadOnlyList<Fix> All { get; } = new List<Fix>
    {
        new()
        {
            Id = "fast-startup-off",
            Title = "Windows-Schnellstart deaktivieren",
            Category = "Energie",
            Description = "Setzt HiberbootEnabled auf 0. 'Herunterfahren' faehrt den Rechner danach wirklich vollstaendig herunter.",
            Why = "Mit Schnellstart konserviert Windows Treiberzustaende ueber das Ausschalten hinweg. Ein einmal " +
                  "verhakter Grafiktreiber bleibt dadurch tagelang verhakt - das ist eine der haeufigsten Ursachen " +
                  "dafuer, dass ein Problem 'mal da und mal weg' ist.",
            Risk = FixRisk.Gering,
            NeedsReboot = true,
            Commands = new[]
            {
                $"reg add \"{PowerKey}\" /v HiberbootEnabled /t REG_DWORD /d 0 /f",
            },
            RevertCommands = new[]
            {
                $"reg add \"{PowerKey}\" /v HiberbootEnabled /t REG_DWORD /d 1 /f",
            },
        },

        new()
        {
            Id = "display-timeout-never",
            Title = "Automatische Bildschirmabschaltung ausschalten",
            Category = "Energie",
            Description = "Setzt 'Bildschirm ausschalten nach' im aktiven Energieplan auf 'Nie'.",
            Why = "Beim Ein- und Ausschalten des Bildschirms wird die DisplayPort-Verbindung neu ausgehandelt. " +
                  "Genau dabei bleibt das Bild manchmal weg. Waehrend der Fehlersuche schaltet man diesen " +
                  "Ausloeser vorruebergehend ab.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "powercfg /change monitor-timeout-ac 0",
                "powercfg /change monitor-timeout-dc 0",
            },
            RevertCommands = new[]
            {
                "powercfg /change monitor-timeout-ac 15",
                "powercfg /change monitor-timeout-dc 10",
            },
        },

        new()
        {
            Id = "sleep-never",
            Title = "Automatischen Energiesparmodus ausschalten",
            Category = "Energie",
            Description = "Setzt 'Energiesparmodus nach' im aktiven Energieplan auf 'Nie'.",
            Why = "Das Aufwachen aus dem Energiesparmodus ist ein weiterer Zeitpunkt, an dem Grafiktreiber und " +
                  "Monitoranbindung neu initialisiert werden - und dabei haengen bleiben koennen.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "powercfg /change standby-timeout-ac 0",
                "powercfg /change standby-timeout-dc 0",
            },
            RevertCommands = new[]
            {
                "powercfg /change standby-timeout-ac 30",
                "powercfg /change standby-timeout-dc 15",
            },
        },

        new()
        {
            Id = "aspm-off",
            Title = "PCIe-Energieverwaltung (ASPM) abschalten",
            Category = "Energie",
            Description = "Setzt die Verbindungsstatus-Energieverwaltung fuer PCI Express auf 'Aus'.",
            Why = "ASPM legt die PCIe-Strecke zur Grafikkarte im Leerlauf schlafen. Bei ungluecklichen Kombinationen " +
                  "aus Board, BIOS und Grafikkarte fuehrt das Aufwachen zu Aussetzern oder korrigierten PCIe-Fehlern.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                $"powercfg /setacvalueindex {PowerScheme} {SubPciExpress} {SettingAspm} 0",
                $"powercfg /setdcvalueindex {PowerScheme} {SubPciExpress} {SettingAspm} 0",
                $"powercfg /setactive {PowerScheme}",
            },
            RevertCommands = new[]
            {
                $"powercfg /setacvalueindex {PowerScheme} {SubPciExpress} {SettingAspm} 2",
                $"powercfg /setdcvalueindex {PowerScheme} {SubPciExpress} {SettingAspm} 2",
                $"powercfg /setactive {PowerScheme}",
            },
        },

        new()
        {
            Id = "usb-suspend-off",
            Title = "Selektives USB-Energiesparen abschalten",
            Category = "Energie",
            Description = "Verhindert, dass Windows einzelne USB-Anschluesse abschaltet.",
            Why = "Betrifft vor allem Eingabegeraete und USB-Audio. Hilft gegen Aussetzer von Maus, Tastatur und " +
                  "Headset - und schliesst eine Fehlerquelle aus, wenn beim Schwarzbild auch die Eingabe haengt.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                $"powercfg /setacvalueindex {PowerScheme} {SubUsb} {SettingUsbSuspend} 0",
                $"powercfg /setdcvalueindex {PowerScheme} {SubUsb} {SettingUsbSuspend} 0",
                $"powercfg /setactive {PowerScheme}",
            },
            RevertCommands = new[]
            {
                $"powercfg /setacvalueindex {PowerScheme} {SubUsb} {SettingUsbSuspend} 1",
                $"powercfg /setdcvalueindex {PowerScheme} {SubUsb} {SettingUsbSuspend} 1",
                $"powercfg /setactive {PowerScheme}",
            },
        },

        new()
        {
            Id = "hags-off",
            Title = "Hardwarebeschleunigte GPU-Planung abschalten",
            Category = "Grafik",
            Description = "Setzt HwSchMode auf 1 (deaktiviert). Entspricht dem Schalter in den Windows-Grafikeinstellungen.",
            Why = "Die hardwarebeschleunigte GPU-Planung uebergibt die Aufgabenverwaltung an die Grafikkarte. Sie ist " +
                  "eine wiederkehrende Ursache fuer Treiber-Resets und Schwarzbilder - und ohne echten Nachteil abschaltbar.",
            Risk = FixRisk.Gering,
            NeedsReboot = true,
            Commands = new[]
            {
                $"reg add \"{GraphicsDriversKey}\" /v HwSchMode /t REG_DWORD /d 1 /f",
            },
            RevertCommands = new[]
            {
                $"reg add \"{GraphicsDriversKey}\" /v HwSchMode /t REG_DWORD /d 2 /f",
            },
        },

        new()
        {
            Id = "tdr-delay",
            Title = "Zeitlimit fuer den Grafiktreiber erhoehen (TdrDelay = 10 s)",
            Category = "Grafik",
            Description = "Gibt dem Grafiktreiber 10 statt 2 Sekunden Zeit, bevor Windows ihn zwangsweise zuruecksetzt.",
            Why = "Wenn das Bild kurz schwarz wird und von selbst zurueckkommt, ist das ein Treiber-Reset. Mehr Zeit " +
                  "verhindert, dass ein kurzzeitig ueberlasteter Treiber abgeschossen wird.\n\n" +
                  "Wichtig: Das bekaempft das Symptom, nicht die Ursache. Wenn der Treiber wirklich haengt, " +
                  "dauert das Schwarzbild danach laenger.",
            Risk = FixRisk.Mittel,
            NeedsReboot = true,
            Commands = new[]
            {
                $"reg add \"{GraphicsDriversKey}\" /v TdrDelay /t REG_DWORD /d 10 /f",
            },
            RevertCommands = new[]
            {
                $"reg delete \"{GraphicsDriversKey}\" /v TdrDelay /f",
            },
        },

        new()
        {
            Id = "tdr-reset",
            Title = "TDR-Wiederherstellung auf Standard zuruecksetzen",
            Category = "Grafik",
            Description = "Entfernt abweichende TdrLevel-/TdrDelay-Werte, sodass wieder das Windows-Standardverhalten gilt.",
            Why = "Ist die TDR-Wiederherstellung abgeschaltet (TdrLevel = 0), kann Windows einen haengenden " +
                  "Grafiktreiber nicht mehr zurueckholen - aus einer kurzen Unterbrechung wird ein dauerhaftes Schwarzbild.",
            Risk = FixRisk.Gering,
            NeedsReboot = true,
            Commands = new[]
            {
                $"reg delete \"{GraphicsDriversKey}\" /v TdrLevel /f",
                $"reg delete \"{GraphicsDriversKey}\" /v TdrDelay /f",
            },
        },

        new()
        {
            Id = "power-defaults",
            Title = "Energieplaene auf Werkseinstellung zuruecksetzen",
            Category = "Energie",
            Description = "Fuehrt 'powercfg /restoredefaultschemes' aus und stellt alle Energieplaene wieder her.",
            Why = "Raeumt angesammelte Anpassungen aus Tuning-Anleitungen und Herstellerwerkzeugen auf. Sinnvoll, " +
                  "wenn unklar ist, was ueber die Jahre alles an den Energieoptionen veraendert wurde.",
            Risk = FixRisk.Mittel,
            Commands = new[]
            {
                "powercfg /restoredefaultschemes",
            },
        },

        new()
        {
            Id = "sfc-dism",
            Title = "Systemdateien pruefen und reparieren",
            Category = "Windows",
            Description = "Fuehrt DISM /RestoreHealth und anschliessend sfc /scannow aus.",
            Why = "Repariert beschaedigte Windows-Systemdateien. Standardschritt, bevor man Hardware verdaechtigt - " +
                  "und ungefaehrlich, weil nur gegen die Originaldateien abgeglichen wird.",
            Risk = FixRisk.Gering,
            Duration = "10 bis 30 Minuten",
            Commands = new[]
            {
                "DISM /Online /Cleanup-Image /RestoreHealth",
                "sfc /scannow",
            },
        },

        new()
        {
            Id = "reset-network-display-cache",
            Title = "Zwischengespeicherte Monitorprofile verwerfen",
            Category = "Grafik",
            Description = "Loescht die von Windows gespeicherten Monitorkonfigurationen. Windows liest sie beim naechsten Start neu ein.",
            Why = "Windows merkt sich Aufloesung, Bildrate und Anordnung pro Monitor. Ist dieser Zwischenspeicher " +
                  "fehlerhaft, kann beim Start oder Aufwachen eine ungueltige Konfiguration gesetzt werden - Ergebnis: kein Bild.",
            Risk = FixRisk.Mittel,
            NeedsReboot = true,
            Duration = "wenige Sekunden, Bildschirmanordnung muss danach neu eingestellt werden",
            Commands = new[]
            {
                @"reg delete ""HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration"" /f",
                @"reg delete ""HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectivity"" /f",
            },
        },
    };

    public static Fix? ById(string id) => All.FirstOrDefault(f => f.Id == id);
}

/// <summary>Fuehrt Reparaturen aus - optional mit vorherigem Wiederherstellungspunkt.</summary>
public static class FixRunner
{
    /// <summary>Legt einen Systemwiederherstellungspunkt an (benoetigt Adminrechte und aktiven Computerschutz).</summary>
    public static Task<ProcessResult> CreateRestorePointAsync()
    {
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        return Shell.RunElevatedBatchAsync(new[]
        {
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"Enable-ComputerRestore -Drive 'C:\\'\"",
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
            "$ErrorActionPreference='Stop'; " +
            "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' " +
            "-Name 'SystemRestorePointCreationFrequency' -Value 0 -PropertyType DWord -Force | Out-Null; " +
            $"Checkpoint-Computer -Description 'PC Helper {stamp}' -RestorePointType MODIFY_SETTINGS\"",
        }, "wiederherstellungspunkt");
    }

    /// <summary>Wendet eine Reparatur an (bzw. deren Ruecknahme).</summary>
    public static Task<ProcessResult> ApplyAsync(Fix fix, bool revert = false)
    {
        var commands = revert ? fix.RevertCommands : fix.Commands;
        if (commands.Count == 0)
            return Task.FromResult(new ProcessResult(-1, "", "Fuer diese Reparatur ist keine Ruecknahme hinterlegt."));

        Log.Info($"Reparatur {(revert ? "zuruecknehmen" : "anwenden")}: {fix.Id}");
        return Shell.RunElevatedBatchAsync(commands, (revert ? "revert-" : "") + fix.Id);
    }
}
