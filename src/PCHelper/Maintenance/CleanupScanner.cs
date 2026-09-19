using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Maintenance;

/// <summary>
/// "Platz schaffen": rechnet erst zusammen, was wo liegt - geloescht wird nur auf Knopfdruck und konservativ.
/// Downloads und Desktop werden nur gezeigt, nie angefasst.
/// </summary>
public static class CleanupScanner
{
    /// <summary>Ab dieser Groesse lohnt es sich, einen Punkt ueberhaupt anzuzeigen.</summary>
    private const long Threshold = 200L * 1024 * 1024;

    private static readonly TimeSpan TempAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Hinweis fuer die Ansicht, wenn PC Helper ohne Administratorrechte laeuft: Windows-eigene Ordner sind dann weder
    /// messbar noch aufraeumbar und fehlen deshalb in der Liste, obwohl sie oft am meisten Platz belegen.
    /// </summary>
    public static string? AdminNote => Shell.IsElevated ? null :
        "PC Helper laeuft ohne Administratorrechte. Update-Reste, Windows.old und die System-Zwischenablage gehoeren dem " +
        "System und lassen sich so weder ausmessen noch aufraeumen - sie fehlen deshalb in dieser Liste. Zum Nachsehen " +
        "PC Helper beenden und per Rechtsklick \"Als Administrator ausfuehren\" starten.";

    public static Task<IReadOnlyList<MaintItem>> ScanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<MaintItem>>(Scan, ct);

