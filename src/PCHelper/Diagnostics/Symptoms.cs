using System.Text;

namespace PCHelper.Diagnostics;

/// <summary>
/// Ein Problem so, wie der Nutzer es beschreibt ("mein Mikrofon wird nicht erkannt").
///
/// Das Symptom ist die Bruecke zwischen Alltagssprache und Technik: es legt fest,
/// welche Ursachenbereiche ueberhaupt in Frage kommen, welche Pruefungen deshalb
/// laufen muessen und welche Reparaturen und Werkzeuge dazu passen.
/// </summary>
public sealed class Symptom
{
    public required string Id { get; init; }

    /// <summary>Themenbereich fuer die Gruppierung in der Oberflaeche.</summary>
    public required string Group { get; init; }

    /// <summary>Beschreibung aus Nutzersicht - genau so, wie man es erzaehlen wuerde.</summary>
    public required string Title { get; init; }

    /// <summary>Was typischerweise dahintersteckt, in zwei bis drei Saetzen.</summary>
    public required string Description { get; init; }

    /// <summary>Begriffe fuer die Freitextsuche (Umgangssprache ausdruecklich erwuenscht).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>Vorabgewichtung der Ursachenbereiche (0..1). Steuert Pruefauswahl und Verdachtsliste.</summary>
    public required IReadOnlyDictionary<Cause, double> Causes { get; init; }

    /// <summary>Was man in zwei Minuten selbst pruefen kann, bevor die Technik uebernimmt.</summary>
    public IReadOnlyList<string> FirstSteps { get; init; } = Array.Empty<string>();

    /// <summary>Passende Eintraege aus dem <see cref="Fixes.FixCatalog"/>.</summary>
    public IReadOnlyList<string> FixIds { get; init; } = Array.Empty<string>();

    /// <summary>Passende Werkzeuge (IDs aus der Werkzeugliste).</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = Array.Empty<string>();

    /// <summary>Die drei staerksten Ursachenbereiche als Klartext.</summary>
    public string CauseSummary => string.Join(" | ", Causes
        .OrderByDescending(kv => kv.Value)
        .Take(3)
        .Select(kv => CauseInfo.Title(kv.Key)));

    public override string ToString() => Title;
}

/// <summary>
/// Alle bekannten Symptome. Neue Eintraege einfach hier ergaenzen - Pruefauswahl,
/// Verdachtsliste, Reparatur- und Werkzeugvorschlaege ergeben sich automatisch
/// aus <see cref="Symptom.Causes"/>.
/// </summary>
public static class SymptomCatalog
{
    public const string GroupDisplay = "Bild und Anzeige";
    public const string GroupAudio = "Ton und Mikrofon";
    public const string GroupNetwork = "Internet und Netzwerk";
    public const string GroupStability = "Leistung und Abstuerze";
    public const string GroupDevices = "Geraete und Anschluesse";
    public const string GroupWindows = "Windows und Datentraeger";

