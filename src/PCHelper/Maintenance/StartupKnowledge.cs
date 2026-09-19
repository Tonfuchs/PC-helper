using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace PCHelper.Maintenance;

/// <summary>Empfehlung zu einem Autostart-Eintrag.</summary>
public enum StartupAdvice
{
    /// <summary>Bremst den Start und wird nicht gebraucht.</summary>
    Remove,

    /// <summary>Geschmackssache.</summary>
    Optional,

    /// <summary>Sollte wirklich mitstarten.</summary>
    Keep,
}

public sealed record StartupInfo(StartupAdvice Advice, string Text, bool Known);

/// <summary>
/// Was ist das ueberhaupt fuer ein Programm? Erst die eingebaute Tabelle, dann die Programmdatei selbst -
/// dort steht Hersteller und Beschreibung, ganz ohne Internet.
/// </summary>
public static class StartupKnowledge
{
    private static readonly (string Key, StartupAdvice Advice, string Text)[] Table =
    {
        ("OneDrive", StartupAdvice.Optional, "Synchronisiert deine Dateien mit der Cloud. Muss nur mitstarten, wenn du willst, dass sich alles sofort abgleicht."),
        ("Steam", StartupAdvice.Remove, "Die Spieleplattform. Startet sonst auch, wenn du ein Spiel anklickst - im Autostart kostet sie nur Zeit und laedt im Hintergrund Updates."),
        ("Discord", StartupAdvice.Optional, "Chatprogramm. Wenn du es sowieso immer offen hast, kann es bleiben. Sonst raus."),
        ("EADM", StartupAdvice.Remove, "Der Spielelauncher von Electronic Arts. Braucht kein Mensch beim Hochfahren."),
        ("EpicGamesLauncher", StartupAdvice.Remove, "Der Spielelauncher von Epic Games. Startet von selbst mit, wenn du ein Spiel oeffnest."),
        ("PlariumPlay", StartupAdvice.Remove, "Noch ein Spielelauncher. Im Autostart voellig unnoetig."),
        ("RiotClient", StartupAdvice.Remove, "Der Launcher von Riot Games (League of Legends, Valorant). Startet mit dem Spiel sowieso."),
        ("Overwolf", StartupAdvice.Remove, "Overlay-Plattform fuer Spiele-Zusatzprogramme. Bekannt dafuer, im Hintergrund viel Leistung zu ziehen."),
        ("BitTorrent", StartupAdvice.Remove, "BitTorrent. Laedt und verteilt im Hintergrund Dateien und kann deine ganze Leitung belegen - genau die Art Programm, die alles andere langsam macht."),
        ("uTorrent", StartupAdvice.Remove, "Ein Torrent-Programm. Laedt und verteilt im Hintergrund Dateien und kann deine ganze Leitung belegen."),
        ("qBittorrent", StartupAdvice.Remove, "Ein Torrent-Programm. Laedt und verteilt im Hintergrund Dateien und kann deine ganze Leitung belegen."),
        ("Docker Desktop", StartupAdvice.Remove, "Entwicklerwerkzeug fuer Container. Frisst dauerhaft Arbeitsspeicher und startet eine kleine virtuelle Maschine mit. Nur mitstarten lassen, wenn du taeglich damit arbeitest."),
        ("NordVPN", StartupAdvice.Optional, "Ein VPN-Programm. Hinweis: Manche VPN-Programme haben zusaetzlich eine eigene Autostart-Einstellung im Programm selbst, die von diesem Eintrag unabhaengig ist."),
        ("NordPass", StartupAdvice.Optional, "Passwortverwaltung. Praktisch, wenn sie immer bereit ist - noetig ist es nicht."),
        ("Mozilla-Firefox", StartupAdvice.Remove, "Firefox will sich beim Hochfahren schon mal vorladen. Spart dir spaeter eine Sekunde und kostet beim Start mehr."),
        ("MicrosoftEdgeAutoLaunch", StartupAdvice.Remove, "Edge startet sich unsichtbar im Hintergrund vor, damit er sich spaeter schneller anfuehlt. Ungefragt und unnoetig."),
        ("Opera GX Browser Assistant", StartupAdvice.Remove, "Hilfsprogramm von Opera, das im Hintergrund mitlaeuft. Wird nicht gebraucht."),
        ("Opera GX", StartupAdvice.Remove, "Der Browser laedt sich beim Hochfahren vor."),
        ("Voicemod", StartupAdvice.Optional, "Stimmverzerrer fuers Streaming. Wenn du ihn nur ab und zu benutzt, kann er raus."),
        ("Stream Deck", StartupAdvice.Keep, "Steuert dein Elgato Stream Deck. Ohne das Programm bleiben die Tasten tot - sollte mitstarten."),
        ("Volume Controller SD plugin", StartupAdvice.Keep, "Gehoert zum Stream Deck und regelt die Lautstaerke-Tasten."),
        ("NVIDIA Broadcast", StartupAdvice.Optional, "Rauschunterdrueckung und Hintergrundentfernung fuer Mikrofon und Kamera. Nur noetig, wenn du streamst oder in Videokonferenzen bist."),
        ("WhisperTyping", StartupAdvice.Optional, "Spracherkennung zum Diktieren."),
        ("LM Studio", StartupAdvice.Remove, "Oberflaeche fuer lokale KI-Modelle. Braucht viel Arbeitsspeicher und muss nur laufen, wenn du sie benutzt."),
        ("com.squirrel.Poe", StartupAdvice.Remove, "Poe-App (KI-Chat). Im Autostart nicht noetig."),
        ("Duet Display", StartupAdvice.Optional, "Macht ein Tablet zum zweiten Bildschirm. Nur noetig, wenn du das taeglich nutzt."),
        ("HidHide", StartupAdvice.Remove, "Sucht nur nach Updates fuer ein Controller-Hilfsprogramm. Kann problemlos raus."),
        ("SignalRgb", StartupAdvice.Optional, "Steuert die Beleuchtung deiner Geraete. Ohne das Programm bleibt die Beleuchtung auf der letzten Einstellung stehen."),
        ("SecurityHealth", StartupAdvice.Keep, "Das Schild-Symbol von Windows Defender. Gehoert zum Virenschutz und sollte bleiben."),
        ("RtkAudUService", StartupAdvice.Keep, "Der Realtek-Audiotreiber. Ohne den kann der Ton spinnen - bitte drin lassen."),
        ("Vanguard", StartupAdvice.Keep, "Anti-Cheat von Riot. Laesst sich nicht sinnvoll abschalten, solange Valorant installiert ist."),
    };

