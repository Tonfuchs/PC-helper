using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCHelper.Diagnostics;

namespace PCHelper.Maintenance;

/// <summary>Was ein Windows-Fehlercode aus dem Geraete-Manager bedeutet und was man dagegen tut.</summary>
public sealed record DeviceErrorInfo(string Short, string Meaning, string Advice);

/// <summary>
/// Windows meldet Geraeteprobleme als nackte Zahl. Hier steht, was sie bedeutet - genau das, was die alten
/// Problembehandlungen einem abgenommen haben.
/// </summary>
public static class DeviceErrorCodes
{
    private static readonly Dictionary<int, DeviceErrorInfo> Table = new()
    {
        [1] = new("Das Geraet ist falsch eingerichtet", "Windows hat fuer dieses Geraet keine brauchbare Einrichtung gefunden. Meist ein Treiber, der nicht richtig installiert wurde.", "Neu starten hilft oft. Sonst Treiber neu installieren."),
        [3] = new("Der Treiber ist beschaedigt", "Der Treiber ist kaputt oder es fehlt Arbeitsspeicher.", "Treiber neu installieren."),
        [10] = new("Das Geraet laesst sich nicht starten", "Der haeufigste Fehler ueberhaupt. Das Geraet meldet sich, laesst sich aber nicht in Betrieb nehmen. Bei USB-Geraeten steckt fast immer eine Stromspar-Einstellung oder ein hakeliger Anschluss dahinter.", "Geraet neu starten (der Knopf hier), danach Stromsparen fuer USB abschalten."),
        [12] = new("Es sind nicht genug Ressourcen frei", "Zwei Geraete wollen dieselben Systemressourcen.", "Ein anderes Geraet abziehen oder einen anderen Anschluss verwenden."),
        [14] = new("Braucht einen Neustart", "Das Geraet laeuft erst nach einem echten Neustart richtig.", "\"Neu starten\" waehlen - nicht \"Herunterfahren\", das reicht bei eingeschaltetem Schnellstart nicht."),
        [18] = new("Die Treiber muessen neu installiert werden", "Windows moechte den Treiber neu einrichten.", "Geraet neu starten, sonst Treiber neu installieren."),
        [19] = new("Die Einrichtung ist durcheinander", "Die Angaben zu diesem Geraet in der Windows-Registrierung sind unvollstaendig oder beschaedigt.", "Geraet entfernen und neu einlesen lassen."),
        [21] = new("Windows raeumt das Geraet gerade weg", "Ein Uebergangszustand. Wenn er bleibt, haengt Windows fest.", "Neu starten."),
        [22] = new("Das Geraet ist abgeschaltet", "Jemand - oder ein Programm - hat dieses Geraet deaktiviert.", "Wieder einschalten."),
        [24] = new("Das Geraet ist nicht da", "Es ist eingetragen, aber nicht auffindbar. Entweder abgezogen oder defekt.", "Kabel pruefen. Wenn es weg bleiben soll: entfernen."),
        [28] = new("Es ist kein Treiber installiert", "Windows weiss nicht, wie es mit diesem Geraet reden soll.", "Treiber installieren - beim Hersteller oder ueber Windows Update."),
        [31] = new("Der Treiber laeuft nicht richtig", "Windows kann den noetigen Treiber nicht laden.", "Treiber neu installieren."),
        [32] = new("Der Treiber ist abgeschaltet", "Der Starttyp des Treibers steht auf \"aus\".", "Treiber neu installieren."),
        [33] = new("Windows findet die Ressourcen nicht", "Meist ein Hardwarefehler oder ein falsch eingestelltes Geraet.", "Anderen Anschluss probieren."),
        [37] = new("Der Treiber ist abgestuerzt", "Der Treiber hat sich beim Starten verabschiedet.", "Geraet neu starten, sonst Treiber neu installieren."),
        [38] = new("Ein alter Treiber haengt noch im Speicher", "Eine fruehere Fassung des Treibers laeuft noch und blockiert die neue.", "Echten Neustart machen."),
        [39] = new("Der Treiber fehlt oder ist beschaedigt", "Die Treiberdatei ist weg oder kaputt.", "Treiber neu installieren."),
        [40] = new("Die Angaben in der Registrierung sind falsch", "Windows findet die Angaben zu diesem Geraet nicht wieder.", "Geraet entfernen und neu einlesen lassen."),
        [43] = new("Windows hat das Geraet gestoppt", "Das Geraet selbst hat einen Fehler gemeldet, woraufhin Windows es abgeschaltet hat. Bei USB-Kameras und Sticks der Klassiker nach dem Aufwachen aus dem Stromsparen - das Geraet meldet sich zurueck, aber nicht sauber.", "Geraet neu starten (der Knopf hier). Danach Stromsparen fuer USB abschalten, damit es nicht wiederkommt."),
        [45] = new("Zurzeit nicht angeschlossen", "Ein Karteileichen-Eintrag: Windows erinnert sich an das Geraet, es steckt aber nicht mehr. Voellig normal bei allem, was man ab und zu absteckt.", "Nichts tun. Nur aufraeumen, wenn es unuebersichtlich wird."),
        [47] = new("Zum Auswerfen vorbereitet", "Windows hat das Geraet zum sicheren Entfernen freigegeben, es steckt aber noch.", "Abziehen und wieder einstecken - oder den Knopf hier druecken."),
        [48] = new("Der Treiber ist gesperrt", "Dieser Treiber ist bekannt dafuer, Probleme zu machen, und wurde deshalb blockiert.", "Beim Hersteller nach einer neueren Fassung sehen."),
        [52] = new("Der Treiber ist nicht unterschrieben", "Windows traut der Treiberdatei nicht, weil die digitale Unterschrift fehlt oder nicht stimmt.", "Treiber vom Hersteller neu laden."),
    };