    public static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private static IReadOnlyList<MaintItem> Scan()
    {
        var items = new List<MaintItem>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool elevated = Shell.IsElevated;

        AddFreeSpace(items, windows);

        // ---- Windows-Update-Reste
        AddFolder(items, "cleanup|wu-download", "Heruntergeladene Windows-Updates",
            new CleanTarget(Path.Combine(windows, @"SoftwareDistribution\Download")), 900,
            "Windows hebt die Installationsdateien bereits eingespielter Updates auf. Gebraucht werden sie nicht mehr - " +
            "Windows laedt sie im Notfall einfach neu.",
            "Kann weg. Voellig gefahrlos, es sind nur Kopien der Installationsdateien.");

        AddFolder(items, "cleanup|delivery-optimization", "Zwischenspeicher der Update-Verteilung",
            new CleanTarget(Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization")), 850,
            "Windows hebt Update-Teile auf, um sie an andere Rechner im Netz weiterzugeben. Auf einem einzelnen Rechner " +
            "zuhause bringt das nichts.",
            "Kann weg.");

        // ---- Windows.old: nie selbst loeschen, Windows schuetzt den Ordner mit Sonderrechten.
        var windowsOld = Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            var size = CleanupEngine.Measure(new CleanTarget(windowsOld));
            items.Add(new MaintItem
            {
                Key = "cleanup|windows-old",
                Title = "Alte Windows-Version (Windows.old)",
                Value = FormatSize(size) + (elevated ? "" : " oder mehr"),
                Meaning = "Uebrig von einem grossen Windows-Update. Damit koenntest du zur alten Version zurueck - das geht " +
                          "ohnehin nur in den ersten 10 Tagen nach dem Update, danach ist der Ordner reiner Ballast. Meist der " +
                          "groesste einzelne Brocken ueberhaupt.",
                Recommendation = "Wenn das Update laenger als zwei Wochen her ist und alles laeuft: weg damit. Danach ist eine " +
                                 "Rueckkehr zur alten Version nicht mehr moeglich.",
                Severity = Severity.Warning,
                Weight = 950,
                ActionText = "Datentraegerbereinigung oeffnen",
                ActionDetail = "Dieser Ordner wird nicht von PC Helper geloescht: Windows schuetzt ihn mit Sonderrechten, und " +
                               "mit Gewalt dranzugehen kann das System beschaedigen. Stattdessen oeffnet sich die " +
                               "Datentraegerbereinigung von Windows. Dort \"Systemdateien bereinigen\" anklicken und " +
                               "\"Vorherige Windows-Installation(en)\" ankreuzen.",
                Execute = () =>
                {
                    Shell.Launch("cleanmgr.exe", $"/d {windows[..1]}");
                    return Task.FromResult(new ActionResult(true,
                        "Die Datentraegerbereinigung von Windows ist offen. Dort \"Systemdateien bereinigen\" anklicken und " +
                        "\"Vorherige Windows-Installation(en)\" ankreuzen."));
                },
            });
        }

        // ---- Temp-Ordner: nur was aelter als 7 Tage ist. Alles Neuere kann noch in Benutzung sein.
        AddFolder(items, "cleanup|user-temp", "Dein Zwischenablage-Ordner: alte Reste",
            new CleanTarget(Path.GetTempPath().TrimEnd('\\'), TempAge), 800,
            "Zwischendateien, die Programme angelegt und nie wieder aufgeraeumt haben. Gezaehlt wird nur, was aelter als " +
            "sieben Tage ist - alles Neuere koennte gerade noch in Benutzung sein.",
            "Kann weg. Was noch gebraucht wird, legen die Programme neu an.");

        AddFolder(items, "cleanup|windows-temp", "Windows-Zwischenablage: alte Reste",
            new CleanTarget(Path.Combine(windows, "Temp"), TempAge), 795,
            "Zwischendateien des Systems und von Installationsprogrammen. Gezaehlt wird nur, was aelter als sieben Tage ist.",
            "Kann weg. Was noch gebraucht wird, legen die Programme neu an.");

        AddRecycleBin(items);
        AddDumps(items, windows);

        // ---- Nur zeigen, nicht loeschen
        AddBrowserCaches(items);

        foreach (var (name, path) in new[]
                 {
                     ("Downloads", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads"),
                     ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                 })
        {
            var size = CleanupEngine.Measure(new CleanTarget(path));
            if (size <= 2L << 30) continue;

            items.Add(new MaintItem
            {
                Key = $"cleanup|folder|{name}",
                Title = $"Ordner \"{name}\"",
                Value = FormatSize(size),
                Meaning = $"Hier liegen {FormatSize(size)}. Klassischer Sammelplatz fuer Sachen, die man einmal gebraucht und " +
                          "nie wieder angefasst hat.",
                Recommendation = "Loesche ich nicht - da kann Wichtiges drin sein. Lohnt sich aber, mal selbst durchzugehen.",
                Severity = Severity.Info,
                Weight = 300,
                ActionText = "Ordner oeffnen",
                Execute = () => { Shell.Open(path); return Task.FromResult(new ActionResult(true, "Ordner geoeffnet.")); },
            });
        }

        AddSteamCaches(items);
        return items;
    }

    // ------------------------------------------------------------------ Einzelne Punkte

    private static void AddFreeSpace(List<MaintItem> items, string windows)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(windows) ?? @"C:\");
            double freeGb = drive.AvailableFreeSpace / (double)(1L << 30);
            double totalGb = drive.TotalSize / (double)(1L << 30);
            double percent = drive.TotalSize == 0 ? 0 : 100.0 * drive.AvailableFreeSpace / drive.TotalSize;

            var severity = percent < 10 ? Severity.Critical : percent < 20 ? Severity.Warning : Severity.Ok;
            items.Add(new MaintItem
            {
                Key = "cleanup|free-space",
                Title = $"Freier Platz auf {drive.Name.TrimEnd('\\')}",
                Value = $"{freeGb:0.#} GB ({percent:0} %)",
                Meaning = percent < 10
                    ? $"Nur noch {freeGb:0.#} GB von {totalGb:0.#} GB frei. Unter 10 Prozent wird Windows spuerbar zaeh: die " +
                      "Auslagerungsdatei kann nicht mehr richtig wachsen, Updates scheitern, und Programme, die Zwischendateien " +
                      "anlegen, stocken."
                    : percent < 20
                        ? $"Noch {freeGb:0.#} GB von {totalGb:0.#} GB frei. Wird langsam eng."
                        : "Der Ausgangspunkt. Alles darunter zeigt, wo Platz zurueckzuholen waere.",
                Recommendation = percent < 20 ? "Unten nachsehen, was weg kann." : "",
                Severity = severity,
                Weight = 1000,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Freier Platz nicht ermittelbar: " + ex.Message);
        }
    }

    /// <summary>Ein Ordner, dessen Inhalt sich per Knopf leeren laesst - sofern er gross genug ist und lesbar.</summary>
    private static void AddFolder(List<MaintItem> items, string key, string title, CleanTarget target,
        int weight, string meaning, string recommendation)
    {
        var size = CleanupEngine.Measure(target);
        if (size <= Threshold) return;

        items.Add(new MaintItem
        {
            Key = key,
            Title = title,
            Value = FormatSize(size),
            Meaning = meaning,
            Recommendation = recommendation,
            Severity = Severity.Warning,
            Weight = weight,
            ActionText = "Aufraeumen",
            Confirm = true,
            ActionDetail = $"Geloescht wird der Inhalt von {target.Path}" +
                           (target.MinAge is null ? "." : $", aber nur, was aelter als {target.MinAge.Value.TotalDays:0} Tage ist.") +
                           " Was gerade in Benutzung ist, bleibt liegen. Der Ordner selbst bleibt bestehen.",
            Execute = () => Task.Run(() => Report(CleanupEngine.Clean(target), target.MinAge is not null)),
        });
    }

    private static void AddRecycleBin(List<MaintItem> items)
    {
        long size = QueryRecycleBin();
        if (size <= Threshold) return;

        items.Add(new MaintItem
        {
            Key = "cleanup|recycle-bin",
            Title = "Papierkorb",
            Value = FormatSize(size),
            Meaning = "Geloeschte Dateien, die noch nicht wirklich weg sind und weiter Platz belegen.",
            Recommendation = "Vorher kurz reinschauen, ob nichts Wichtiges drin liegt - danach leeren.",
            Severity = Severity.Warning,
            Weight = 780,
            ActionText = "Leeren",
            Confirm = true,
            ActionDetail = "Der Papierkorb aller Laufwerke wird endgueltig geleert.",
            Execute = () => Task.Run(() =>
            {
                EmptyRecycleBin();
                var rest = QueryRecycleBin();
                return rest == 0
                    ? new ActionResult(true, $"Papierkorb geleert ({FormatSize(size)} freigeraeumt).")
                    : new ActionResult(false, $"Der Papierkorb liess sich nicht ganz leeren - es liegen noch {FormatSize(rest)} darin.");
            }),
        });
    }

    private static void AddDumps(List<MaintItem> items, string windows)
    {
        var targets = new[]
        {
            new CleanTarget(Path.Combine(windows, "MEMORY.DMP")),
            new CleanTarget(Path.Combine(windows, "Minidump")),
            new CleanTarget(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps")),
        };

        long size = targets.Sum(CleanupEngine.Measure);
        if (size <= Threshold) return;

        items.Add(new MaintItem
        {
            Key = "cleanup|dumps",
            Title = "Absturzberichte",
            Value = FormatSize(size),
            Meaning = "Speicherabbilder von abgestuerzten Programmen oder Blaubildschirmen. Nuetzlich zur Fehlersuche, sonst nur gross.",
            Recommendation = "Wenn du gerade keinem Absturz auf der Spur bist: koennen weg.",
            Severity = Severity.Info,
            Weight = 700,
            ActionText = "Loeschen",
            Confirm = true,
            ActionDetail = "Geloescht werden MEMORY.DMP, der Ordner Minidump und die CrashDumps deines Benutzerkontos. " +
                           "Die Diagnose dieser App liest die Bluescreen-Codes aus dem Ereignisprotokoll und braucht die Abbilder nicht.",
            Execute = () => Task.Run(() => Report(targets.Aggregate(default(CleanResult), (sum, t) => sum + CleanupEngine.Clean(t)), false)),
        });
    }

    /// <summary>Wird nur angezeigt, nicht geloescht: Dafuer muesste der Browser zu sein, und der Gewinn ist meist klein.</summary>
    private static void AddBrowserCaches(List<MaintItem> items)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var caches = new (string Name, string Path)[]
        {
            ("Firefox", Path.Combine(local, @"Mozilla\Firefox\Profiles")),
            ("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable\Cache")),
            ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache")),
            ("Chrome", Path.Combine(local, @"Google\Chrome\User Data\Default\Cache")),
        };

        long total = 0;
        var parts = new List<string>();
        foreach (var (name, path) in caches)
        {
            var size = CleanupEngine.Measure(new CleanTarget(path));
            if (size <= 100L << 20) continue;
            total += size;
            parts.Add($"{name} {FormatSize(size)}");
        }

        if (total <= Threshold) return;

        items.Add(new MaintItem
        {
            Key = "cleanup|browser-caches",
            Title = "Zwischenspeicher der Browser",
            Value = FormatSize(total),
            Meaning = $"Aufbewahrte Bilder und Dateien von Webseiten, damit sie beim naechsten Besuch schneller laden: " +
                      $"{string.Join(", ", parts)}. Das ist kein Muell, sondern Absicht - Loeschen macht das Surfen erstmal " +
                      "langsamer, nicht schneller.",
            Recommendation = "Nur loeschen, wenn du den Platz wirklich brauchst. Am besten im Browser selbst, dort weiss er, " +
                             "was er noch braucht.",
            Severity = Severity.Info,
            Weight = 400,
        });
    }

