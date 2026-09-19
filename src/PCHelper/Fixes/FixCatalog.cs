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

    /// <summary>Warum das gegen das jeweilige Problem hilft.</summary>
    public required string Why { get; init; }

    public FixRisk Risk { get; init; } = FixRisk.Gering;
    public bool NeedsReboot { get; init; }

    /// <summary>
    /// Braucht die Aenderung Administratorrechte? Einstellungen des angemeldeten
    /// Benutzers (HKCU) ausdruecklich nicht - sie muessen sogar ohne Elevation
    /// laufen, weil sonst der Benutzerzweig eines anderen Kontos getroffen wuerde.
    /// </summary>
    public bool RequiresAdmin { get; init; } = true;

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
    private const string ConsentKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>Geraeteklasse "Netzwerkadapter" im Geraete-Manager.</summary>
    private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Setzt PnPCapabilities fuer alle Netzwerkadapter (0x18 = Energiesparen aus).</summary>
    private static string NetworkPowerCommand(int value) =>
        "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
        $"Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{NetClassGuid}' | " +
        "Where-Object { $_.PSChildName -match '^[0-9]{4}$' } | " +
        $"ForEach-Object {{ New-ItemProperty -Path $_.PSPath -Name PnPCapabilities -Value {value} " +
        "-PropertyType DWord -Force | Out-Null }\"";

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

        // ---------------- Ton, Mikrofon und Kamera ----------------

        new()
        {
            Id = "mic-privacy-allow",
            Title = "Mikrofonzugriff fuer alle Programme erlauben",
            Category = "Ton",
            Description = "Setzt die Windows-Datenschutzeinstellung fuer das Mikrofon auf 'erlaubt' - " +
                          "sowohl fuer Apps als auch fuer klassische Desktop-Programme.",
            Why = "Das ist der haeufigste Grund dafuer, dass ein Mikrofon in Windows einwandfrei aussieht, in " +
                  "Discord, Teams oder OBS aber schlicht nicht auftaucht. Der entscheidende Schalter " +
                  "('Desktop-Apps duerfen zugreifen') steht unterhalb einer langen App-Liste und wird deshalb " +
                  "fast immer uebersehen.\n\n" +
                  "Die Aenderung betrifft nur das angemeldete Benutzerkonto und laeuft bewusst ohne Adminrechte.\n\n" +
                  "Die Ruecknahme entfernt die Eintraege wieder, setzt aber ausdruecklich kein 'verweigert' - " +
                  "ein versehentlicher Klick auf 'Zuruecknehmen' legt das Mikrofon also nicht still. " +
                  "Wer den Zugriff wirklich sperren will, macht das in den Windows-Einstellungen.",
            Risk = FixRisk.Gering,
            RequiresAdmin = false,
            Commands = new[]
            {
                $"reg add \"{ConsentKey}\\microphone\" /v Value /t REG_SZ /d Allow /f",
                $"reg add \"{ConsentKey}\\microphone\\NonPackaged\" /v Value /t REG_SZ /d Allow /f",
            },
            RevertCommands = new[]
            {
                $"reg delete \"{ConsentKey}\\microphone\" /v Value /f",
                $"reg delete \"{ConsentKey}\\microphone\\NonPackaged\" /v Value /f",
            },
        },

        new()
        {
            Id = "camera-privacy-allow",
            Title = "Kamerazugriff fuer alle Programme erlauben",
            Category = "Geraete",
            Description = "Setzt die Windows-Datenschutzeinstellung fuer die Kamera auf 'erlaubt' - " +
                          "fuer Apps und fuer klassische Desktop-Programme.",
            Why = "Wie beim Mikrofon: Die Kamera ist vorhanden und funktioniert, Windows reicht sie aber nicht " +
                  "an das Programm durch. Betroffene Anwendungen melden dann meist 'keine Kamera gefunden'.\n\n" +
                  "Die Ruecknahme entfernt die Eintraege wieder und setzt kein 'verweigert'.",
            Risk = FixRisk.Gering,
            RequiresAdmin = false,
            Commands = new[]
            {
                $"reg add \"{ConsentKey}\\webcam\" /v Value /t REG_SZ /d Allow /f",
                $"reg add \"{ConsentKey}\\webcam\\NonPackaged\" /v Value /t REG_SZ /d Allow /f",
            },
            RevertCommands = new[]
            {
                $"reg delete \"{ConsentKey}\\webcam\" /v Value /f",
                $"reg delete \"{ConsentKey}\\webcam\\NonPackaged\" /v Value /f",
            },
        },

        new()
        {
            Id = "restart-audio-services",
            Title = "Audiodienste neu starten",
            Category = "Ton",
            Description = "Startet 'Windows-Audio' und die 'Audio-Geraetehandler' neu und stellt sicher, " +
                          "dass beide automatisch mit Windows starten.",
            Why = "Haengen diese Dienste, verschwinden schlagartig alle Wiedergabe- und Aufnahmegeraete - " +
                  "Programme melden dann 'kein Geraet gefunden', obwohl alles angeschlossen ist. Der Neustart " +
                  "der Dienste baut die komplette Geraeteliste neu auf und ersetzt in vielen Faellen den Neustart des Rechners.",
            Risk = FixRisk.Gering,
            Duration = "wenige Sekunden, der Ton setzt dabei kurz aus",
            Commands = new[]
            {
                "sc config Audiosrv start= auto",
                "sc config AudioEndpointBuilder start= auto",
                "net stop Audiosrv /y",
                "net stop AudioEndpointBuilder /y",
                "net start AudioEndpointBuilder",
                "net start Audiosrv",
            },
        },

        // ---------------- Netzwerk ----------------

        new()
        {
            Id = "flush-dns",
            Title = "DNS-Zwischenspeicher leeren",
            Category = "Netzwerk",
            Description = "Verwirft die zwischengespeicherten Namensaufloesungen und erneuert die IP-Adresse vom Router.",
            Why = "Wenn IP-Adressen erreichbar sind, Webadressen aber nicht, liegt es an der Namensaufloesung. " +
                  "Veraltete Eintraege im Zwischenspeicher zeigen dann auf Adressen, die es nicht mehr gibt - " +
                  "das fuehlt sich an wie 'kein Internet', obwohl die Leitung steht.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "ipconfig /flushdns",
                "ipconfig /release",
                "ipconfig /renew",
            },
        },

        new()
        {
            Id = "network-power-off",
            Title = "Energiesparen der Netzwerkadapter abschalten",
            Category = "Netzwerk",
            Description = "Entfernt bei allen Netzwerkadaptern die Erlaubnis, das Geraet zum Energiesparen abzuschalten " +
                          "(PnPCapabilities = 24).",
            Why = "Legt Windows den Netzwerkadapter im Leerlauf schlafen, reisst die Verbindung fuer ein bis zwei " +
                  "Sekunden ab. Das faellt beim Surfen kaum auf, wirft einen aber zuverlaessig aus Spielen und " +
                  "Videokonferenzen - und ist eine der haeufigsten Ursachen fuer 'die Verbindung bricht staendig kurz ab'.",
            Risk = FixRisk.Gering,
            NeedsReboot = true,
            Commands = new[] { NetworkPowerCommand(24) },
            RevertCommands = new[] { NetworkPowerCommand(0) },
        },

        new()
        {
            Id = "network-reset-stack",
            Title = "Netzwerkeinstellungen zuruecksetzen",
            Category = "Netzwerk",
            Description = "Setzt Winsock und den TCP/IP-Stapel auf die Werkseinstellung zurueck und fordert " +
                          "eine neue IP-Adresse an.",
            Why = "Raeumt Reste von VPN-Programmen, Proxy-Werkzeugen und fehlgeschlagenen Treiberinstallationen " +
                  "aus dem Netzwerkstapel. Der Standardschritt, wenn eine Verbindung besteht, aber trotzdem " +
                  "nichts durchgeht.",
            Risk = FixRisk.Mittel,
            NeedsReboot = true,
            Duration = "wenige Sekunden, danach ist ein Neustart noetig",
            Commands = new[]
            {
                "ipconfig /release",
                "ipconfig /flushdns",
                "netsh winsock reset",
                "netsh int ip reset",
                "ipconfig /renew",
            },
        },

        // ---------------- Leistung ----------------

        new()
        {
            Id = "power-high-performance",
            Title = "Energieplan auf Hoechstleistung stellen",
            Category = "Energie",
            Description = "Aktiviert den Windows-Energieplan 'Hoechstleistung'.",
            Why = "Nimmt die Taktabsenkung der CPU und einen Teil der Energiesparzustaende aus dem Spiel. " +
                  "Als Dauerloesung nicht noetig, zum Eingrenzen aber sehr nuetzlich: Verschwinden Aussetzer " +
                  "oder Traegheit damit, ist Energiesparen die Ursache und nicht die Hardware.",
            Risk = FixRisk.Gering,
            Commands = new[] { "powercfg /setactive SCHEME_MIN" },
            RevertCommands = new[] { "powercfg /setactive SCHEME_BALANCED" },
        },

        new()
        {
            Id = "power-balanced",
            Title = "Energieplan auf Ausbalanciert stellen",
            Category = "Energie",
            Description = "Aktiviert den Windows-Energieplan 'Ausbalanciert'.",
            Why = "Im Energiesparmodus drosselt Windows den Prozessor absichtlich, um Strom zu sparen. Auf einem Rechner, der " +
                  "spielen oder streamen soll, ist das eine unsichtbare Handbremse. 'Ausbalanciert' regelt die Leistung nach " +
                  "Bedarf hoch und runter.",
            Risk = FixRisk.Gering,
            Commands = new[] { "powercfg /setactive SCHEME_BALANCED" },
            RevertCommands = new[] { "powercfg /setactive SCHEME_MAX" },
        },

        // ---------------- Netzwerk: Umwege und Bremsen ----------------

        new()
        {
            Id = "proxy-off",
            Title = "Eingetragenen Proxy-Server entfernen",
            Category = "Netzwerk",
            Description = "Schaltet den Proxy in den Internetoptionen aus und entfernt den eingetragenen Server.",
            Why = "Jede Internetanfrage laeuft dann zuerst zu diesem Server. Ist er nicht erreichbar, wartet Windows bei jeder " +
                  "einzelnen Anfrage auf einen Timeout - das fuehlt sich an wie ein eingefrorener Rechner. Solche Eintraege " +
                  "bleiben oft von alter Software zurueck. Im Privathaushalt braucht man praktisch nie einen Proxy.\n\n" +
                  "Die Adresse des Servers geht dabei verloren. Wenn du sie noch brauchst, vorher notieren.",
            Risk = FixRisk.Mittel,
            RequiresAdmin = false,
            Commands = new[]
            {
                @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"" /v ProxyEnable /t REG_DWORD /d 0 /f",
                @"reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"" /v ProxyServer /f",
            },
        },

        new()
        {
            Id = "auto-proxy-off",
            Title = "Automatische Proxy-Suche abschalten",
            Category = "Netzwerk",
            Description = "Setzt 'Einstellungen automatisch erkennen' in den Internetoptionen auf aus.",
            Why = "Windows sucht bei jeder neuen Verbindung selbsttaetig nach einem Proxy-Server. Im Heimnetz gibt es keinen, " +
                  "also wartet es jedes Mal vergeblich, bis die Suche aufgibt. Das verzoegert spuerbar den ersten Seitenaufruf.",
            Risk = FixRisk.Gering,
            RequiresAdmin = false,
            Commands = new[]
            {
                @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"" /v AutoDetect /t REG_DWORD /d 0 /f",
            },
            RevertCommands = new[]
            {
                @"reg add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"" /v AutoDetect /t REG_DWORD /d 1 /f",
            },
        },

        new()
        {
            Id = "tcp-autotuning-normal",
            Title = "TCP-Empfangsfenster auf 'normal' zuruecksetzen",
            Category = "Netzwerk",
            Description = "Setzt die automatische Abstimmung des TCP-Empfangsfensters auf den Windows-Standard.",
            Why = "Steht die Abstimmung auf 'disabled' oder 'restricted', darf Windows nur kleine Datenmengen am Stueck annehmen. " +
                  "Auf schnellen Leitungen bricht der Durchsatz dadurch stark ein. Der Wert wird gern von aelteren " +
                  "'Tuning-Programmen' verstellt.",
            Risk = FixRisk.Gering,
            Commands = new[] { "netsh int tcp set global autotuninglevel=normal" },
        },

        new()
        {
            Id = "wlan-off-with-cable",
            Title = "WLAN abschalten, solange ein Netzwerkkabel steckt",
            Category = "Netzwerk",
            Description = "Schaltet die WLAN-Adapter ab - aber nur, wenn ein aktives Netzwerkkabel gefunden wird.",
            Why = "Haengt der Rechner gleichzeitig per Kabel und per WLAN am selben Router, muss Windows sich staendig entscheiden, " +
                  "und manche Programme erwischen die langsamere Leitung. Das Kabel ist immer schneller und stabiler.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                "$kabel = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Wi-?Fi|Wireless|WLAN' }; " +
                "if (-not $kabel) { Write-Error 'Kein aktives Netzwerkkabel gefunden - das WLAN bleibt an.'; exit 1 }; " +
                "Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN' } | " +
                "Disable-NetAdapter -Confirm:$false\"",
            },
            RevertCommands = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                "Get-NetAdapter -Physical | Where-Object { $_.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN' } | " +
                "Enable-NetAdapter -Confirm:$false\"",
            },
        },

        new()
        {
            Id = "ghost-adapters-remove",
            Title = "Netzwerkkarten-Karteileichen entfernen",
            Category = "Netzwerk",
            Description = "Entfernt Netzwerkkarten, die in Windows eingetragen sind, aber nicht mehr im Rechner stecken.",
            Why = "Reste von alten Treibern, VPN-Programmen oder USB-Adaptern. Sie bremsen nichts direkt, machen aber die " +
                  "Netzwerkeinstellungen unuebersichtlich und koennen die Reihenfolge durcheinanderbringen. Steckt das Geraet " +
                  "wieder, richtet Windows es von selbst neu ein.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                "Get-NetAdapter | Where-Object { $_.Status -eq 'Not Present' } | Remove-NetAdapter -Confirm:$false\"",
            },
        },

        // ---------------- Geraete ----------------

        new()
        {
            Id = "device-power-saving-off",
            Title = "Stromsparen einzelner USB-Geraete und Netzwerkkarten abschalten",
            Category = "Geraete",
            Description = "Entfernt im Geraete-Manager das Haekchen 'Computer kann das Geraet ausschalten, um Energie zu sparen' " +
                          "bei USB-Verteilern, USB-Geraeten und Netzwerkkarten.",
            Why = "Das ist eine zweite Stromspar-Ebene, unabhaengig vom Energieplan - deshalb bringt es oft nichts, nur den " +
                  "Energieplan umzustellen. Genau das verhindert, dass Webcams, Mikrofone und externe Platten mitten im Betrieb " +
                  "verschwinden. Der eingesparte Strom liegt im Bereich einer Gluehbirne, die kurz blinkt.",
            Risk = FixRisk.Gering,
            Commands = new[]
            {
                "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                "Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Enable -and $_.InstanceName -match '^(USB\\\\ROOT_HUB|USB\\\\VID_|PCI\\\\VEN_.*NET)' } | " +
                "ForEach-Object { $_.Enable = $false; Set-CimInstance -InputObject $_ }\"",
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
        return Shell.RunBatchAsync(commands, (revert ? "revert-" : "") + fix.Id, fix.RequiresAdmin);
    }
}