    public static IReadOnlyList<Symptom> All { get; } = new List<Symptom>
    {
        // ================= Bild und Anzeige =================
        new()
        {
            Id = "display-black-random",
            Group = GroupDisplay,
            Title = "Der Bildschirm wird ploetzlich schwarz, der Ton laeuft weiter",
            Description =
                "Das klassische Muster fuer einen Abriss der Signalstrecke oder einen Treiber-Reset: Der Rechner " +
                "laeuft weiter (Musik spielt, Luefter drehen), nur das Bild ist weg. Haeufig hilft nur ein Neustart.",
            Keywords = new[]
            {
                "schwarz", "schwarzbild", "blackscreen", "kein bild", "bild weg", "monitor aus", "kein signal",
                "displayport", "hdmi", "ton laeuft weiter", "neu starten", "bildschirm dunkel",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.DisplayLink] = 1.0, [Cause.GpuDriver] = 0.9, [Cause.GpuHardware] = 0.8, [Cause.PowerSettings] = 0.6,
                [Cause.Memory] = 0.4, [Cause.PowerSupply] = 0.4, [Cause.Software] = 0.3, [Cause.Thermal] = 0.3,
            },
            FirstSteps = new[]
            {
                "Beim naechsten Auftreten pruefen: Meldet der Monitor 'kein Signal' oder bleibt er einfach dunkel? Das trennt Kabel/Monitor von Grafikkarte.",
                "Laeuft der Ton weiter? Dann ist es kein Absturz, sondern nur die Bildausgabe.",
                "Testweise ein anderes Kabel oder einen anderen Anschluss (HDMI statt DisplayPort) verwenden.",
                "Testweise 60 Hz statt der maximalen Bildwiederholrate einstellen.",
            },
            FixIds = new[] { "display-timeout-never", "fast-startup-off", "hags-off", "aspm-off" },
            ToolIds = new[] { "device-manager", "display-settings", "reliability", "dxdiag" },
        },
        new()
        {
            Id = "display-no-signal-boot",
            Group = GroupDisplay,
            Title = "Beim Einschalten oder Aufwachen kommt gar kein Bild",
            Description =
                "Der Rechner startet hoerbar, der Bildschirm bleibt aber dunkel. Meistens steckt eine falsch " +
                "gespeicherte Monitorkonfiguration, der Windows-Schnellstart oder ein Aufwachproblem dahinter.",
            Keywords = new[]
            {
                "kein bild beim start", "startet nicht", "kein signal", "aufwachen", "standby", "ruhezustand",
                "schwarz nach start", "bootet ohne bild", "monitor bleibt dunkel",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.DisplayLink] = 0.9, [Cause.PowerSettings] = 0.8, [Cause.GpuDriver] = 0.7,
                [Cause.Bios] = 0.4, [Cause.OperatingSystem] = 0.3,
            },
            FirstSteps = new[]
            {
                "Monitor komplett stromlos machen (Stecker ziehen, 30 Sekunden warten) - das setzt die Monitor-Elektronik zurueck.",
                "Kabel am anderen Anschluss der Grafikkarte testen, nicht am Mainboard-Ausgang.",
                "Pruefen, ob der Rechner nur nicht aufwacht: Feststelltaste druecken - leuchtet die LED, laeuft Windows.",
            },
            FixIds = new[] { "fast-startup-off", "reset-network-display-cache", "sleep-never" },
            ToolIds = new[] { "display-settings", "power-options", "event-viewer" },
        },
        new()
        {
            Id = "display-flicker",
            Group = GroupDisplay,
            Title = "Das Bild flackert, ruckelt oder zeigt Streifen",
            Description =
                "Kurze Aussetzer, Flackern beim Scrollen oder farbige Artefakte deuten auf die Signalstrecke " +
                "(Kabel, Bildwiederholrate, DSC) oder auf eine ueberhitzte bzw. uebertaktete Grafikkarte hin.",
            Keywords = new[]
            {
                "flackern", "flimmern", "streifen", "artefakte", "ruckeln", "bild zuckt", "pixelfehler",
                "gruene streifen", "bild springt",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.DisplayLink] = 0.9, [Cause.GpuDriver] = 0.8, [Cause.Thermal] = 0.5, [Cause.Software] = 0.4,
            },
            FirstSteps = new[]
            {
                "Bildwiederholrate testweise auf 60 Hz stellen. Verschwindet das Flackern, ist die Signalstrecke ueberfordert.",
                "Anderes, zertifiziertes Kabel testen - bei hohen Aufloesungen ist das die haeufigste Ursache.",
                "Uebertaktungswerkzeuge (Afterburner & Co.) beenden und beobachten.",
            },
            FixIds = new[] { "hags-off", "tdr-reset" },
            ToolIds = new[] { "display-settings", "dxdiag", "hwinfo" },
        },

        // ================= Ton und Mikrofon =================
        new()
        {
            Id = "mic-not-in-app",
            Group = GroupAudio,
            Title = "Mein Mikrofon ist in Windows da, aber eine App (z. B. Discord) findet es nicht",
            Description =
                "Windows kennt das Geraet, die Anwendung sieht es trotzdem nicht. In den allermeisten Faellen " +
                "blockiert der Windows-Datenschutz den Zugriff, die App ist auf ein anderes Geraet eingestellt, " +
                "oder eine andere Anwendung belegt das Mikrofon exklusiv.",
            Keywords = new[]
            {
                "mikrofon", "mikro", "mic", "discord", "teams", "zoom", "obs", "headset", "wird nicht erkannt",
                "kein mikrofon", "findet mikrofon nicht", "app hoert mich nicht", "niemand hoert mich",
                "aufnahmegeraet", "sprachchat",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.AppPermission] = 1.0, [Cause.Microphone] = 0.9, [Cause.AudioDevice] = 0.5,
                [Cause.UsbDevice] = 0.4, [Cause.DeviceDriver] = 0.3, [Cause.Software] = 0.3,
            },
            FirstSteps = new[]
            {
                "Windows-Einstellungen > Datenschutz > Mikrofon: Sowohl 'Mikrofonzugriff' als auch 'Desktop-Apps duerfen auf das Mikrofon zugreifen' muessen an sein - der zweite Schalter ganz unten wird fast immer uebersehen.",
                "In der App selbst das Eingabegeraet ausdruecklich auswaehlen, statt 'Standard' stehen zu lassen.",
                "Andere Anwendungen schliessen, die das Mikrofon halten koennten (Sprachassistent, Aufnahmesoftware, Spiel im Hintergrund).",
                "Sound-Einstellungen > Aufnahme: Zeigt der Pegelausschlag beim Sprechen? Dann ist das Geraet in Ordnung und es ist eindeutig ein App- oder Berechtigungsproblem.",
            },
            FixIds = new[] { "mic-privacy-allow", "restart-audio-services", "usb-suspend-off" },
            ToolIds = new[] { "mic-privacy", "sound-devices", "sound-settings", "device-manager", "taskmgr" },
        },
        new()
        {
            Id = "mic-missing-system",
            Group = GroupAudio,
            Title = "Mein Mikrofon fehlt komplett in Windows",
            Description =
                "Das Geraet taucht in den Sound-Einstellungen gar nicht auf. Dann ist es entweder deaktiviert, " +
                "als 'nicht angeschlossen' gemeldet, der Treiber fehlt - oder es haengt an einem Anschluss, " +
                "den Windows abgeschaltet hat.",
            Keywords = new[]
            {
                "mikrofon fehlt", "kein aufnahmegeraet", "mikrofon nicht vorhanden", "mikrofon deaktiviert",
                "klinke", "headset nicht erkannt", "usb mikrofon", "webcam mikrofon",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Microphone] = 1.0, [Cause.DeviceDriver] = 0.8, [Cause.UsbDevice] = 0.6,
                [Cause.AudioDevice] = 0.5, [Cause.AppPermission] = 0.3,
            },
            FirstSteps = new[]
            {
                "In den Sound-Einstellungen unter 'Aufnahme' mit der rechten Maustaste 'Deaktivierte Geraete anzeigen' einschalten - deaktivierte Mikrofone sind sonst unsichtbar.",
                "USB-Mikrofon an einem anderen Anschluss testen, moeglichst direkt am Mainboard statt am Hub oder Frontpanel.",
                "Im Geraete-Manager unter 'Audioeingaenge und -ausgaenge' nach Geraeten mit Ausrufezeichen suchen.",
            },
            FixIds = new[] { "restart-audio-services", "usb-suspend-off" },
            ToolIds = new[] { "sound-devices", "device-manager", "mic-privacy", "sound-settings" },
        },
        new()
        {
            Id = "audio-no-sound",
            Group = GroupAudio,
            Title = "Kein Ton, obwohl alles angeschlossen ist",
            Description =
                "Lautsprecher oder Kopfhoerer bleiben stumm. Meist ist ein falsches Standardgeraet gesetzt, " +
                "das richtige Geraet ist deaktiviert, oder der Audiodienst von Windows haengt.",
            Keywords = new[]
            {
                "kein ton", "kein sound", "stumm", "lautsprecher", "kopfhoerer", "boxen", "ton weg",
                "keine wiedergabe", "audio funktioniert nicht", "hdmi ton",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.AudioDevice] = 1.0, [Cause.DeviceDriver] = 0.6, [Cause.UsbDevice] = 0.4,
                [Cause.OperatingSystem] = 0.3,
            },
            FirstSteps = new[]
            {
                "Lautstaerkesymbol anklicken und pruefen, welches Geraet als Ausgabe gewaehlt ist - bei angeschlossenem Monitor schaltet Windows den Ton gern auf HDMI um.",
                "In den Sound-Einstellungen 'Deaktivierte Geraete anzeigen' einschalten und das gewuenschte Geraet wieder aktivieren.",
                "Kopfhoerer an einem anderen Anschluss oder Geraet gegentesten.",
            },
            FixIds = new[] { "restart-audio-services" },
            ToolIds = new[] { "sound-devices", "sound-settings", "device-manager", "troubleshoot" },
        },
        new()
        {
            Id = "audio-crackle",
            Group = GroupAudio,
            Title = "Der Ton knackt, stottert oder setzt kurz aus",
            Description =
                "Aussetzer im Ton entstehen fast immer durch Latenzspitzen im System: ein Treiber blockiert zu lange, " +
                "USB-Anschluesse werden schlafen gelegt, oder eine Hintergrundlast bremst die Audioverarbeitung aus.",
            Keywords = new[]
            {
                "knacken", "knistern", "stottern", "ton haengt", "aussetzer", "roboterstimme", "verzerrt",
                "audio ruckelt", "crackling",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.AudioDevice] = 0.8, [Cause.UsbDevice] = 0.7, [Cause.PowerSettings] = 0.6,
                [Cause.Cpu] = 0.5, [Cause.DeviceDriver] = 0.5, [Cause.Software] = 0.3,
            },
            FirstSteps = new[]
            {
                "USB-Audio testweise an einen anderen Anschluss stecken - moeglichst nicht gemeinsam mit Webcam oder externer Platte an einem Hub.",
                "Waehrend der Aussetzer im Task-Manager pruefen, ob ein Prozess die CPU auslastet.",
                "Exklusivmodus fuer das Audiogeraet in den erweiterten Sound-Einstellungen testweise abschalten.",
            },
            FixIds = new[] { "usb-suspend-off", "restart-audio-services", "power-high-performance" },
            ToolIds = new[] { "sound-settings", "taskmgr", "power-options" },
        },

        // ================= Internet und Netzwerk =================
        new()
        {
            Id = "net-no-internet",
            Group = GroupNetwork,
            Title = "Keine Internetverbindung, obwohl das Kabel oder WLAN verbunden ist",
            Description =
                "Windows zeigt eine Verbindung, es geht aber nichts durch. Der Unterschied zwischen 'kein Netzwerk', " +
                "'keine IP-Adresse' und 'DNS antwortet nicht' entscheidet, wo man suchen muss - genau das prueft die Diagnose.",
            Keywords = new[]
            {
                "kein internet", "internet geht nicht", "offline", "keine verbindung", "netzwerk", "lan",
                "wlan", "wifi", "router", "dns", "ip adresse", "eingeschraenkte konnektivitaet",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Network] = 1.0, [Cause.Wifi] = 0.6, [Cause.DeviceDriver] = 0.5,
                [Cause.Software] = 0.4, [Cause.OperatingSystem] = 0.3,
            },
            FirstSteps = new[]
            {
                "Router und Modem fuer 30 Sekunden vom Strom trennen - loest einen erstaunlich grossen Teil aller Faelle.",
                "Pruefen, ob andere Geraete im selben Netz online sind. Wenn nicht, liegt es nicht am PC.",
                "VPN-Software und Drittanbieter-Firewalls testweise beenden - sie kapern den Netzwerkstapel.",
            },
            FixIds = new[] { "network-reset-stack", "flush-dns" },
            ToolIds = new[] { "network-settings", "troubleshoot", "device-manager", "event-viewer" },
        },
        new()
        {
            Id = "net-drops",
            Group = GroupNetwork,
            Title = "Die Verbindung bricht immer wieder kurz ab",
            Description =
                "Kurze Aussetzer beim Spielen, in Videokonferenzen oder beim Streaming. Typische Ursachen: " +
                "Energiesparen am Netzwerkadapter, ein instabiler Treiber oder Funkstoerungen im WLAN.",
            Keywords = new[]
            {
                "verbindung bricht ab", "disconnect", "aussetzer", "unterbrechung", "verbindungsabbruch",
                "wlan bricht ab", "lan bricht ab", "kurz offline", "lag",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Network] = 1.0, [Cause.Wifi] = 0.8, [Cause.PowerSettings] = 0.7, [Cause.DeviceDriver] = 0.6,
            },
            FirstSteps = new[]
            {
                "Im Geraete-Manager beim Netzwerkadapter unter 'Energieverwaltung' den Haken 'Computer kann das Geraet ausschalten' entfernen.",
                "Bei WLAN: Abstand zum Router und Kanalwahl pruefen, testweise 5 GHz statt 2,4 GHz oder umgekehrt.",
                "Testweise per Kabel verbinden - das trennt Funkprobleme von allem anderen.",
            },
            FixIds = new[] { "network-power-off", "network-reset-stack" },
            ToolIds = new[] { "device-manager", "network-settings", "event-viewer" },
        },
        new()
        {
            Id = "net-slow",
            Group = GroupNetwork,
            Title = "Das Internet ist langsam oder der Ping ist hoch",
            Description =
                "Niedrige Geschwindigkeit oder hohe Latenz koennen am Anschluss liegen - aber genauso an einem " +
                "Adapter, der nur mit 100 Mbit statt 1 Gbit ausgehandelt hat, an WLAN-Stoerungen oder an Hintergrunddownloads.",
            Keywords = new[]
            {
                "langsam", "lahm", "ping", "latenz", "lags", "geschwindigkeit", "speed", "downloadrate",
                "ruckelt online", "hoher ping",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Network] = 1.0, [Cause.Wifi] = 0.7, [Cause.Software] = 0.4, [Cause.Cpu] = 0.3,
            },
            FirstSteps = new[]
            {
                "Im Task-Manager unter 'Leistung' pruefen, mit welcher Verbindungsgeschwindigkeit der Adapter laeuft (100 Mbit deutet auf ein defektes Kabel oder eine schlechte Steckverbindung hin).",
                "Windows-Update und Spiele-Launcher pruefen - Hintergrunddownloads fressen die Leitung leise auf.",
                "Bei WLAN einmal per Kabel gegentesten.",
            },
            FixIds = new[] { "flush-dns", "network-power-off" },
            ToolIds = new[] { "taskmgr", "network-settings", "resource-monitor" },
        },

        // ================= Leistung und Abstuerze =================
        new()
        {
            Id = "perf-slow",
            Group = GroupStability,
            Title = "Der PC ist allgemein langsam geworden",
            Description =
                "Lange Startzeiten, traege Fenster, alles haengt kurz. Ursachen sind meist Autostart-Ballast, " +
                "ein volles oder muedes Systemlaufwerk, zu wenig freier Arbeitsspeicher oder eine gedrosselte CPU.",
            Keywords = new[]
            {
                "langsam", "traege", "haengt", "lahm", "dauert lange", "reagiert nicht", "startet langsam",
                "performance", "leistung schlecht",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Cpu] = 0.9, [Cause.Storage] = 0.8, [Cause.Memory] = 0.6, [Cause.Software] = 0.6,
                [Cause.Thermal] = 0.5, [Cause.PowerSettings] = 0.4, [Cause.OperatingSystem] = 0.4,
            },
            FirstSteps = new[]
            {
                "Task-Manager oeffnen und nach Spalte 'CPU', dann nach 'Datentraeger' sortieren - der Bremser steht meist ganz oben.",
                "Autostart ausmisten: Task-Manager > Autostart, alles ausschalten, was nicht wirklich beim Start laufen muss.",
                "Freien Speicherplatz auf C: pruefen - unter 10 % wird Windows spuerbar langsamer.",
            },
            FixIds = new[] { "power-high-performance", "sfc-dism" },
            ToolIds = new[] { "taskmgr", "resource-monitor", "cleanmgr", "power-options" },
        },
        new()
        {
            Id = "perf-cpu-high",
            Group = GroupStability,
            Title = "Die CPU-Last ist hoch und der Luefter dreht auf, ohne dass ich etwas mache",
            Description =
                "Dauerlast im Leerlauf hat fast immer einen konkreten Verursacher: Windows-Suche, Update-Dienste, " +
                "ein haengender Prozess oder Hintergrundsoftware. Bleibt die Last hoch, drosselt die CPU wegen Waerme.",
            Keywords = new[]
            {
                "cpu last", "100 prozent", "luefter laut", "auslastung", "heiss", "prozessor", "im leerlauf",
                "wird warm", "laut", "dreht hoch",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Cpu] = 1.0, [Cause.Thermal] = 0.8, [Cause.Software] = 0.7, [Cause.OperatingSystem] = 0.4,
            },
            FirstSteps = new[]
            {
                "Task-Manager nach CPU sortieren und den Prozess benennen - ohne Namen keine Diagnose.",
                "Pruefen, ob gerade ein Windows-Update oder eine Virensuche laeuft. Beides ist normal und hoert von selbst auf.",
                "Temperaturen mitlesen: Drosselt die CPU bereits, ist Kuehlung das eigentliche Thema.",
            },
            FixIds = new[] { "power-high-performance" },
            ToolIds = new[] { "taskmgr", "resource-monitor", "hwinfo" },
        },
        new()
        {
            Id = "crash-bluescreen",
            Group = GroupStability,
            Title = "Ich bekomme Bluescreens",
            Description =
                "Ein Bluescreen ist die Notbremse von Windows. Der Stopcode und der beteiligte Treiber stehen im " +
                "Ereignisprotokoll und im Absturzabbild - daraus laesst sich die Ursache meistens sauber eingrenzen.",
            Keywords = new[]
            {
                "bluescreen", "blauer bildschirm", "bsod", "absturz", "stopcode", "kernel", "dump",
                "windows stuerzt ab", "neustart mit fehler",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Memory] = 0.9, [Cause.DeviceDriver] = 0.8, [Cause.GpuDriver] = 0.6, [Cause.Storage] = 0.5,
                [Cause.OperatingSystem] = 0.5, [Cause.PowerSupply] = 0.4, [Cause.Thermal] = 0.3, [Cause.Bios] = 0.3,
            },
            FirstSteps = new[]
            {
                "Den genauen Stopcode notieren (z. B. IRQL_NOT_LESS_OR_EQUAL) - er engt die Ursache stark ein.",
                "Ueberlegen, was zuletzt geaendert wurde: neuer Treiber, neue Hardware, neues Programm.",
                "EXPO/XMP im BIOS testweise deaktivieren, wenn der Speicher uebertaktet laeuft.",
            },
            FixIds = new[] { "sfc-dism", "fast-startup-off" },
            ToolIds = new[] { "reliability", "event-viewer", "mdsched", "memtest" },
        },
        new()
        {
            Id = "crash-restart",
            Group = GroupStability,
            Title = "Der PC startet ohne Vorwarnung neu oder geht einfach aus",
            Description =
                "Wenn der Rechner ohne Bluescreen komplett ausgeht, ist das ein Hardware-Ereignis: Stromversorgung, " +
                "Ueberhitzung oder ein Schutzabschalten. Windows protokolliert das als 'Kernel-Power 41'.",
            Keywords = new[]
            {
                "startet neu", "geht aus", "reboot", "schaltet ab", "stromausfall", "kernel power",
                "einfach aus", "beim spielen aus", "shutdown",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.PowerSupply] = 1.0, [Cause.Thermal] = 0.8, [Cause.Memory] = 0.5, [Cause.Bios] = 0.4,
                [Cause.DeviceDriver] = 0.3,
            },
            FirstSteps = new[]
            {
                "Tritt es unter Last auf (Spiel, Rendern)? Dann sind Netzteil und Kuehlung die ersten Verdaechtigen.",
                "Alle Stromstecker an Grafikkarte und Mainboard pruefen - bei 12V-2x6-Steckern auf hoerbares Einrasten achten.",
                "Staub aus Kuehlern und Netzteil entfernen und Temperaturen unter Last mitlesen.",
            },
            FixIds = new[] { "fast-startup-off" },
            ToolIds = new[] { "reliability", "event-viewer", "hwinfo", "energy-report" },
        },
        new()
        {
            Id = "crash-freeze",
            Group = GroupStability,
            Title = "Das System friert komplett ein",
            Description =
                "Bild steht, Ton haengt in einer Schleife, nichts reagiert mehr. Das deutet auf Speicher, " +
                "Datentraeger-Aussetzer oder einen blockierenden Treiber hin - anders als beim Schwarzbild, bei dem der Ton weiterlaeuft.",
            Keywords = new[]
            {
                "friert ein", "haengt sich auf", "freeze", "eingefroren", "reagiert nicht mehr", "ton haengt",
                "maus bewegt sich nicht", "nur noch reset hilft",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Memory] = 0.9, [Cause.Storage] = 0.8, [Cause.DeviceDriver] = 0.7, [Cause.GpuDriver] = 0.5,
                [Cause.Thermal] = 0.4, [Cause.PowerSupply] = 0.3,
            },
            FirstSteps = new[]
            {
                "Pruefen, ob der Ton in einer kurzen Schleife haengt - das ist das Kennzeichen eines echten Freeze.",
                "Speichertest laufen lassen (Windows-Speicherdiagnose, besser MemTest86).",
                "SMART-Werte der SSD pruefen; Aussetzer des Datentraegers frieren das System sekundenlang ein.",
            },
            FixIds = new[] { "sfc-dism", "fast-startup-off" },
            ToolIds = new[] { "mdsched", "memtest", "crystaldiskinfo", "reliability" },
        },
        new()
        {
            Id = "game-crash",
            Group = GroupStability,
            Title = "Spiele stuerzen ab oder ruckeln",
            Description =
                "Abstuerze nur in Spielen deuten auf Grafiktreiber, Uebertaktung oder Lastspitzen hin. " +
                "Ruckeln bei guter Bildrate ist dagegen meist ein Problem der Bildausgabe oder von Overlays.",
            Keywords = new[]
            {
                "spiel stuerzt ab", "game crash", "fps", "ruckelt", "stotter", "microstutter", "absturz im spiel",
                "directx fehler", "spiel schliesst sich",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.GpuDriver] = 1.0, [Cause.GpuHardware] = 0.8, [Cause.Software] = 0.7, [Cause.Thermal] = 0.6,
                [Cause.PowerSupply] = 0.5, [Cause.Memory] = 0.5, [Cause.Cpu] = 0.4,
            },
            FirstSteps = new[]
            {
                "Alle Overlays abschalten (Discord, Steam, GeForce Experience, RivaTuner) - sie haengen sich in die Grafikausgabe ein.",
                "Uebertaktung der Grafikkarte zuruecksetzen, auch wenn das Werkzeug geschlossen ist: die Kurve bleibt aktiv.",
                "Temperaturen von CPU und Grafikkarte waehrend des Spielens mitlesen.",
            },
            FixIds = new[] { "hags-off", "tdr-reset" },
            ToolIds = new[] { "hwinfo", "ddu", "nvidia-drivers", "dxdiag" },
        },
        new()
        {
            Id = "overheat",
            Group = GroupStability,
            Title = "Der Rechner wird sehr heiss oder sehr laut",
            Description =
                "Hohe Temperaturen fuehren zu Drosselung, Aussetzern und im Extremfall zum Abschalten. " +
                "Die Diagnose liest Sensoren aus und sucht im Protokoll nach thermischen Ereignissen.",
            Keywords = new[]
            {
                "heiss", "temperatur", "ueberhitzt", "luefter laut", "drosselt", "throttling", "warm",
                "kuehlung", "staub",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Thermal] = 1.0, [Cause.Cpu] = 0.6, [Cause.GpuDriver] = 0.3, [Cause.PowerSettings] = 0.3,
            },
            FirstSteps = new[]
            {
                "Staubfilter, Luefter und Kuehlkoerper reinigen - das ist die wirksamste Einzelmassnahme.",
                "Pruefen, ob alle Gehaeuseluefter drehen und die Luftrichtung stimmt.",
                "Temperaturen im Leerlauf und unter Last vergleichen; steigt die CPU sofort auf ueber 90 Grad, sitzt der Kuehler schlecht.",
            },
            FixIds = Array.Empty<string>(),
            ToolIds = new[] { "hwinfo", "taskmgr", "energy-report" },
        },

        // ================= Geraete und Anschluesse =================
        new()
        {
            Id = "device-usb-unknown",
            Group = GroupDevices,
            Title = "Ein USB-Geraet wird nicht erkannt",
            Description =
                "Windows meldet 'USB-Geraet wurde nicht erkannt' oder das Geraet erscheint gar nicht. " +
                "Ursachen: zu wenig Strom am Anschluss, selektives USB-Energiesparen, ein Hub oder ein fehlender Treiber.",
            Keywords = new[]
            {
                "usb", "wird nicht erkannt", "unbekanntes geraet", "stick", "festplatte extern", "hub",
                "anschluss", "steckt aber nichts passiert",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.UsbDevice] = 1.0, [Cause.DeviceDriver] = 0.8, [Cause.PowerSettings] = 0.6,
            },
            FirstSteps = new[]
            {
                "Direkt an einen Anschluss hinten am Mainboard stecken, nicht ueber Hub oder Frontpanel.",
                "Anderes Kabel testen - bei externen Platten ist das Kabel die haeufigste Ursache.",
                "Geraet an einem anderen Rechner gegentesten, um Geraet und PC zu trennen.",
            },
            FixIds = new[] { "usb-suspend-off" },
            ToolIds = new[] { "device-manager", "event-viewer", "power-options" },
        },
        new()
        {
            Id = "device-error-code",
            Group = GroupDevices,
            Title = "Im Geraete-Manager steht ein Ausrufezeichen an einem Geraet",
            Description =
                "Ein gelbes Ausrufezeichen bedeutet, dass Windows dem Geraet einen Fehlercode zugewiesen hat. " +
                "Der Code sagt genau, woran es liegt - fehlender Treiber, deaktiviert, nicht gestartet oder nicht angeschlossen.",
            Keywords = new[]
            {
                "geraete-manager", "ausrufezeichen", "gelbes dreieck", "fehlercode", "code 10", "code 43",
                "code 28", "treiber fehlt", "unbekanntes geraet",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.DeviceDriver] = 1.0, [Cause.UsbDevice] = 0.5, [Cause.OperatingSystem] = 0.4,
            },
            FirstSteps = new[]
            {
                "Fehlercode im Geraete-Manager ablesen (Doppelklick auf das Geraet, Reiter 'Allgemein').",
                "Treiber von der Hersteller-Webseite laden statt ueber Windows Update - besonders bei Chipsatz und Netzwerk.",
                "Bei 'Code 43' das Geraet einmal deinstallieren und den Rechner neu starten.",
            },
            FixIds = Array.Empty<string>(),
            ToolIds = new[] { "device-manager", "msinfo", "event-viewer" },
        },
        new()
        {
            Id = "device-webcam",
            Group = GroupDevices,
            Title = "Die Webcam funktioniert nicht oder bleibt schwarz",
            Description =
                "Wie beim Mikrofon gilt: Windows-Datenschutz, exklusive Nutzung durch eine andere Anwendung oder " +
                "ein Treiberproblem. Ein schwarzes Bild bei leuchtender LED heisst fast immer: eine andere App hat die Kamera.",
            Keywords = new[]
            {
                "webcam", "kamera", "bild schwarz", "cam geht nicht", "video aus", "kamera nicht gefunden",
                "teams kamera", "zoom kamera",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.AppPermission] = 1.0, [Cause.DeviceDriver] = 0.7, [Cause.UsbDevice] = 0.6, [Cause.Software] = 0.4,
            },
            FirstSteps = new[]
            {
                "Windows-Einstellungen > Datenschutz > Kamera: Zugriff und 'Desktop-Apps' pruefen.",
                "Alle anderen Programme schliessen, die die Kamera nutzen koennten.",
                "Mit der Windows-App 'Kamera' gegentesten - funktioniert sie dort, liegt es an der anderen Anwendung.",
            },
            FixIds = new[] { "camera-privacy-allow", "usb-suspend-off" },
            ToolIds = new[] { "camera-privacy", "device-manager" },
        },
        new()
        {
            Id = "device-input-lag",
            Group = GroupDevices,
            Title = "Maus oder Tastatur setzen kurz aus",
            Description =
                "Kurze Aussetzer von Eingabegeraeten kommen meist vom selektiven USB-Energiesparen, von einem " +
                "ueberlasteten USB-Anschluss oder von Funkstoerungen bei kabellosen Geraeten.",
            Keywords = new[]
            {
                "maus haengt", "tastatur", "eingabe", "aussetzer", "verzoegerung", "input lag", "funkmaus",
                "bluetooth maus", "tastatur reagiert nicht",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.UsbDevice] = 1.0, [Cause.PowerSettings] = 0.8, [Cause.DeviceDriver] = 0.5, [Cause.Cpu] = 0.3,
            },
            FirstSteps = new[]
            {
                "Empfaenger von Funkgeraeten direkt vorne anstecken oder ein Verlaengerungskabel nutzen - Metallgehaeuse daempfen das Signal.",
                "Nicht alles an einen Hub haengen; USB 3.0 stoert 2,4-GHz-Funk besonders stark.",
                "Batterien bzw. Akkustand pruefen.",
            },
            FixIds = new[] { "usb-suspend-off" },
            ToolIds = new[] { "device-manager", "power-options" },
        },

        // ================= Windows und Datentraeger =================
        new()
        {
            Id = "storage-slow",
            Group = GroupWindows,
            Title = "Die Festplatte oder SSD ist langsam oder macht Geraeusche",
            Description =
                "Dauerhaft 100 % Datentraegerlast, lange Ladezeiten oder Klickgeraeusche. Die Diagnose liest " +
                "Gesundheitsstatus und Datentraegerfehler aus dem Protokoll - bei einer sterbenden Platte zaehlt jede Stunde.",
            Keywords = new[]
            {
                "festplatte", "ssd", "langsam", "100 prozent", "klackern", "geraeusch", "datentraeger",
                "laedt ewig", "smart", "defekt",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Storage] = 1.0, [Cause.OperatingSystem] = 0.4, [Cause.Software] = 0.3,
            },
            FirstSteps = new[]
            {
                "Sofort eine Sicherung der wichtigen Daten anlegen, bevor weiter getestet wird.",
                "Im Task-Manager pruefen, welcher Prozess die Datentraegerlast erzeugt.",
                "SMART-Werte mit CrystalDiskInfo ansehen, besonders 'wiederzugewiesene Sektoren'.",
            },
            FixIds = new[] { "sfc-dism" },
            ToolIds = new[] { "crystaldiskinfo", "taskmgr", "cleanmgr", "event-viewer" },
        },
        new()
        {
            Id = "storage-full",
            Group = GroupWindows,
            Title = "Der Speicherplatz ist voll",
            Description =
                "Ein volles Systemlaufwerk bremst Windows aus, verhindert Updates und sorgt dafuer, dass " +
                "Absturzabbilder gar nicht erst geschrieben werden - was jede spaetere Fehlersuche erschwert.",
            Keywords = new[]
            {
                "speicherplatz", "voll", "kein platz", "festplatte voll", "c ist voll", "speicher knapp",
                "aufraeumen", "wenig speicher",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Storage] = 1.0, [Cause.OperatingSystem] = 0.5,
            },
            FirstSteps = new[]
            {
                "Datentraegerbereinigung mit 'Systemdateien bereinigen' ausfuehren - alte Windows-Installationen belegen schnell 20 GB und mehr.",
                "Speicheranalyse in den Windows-Einstellungen unter System > Speicher ansehen.",
                "Mindestens 10 bis 15 Prozent des Laufwerks freihalten.",
            },
            FixIds = Array.Empty<string>(),
            ToolIds = new[] { "cleanmgr", "storage-settings" },
        },
        new()
        {
            Id = "win-update-fails",
            Group = GroupWindows,
            Title = "Windows-Update schlaegt fehl oder haengt",
            Description =
                "Updates, die immer wieder mit einem Fehlercode abbrechen, gehen meist auf beschaedigte " +
                "Systemdateien, zu wenig Platz oder einen halbfertigen vorherigen Update-Versuch zurueck.",
            Keywords = new[]
            {
                "update", "windows update", "fehlercode", "installiert nicht", "haengt bei prozent",
                "update schlaegt fehl", "0x8", "neustart schleife",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.OperatingSystem] = 1.0, [Cause.Storage] = 0.5, [Cause.Network] = 0.3,
            },
            FirstSteps = new[]
            {
                "Freien Speicherplatz pruefen - unter 20 GB scheitern grosse Funktionsupdates regelmaessig.",
                "Den Rechner ueber 'Neu starten' durchstarten, damit ausstehende Schritte abgeschlossen werden.",
                "Systemdateien pruefen und reparieren lassen (DISM und sfc).",
            },
            FixIds = new[] { "sfc-dism" },
            ToolIds = new[] { "windows-update", "troubleshoot", "cleanmgr", "event-viewer" },
        },
        new()
        {
            Id = "win-boot-slow",
            Group = GroupWindows,
            Title = "Windows startet sehr langsam",
            Description =
                "Lange Startzeiten entstehen durch Autostart-Programme, wartende Dienste oder ein muedes " +
                "Systemlaufwerk. Der Windows-Schnellstart verdeckt das Problem eher, als es zu loesen.",
            Keywords = new[]
            {
                "startet langsam", "bootzeit", "hochfahren dauert", "anmeldung dauert", "autostart",
                "schwarzer bildschirm beim start", "lange ladezeit",
            },
            Causes = new Dictionary<Cause, double>
            {
                [Cause.Software] = 0.9, [Cause.Storage] = 0.8, [Cause.OperatingSystem] = 0.5, [Cause.PowerSettings] = 0.4,
            },
            FirstSteps = new[]
            {
                "Task-Manager > Autostart: alles mit hoher Startauswirkung deaktivieren.",
                "Pruefen, ob Windows auf einer SSD oder noch auf einer klassischen Festplatte liegt.",
                "Ereignisprotokoll auf Dienste ansehen, die beim Start in einen Zeitablauf laufen.",
            },
            FixIds = new[] { "fast-startup-off" },
            ToolIds = new[] { "taskmgr", "event-viewer", "msinfo" },
        },
    };

    public static Symptom? ById(string? id)
        => id is null ? null : All.FirstOrDefault(s => s.Id == id);

    public static IEnumerable<IGrouping<string, Symptom>> ByGroup() => All.GroupBy(s => s.Group);
}