    private static void AddSteamCaches(List<MaintItem> items)
    {
        var steam = SteamPath();
        if (steam is null) return;

        long size = new[] { @"depotcache", @"steamapps\shadercache", @"steamapps\downloading" }
            .Sum(p => CleanupEngine.Measure(new CleanTarget(Path.Combine(steam, p))));
        if (size <= 1L << 30) return;

        items.Add(new MaintItem
        {
            Key = "cleanup|steam-caches",
            Title = "Steam-Zwischenspeicher",
            Value = FormatSize(size),
            Meaning = "Reste von Downloads und vorberechnete Grafikdaten von Steam. Die Shader-Daten sorgen dafuer, dass Spiele " +
                      "fluessiger starten - die legt Steam bei Bedarf neu an.",
            Recommendation = "Nur anfassen, wenn du den Platz brauchst. Steam raeumt hier meist selbst auf.",
            Severity = Severity.Info,
            Weight = 250,
        });
    }

    private static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return p.Replace('/', '\\');
        }
        catch { /* dann eben der Standardpfad */ }

        var fallback = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static ActionResult Report(CleanResult result, bool ageLimited)
    {
        var text = $"{FormatSize(result.Freed)} freigeraeumt ({result.Deleted} Dateien" +
                   (ageLimited ? ", nur aelter als sieben Tage" : "") + ").";
        if (result.Blocked > 0)
            text += $" {result.Blocked} waren gerade in Benutzung und blieben liegen - beim naechsten Mal klappt es.";
        return new ActionResult(true, text);
    }

    // ------------------------------------------------------------------ Papierkorb (Shell-API)

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;

    private static long QueryRecycleBin()
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void EmptyRecycleBin()
    {
        try { SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound); }
        catch (Exception ex) { Log.Warn("Papierkorb leeren: " + ex.Message); }
    }
}