    public static DeviceErrorInfo? Describe(int code) => Table.GetValueOrDefault(code);
}

/// <summary>Ein Geraet aus dem Geraete-Manager (Win32_PnPEntity).</summary>
public sealed record PnpDevice(string Name, string DeviceId, string DeviceClass, int ErrorCode, string Status)
{
    public bool IsUsb => DeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Liest Geraete und ihre Stromspar-Einstellungen. Wird von der Wartungsansicht und den Diagnose-Pruefungen gemeinsam genutzt.</summary>
public static class DeviceScanner
{
    /// <summary>
    /// Geraeteklassen, bei denen "Neu einstecken" gefahrlos ist. Systemnahes (Prozessor, Festplatten, Grafik)
    /// bleibt bewusst aussen vor.
    /// </summary>
    private static readonly Regex SafeToRestart = new(
        "^(Camera|Image|Media|AudioEndpoint|USB|Bluetooth|Net|HIDClass|Mouse|Keyboard|Printer|WPD)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Stromsparen zaehlt nur bei USB-Verteilern, USB-Geraeten, Tonchips und Netzwerkkarten.</summary>
    private static readonly Regex PowerRelevant = new(
        @"^(USB\\ROOT_HUB|USB\\VID_|HDAUDIO|PCI\\VEN_.*NET)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Was PC Helper abschaltet: Tonchips bleiben unberuehrt, deren Stromsparen ist meist gewollt.</summary>
    private static readonly Regex PowerChangeable = new(
        @"^(USB\\ROOT_HUB|USB\\VID_|PCI\\VEN_.*NET)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object CacheLock = new();
    private static IReadOnlyList<PnpDevice>? _cache;
    private static DateTime _cacheTime;
    private static List<(string Instance, bool Enabled)>? _powerRows;
    private static DateTime _powerRowsTime;

    /// <summary>Alle Geraete. Ein kurzer Zwischenspeicher verhindert, dass mehrere Pruefungen dasselbe erneut abfragen.</summary>
    public static IReadOnlyList<PnpDevice> ReadDevices(bool fresh = false)
    {
        lock (CacheLock)
        {
            if (!fresh && _cache is not null && DateTime.Now - _cacheTime < TimeSpan.FromSeconds(20)) return _cache;

            _cache = Wmi.Query("SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, Status FROM Win32_PnPEntity")
                .Select(r => new PnpDevice(r.Str("Name"), r.Str("DeviceID"), r.Str("PNPClass"),
                    r.Int("ConfigManagerErrorCode") ?? 0, r.Str("Status")))
                .Where(d => d.DeviceId.Length > 0)
                .ToList();
            _cacheTime = DateTime.Now;
            return _cache;
        }
    }

    public static PnpDevice? Find(string deviceId)
        => ReadDevices(fresh: true).FirstOrDefault(d => d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));

    public static bool CanRestart(PnpDevice d) => d.ErrorCode != 45 && SafeToRestart.IsMatch(d.DeviceClass);

    /// <summary>
    /// "Unbekanntes USB-Geraet" mit VID_0000 heisst: Der Anschluss hat es nicht geschafft, das Geraet sauber anzumelden.
    /// Genau der Zustand, in dem eine Kamera landet, die aus dem Stromsparen nicht aufwacht.
    /// </summary>
    public static bool IsDeadUsbEntry(PnpDevice d)
        => d.DeviceId.Contains("VID_0000", StringComparison.OrdinalIgnoreCase)
           || d.Name.Contains("Unbekanntes USB-Ger", StringComparison.OrdinalIgnoreCase)
           || d.Name.Contains("Unknown USB Device", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<PnpDevice> DeadUsbEntries(bool fresh = false)
        => ReadDevices(fresh).Where(IsDeadUsbEntry).ToList();

    public static IReadOnlyList<PnpDevice> Cameras(bool fresh = false)
        => ReadDevices(fresh)
            .Where(d => d.DeviceClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) && d.Name.Length > 0)
            .ToList();

    /// <summary>Programme, die die Kamera gerade belegen: Start gesetzt, Stop steht auf 0.</summary>
    public static IReadOnlyList<string> CameraUsers()
    {
        const string store = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        var users = new List<string>();

        try
        {
            foreach (var branch in new[] { store + @"\NonPackaged", store })
            {
                using var root = Registry.CurrentUser.OpenSubKey(branch);
                if (root is null) continue;

                foreach (var app in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(app);
                    if (key is null) continue;

                    var start = key.GetValue("LastUsedTimeStart") as long?;
                    var stop = key.GetValue("LastUsedTimeStop") as long?;
                    if (start is > 0 && stop == 0)
                    {
                        var name = app.Split('#')[^1];
                        if (name.Length > 0 && name != "NonPackaged") users.Add(name);
                    }
                }
            }
        }
        catch { /* Schluessel nicht lesbar: dann eben keine Angabe */ }

        return users.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Geraete, bei denen im Geraete-Manager das Haekchen "Computer kann das Geraet ausschalten, um Energie zu sparen"
    /// gesetzt ist - eine zweite Stromspar-Ebene, unabhaengig vom Energieplan. Ohne Administratorrechte liefert Windows
    /// hier unter Umstaenden nichts; <paramref name="readable"/> unterscheidet das von "es ist wirklich keins gesetzt".
    /// </summary>
    public static IReadOnlyList<string> PowerSavingDevices(out bool readable, bool onlyChangeable = false, bool fresh = false)
    {
        // Die Abfrage bricht ohne Administratorrechte bei einzelnen Geraeten mit "Ungueltiges Objekt" ab und liefert dann
        // nur, was bis dahin gelesen wurde. Sie ist langsam und laut im Protokoll - deshalb wie die Geraete kurz zwischenspeichern.
        List<(string Instance, bool Enabled)> rows;
        lock (CacheLock)
        {
            if (fresh || _powerRows is null || DateTime.Now - _powerRowsTime > TimeSpan.FromSeconds(20))
            {
                _powerRows = Wmi.Query("SELECT InstanceName, Enable FROM MSPower_DeviceEnable", @"root\wmi")
                    .Select(r => (r.Str("InstanceName"), r.Bool("Enable") == true))
                    .ToList();
                _powerRowsTime = DateTime.Now;
                Core.Log.Info($"Geraete-Stromsparen: {_powerRows.Count} Eintraege gelesen, {_powerRows.Count(r => r.Enabled)} davon mit Haekchen.");
            }
            rows = _powerRows;
        }

        readable = rows.Count > 0;
        var pattern = onlyChangeable ? PowerChangeable : PowerRelevant;
        return rows.Where(r => r.Enabled && pattern.IsMatch(r.Instance)).Select(r => r.Instance).ToList();
    }

    /// <summary>Kurzname zu einer WMI-Instanz wie <c>USB\VID_046D&amp;PID_C52B\5&amp;...</c>.</summary>
    public static string ShortName(string instanceName)
    {
        var parts = instanceName.Split('\\');
        return parts.Length > 1 ? parts[1] : instanceName;
    }
}