/// <summary>
/// Ordnet einer freien Problembeschreibung die passenden Symptome zu.
/// Bewusst einfach gehalten: Wortvergleich mit Normalisierung, kein Modell und
/// keine Netzverbindung - das Ergebnis ist dadurch nachvollziehbar und sofort da.
/// </summary>
public static class SymptomMatcher
{
    /// <summary>Woerter, die in jeder Problembeschreibung vorkommen und nichts unterscheiden.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "und", "oder", "aber", "der", "die", "das", "ein", "eine", "einen", "einem", "einer", "mein", "meine",
        "meinem", "meinen", "ich", "mir", "mich", "ist", "sind", "war", "wird", "werden", "hat", "habe", "haben",
        "nicht", "kein", "keine", "mehr", "immer", "wieder", "beim", "beim", "wenn", "dann", "sich", "auch",
        "auf", "aus", "mit", "von", "vom", "fuer", "bei", "nach", "vor", "zum", "zur", "als", "wie", "was",
        "pc", "computer", "rechner", "problem", "geht", "macht", "kann", "muss", "man", "es", "im", "in", "an",
    };

    /// <summary>Liefert die Symptome, die am besten zur Beschreibung passen (bestes zuerst).</summary>
    public static IReadOnlyList<(Symptom Symptom, double Score)> Match(string? query, int max = 6)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return Array.Empty<(Symptom, double)>();

        var scored = new List<(Symptom Symptom, double Score)>();

        foreach (var symptom in SymptomCatalog.All)
        {
            double score = 0;

            var title = Normalize(symptom.Title);
            var description = Normalize(symptom.Description);
            var keywords = symptom.Keywords.Select(Normalize).ToList();

            foreach (var token in tokens)
            {
                // Ein Stichwort, das den Suchbegriff enthaelt, ist das staerkste Signal.
                if (keywords.Any(k => k == token)) score += 4;
                else if (keywords.Any(k => k.Contains(token, StringComparison.Ordinal))) score += 2.5;
                else if (token.Length >= 5 && keywords.Any(k => token.Contains(k, StringComparison.Ordinal))) score += 2;
                else if (title.Contains(token, StringComparison.Ordinal)) score += 1.5;
                else if (description.Contains(token, StringComparison.Ordinal)) score += 0.5;
            }

            if (score > 0) scored.Add((symptom, Math.Round(score, 2)));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symptom.Title, StringComparer.CurrentCulture)
            .Take(max)
            .ToList();
    }

    private static List<string> Tokenize(string? query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        foreach (var raw in Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3 || StopWords.Contains(raw)) continue;
            if (!result.Contains(raw)) result.Add(raw);
        }
        return result;
    }

    /// <summary>Kleinschreibung, Umlaute ausgeschrieben, Satzzeichen raus.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'ß': sb.Append("ss"); break;
                default:
                    sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
                    break;
            }
        }
        return sb.ToString();
    }
}