    private const string UnknownText =
        "Dieses Programm kenne ich nicht. Der Autostart entscheidet nur, ob es beim Hochfahren startet - nicht, ob es " +
        "installiert bleibt. Im Zweifel rauswerfen und schauen, ob dir etwas fehlt.";

    /// <summary>
    /// Namensmuster fuer Hardware- und Schutzsoftware. Hier kann man sich echten Aerger einhandeln,
    /// deshalb kommt vor dem Abschalten einmal eine Rueckfrage - verboten wird es nicht.
    /// </summary>
    public static readonly Regex Sensitive = new(
        "fan|luefter|temp|thermal|cool|gpu|nvidia|amd|intel|driver|treiber|defender|antivir|security|vanguard|" +
        "anticheat|hidhide|vigem|corsair|logitech|razer|asus|msi|gigabyte|afterburner|icue|synapse|backup|" +
        "sicherung|bitlocker|raid|smart",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static StartupInfo Rate(string name)
    {
        foreach (var (key, advice, text) in Table)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
                return new StartupInfo(advice, text, true);

        return new StartupInfo(StartupAdvice.Optional, UnknownText, false);
    }

    /// <summary>
    /// Fragt die Programmdatei, wer sie ist. Das steht in jeder Windows-Programmdatei drin und beantwortet
    /// "was ist das ueberhaupt?" bei kryptischen Namen wie "com.squirrel.Poe.Poe" meist besser als jede Suche.
    /// </summary>
    public static string? DescribeFile(string? command)
    {
        var path = ExtractPath(command);
        if (path is null) return null;

        try
        {
            if (!File.Exists(path)) return null;
            var v = FileVersionInfo.GetVersionInfo(path);

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(v.CompanyName)) parts.Add(v.CompanyName.Trim());
            if (!string.IsNullOrWhiteSpace(v.FileDescription)) parts.Add(v.FileDescription.Trim());
            else if (!string.IsNullOrWhiteSpace(v.ProductName)) parts.Add(v.ProductName.Trim());

            return parts.Count == 0 ? null : string.Join(" - ", parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Schaelt den Programmpfad aus einer Befehlszeile (mit und ohne Anfuehrungszeichen).</summary>
    public static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var text = Environment.ExpandEnvironmentVariables(command.Trim());

        var quoted = Regex.Match(text, "^\"([^\"]+)\"");
        if (quoted.Success) return quoted.Groups[1].Value;

        var exe = Regex.Match(text, @"^(.+?\.exe)", RegexOptions.IgnoreCase);
        if (exe.Success) return exe.Groups[1].Value;

        return text.Split(' ', 2)[0];
    }
}
