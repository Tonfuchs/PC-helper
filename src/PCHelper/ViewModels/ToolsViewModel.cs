using System.Collections.ObjectModel;
using System.IO;
using PCHelper.Core;

namespace PCHelper.ViewModels;

/// <summary>Werkzeuge eines Themenbereichs.</summary>
public sealed class ToolGroup
{
    public required string Name { get; init; }
    public ObservableCollection<ToolItem> Items { get; } = new();
}

/// <summary>Ein Eintrag der Werkzeugliste.</summary>
public sealed class ToolItem
{
    /// <summary>Kurzname, ueber den Symptome auf dieses Werkzeug verweisen.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Group { get; init; }
    public required RelayCommand Command { get; init; }
    public string ButtonText { get; init; } = "Oeffnen";
}

/// <summary>Sammlung nuetzlicher Windows-Bordmittel und externer Werkzeuge.</summary>
public sealed class ToolsViewModel : ObservableObject
{
    private string _status = "Diese Werkzeuge gehoeren zu Windows bzw. sind bewaehrte Standardprogramme.";

    public ToolsViewModel()
    {
        Add("device-manager", "Windows", "Geraete-Manager", "Treiber pruefen, Geraete mit Fehlerzeichen finden.",
            () => Shell.Launch("devmgmt.msc"));

        Add("event-viewer", "Windows", "Ereignisanzeige", "Das vollstaendige Windows-Protokoll - Quelle aller Befunde dieser App.",
            () => Shell.Launch("eventvwr.msc"));

        Add("reliability", "Windows", "Zuverlaessigkeitsverlauf", "Zeitachse aller Abstuerze und Probleme - sehr gut, um Muster zu erkennen.",
            () => Shell.Launch("perfmon.exe", "/rel"));

        Add("msinfo", "Windows", "Systeminformationen", "Vollstaendige Hardware- und Treiberuebersicht (msinfo32).",
            () => Shell.Launch("msinfo32.exe"));

        Add("dxdiag", "Windows", "DirectX-Diagnose", "Grafik- und Soundinformationen (dxdiag).",
            () => Shell.Launch("dxdiag.exe"));

        Add("taskmgr", "Windows", "Task-Manager", "Zeigt Prozessorlast, Speicher, Datentraeger, Netzwerk und den Autostart.",
            () => Shell.Launch("taskmgr.exe"));

        Add("resource-monitor", "Windows", "Ressourcenmonitor", "Zeigt genau, welcher Prozess CPU, Datentraeger oder Netzwerk belegt.",
            () => Shell.Launch("resmon.exe"));

        Add("mdsched", "Windows", "Windows-Speicherdiagnose", "Prueft den Arbeitsspeicher beim naechsten Neustart.",
            () => Shell.Launch("mdsched.exe"), "Starten");

        Add("cleanmgr", "Windows", "Datentraegerbereinigung", "Gibt Speicherplatz frei, auch aus alten Windows-Installationen.",
            () => Shell.Launch("cleanmgr.exe"));

        Add("restore", "Windows", "Systemwiederherstellung", "Auf einen frueheren Zustand zuruecksetzen.",
            () => Shell.Launch("rstrui.exe"));

        Add("display-settings", "Einstellungen", "Anzeigeeinstellungen", "Aufloesung, Bildwiederholrate und Monitoranordnung.",
            () => Shell.Open("ms-settings:display"));

        Add("graphics-settings", "Einstellungen", "Grafikeinstellungen", "Hardwarebeschleunigte GPU-Planung und Standardgrafikkarte.",
            () => Shell.Open("ms-settings:display-advancedgraphics"));

        Add("sound-settings", "Einstellungen", "Sound-Einstellungen", "Wiedergabe- und Aufnahmegeraete, Lautstaerke pro Anwendung.",
            () => Shell.Open("ms-settings:sound"));

        Add("sound-devices", "Einstellungen", "Sound-Systemsteuerung (klassisch)", "Die alte Ansicht - nur hier lassen sich deaktivierte und getrennte Geraete einblenden und wieder aktivieren.",
            () => Shell.Launch("control.exe", "mmsys.cpl"));

        Add("mic-privacy", "Einstellungen", "Datenschutz: Mikrofon", "Der entscheidende Schalter 'Desktop-Apps duerfen zugreifen' steht hier ganz unten.",
            () => Shell.Open("ms-settings:privacy-microphone"));

        Add("camera-privacy", "Einstellungen", "Datenschutz: Kamera", "Zugriffsrechte fuer Apps und klassische Programme auf die Webcam.",
            () => Shell.Open("ms-settings:privacy-webcam"));

        Add("network-settings", "Einstellungen", "Netzwerkeinstellungen", "Status, Adapter, IP-Konfiguration und WLAN-Verbindungen.",
            () => Shell.Open("ms-settings:network-status"));

        Add("troubleshoot", "Einstellungen", "Windows-Problembehandlung", "Die gefuehrten Assistenten fuer Netzwerk, Audio, Drucker und Windows Update.",
            () => Shell.Open("ms-settings:troubleshoot"));

        Add("storage-settings", "Einstellungen", "Speicheruebersicht", "Zeigt, was den Platz auf dem Systemlaufwerk belegt.",
            () => Shell.Open("ms-settings:storagesense"));

        Add("windows-update", "Einstellungen", "Windows Update", "Updateverlauf, ausstehende Updates und Fehlercodes.",
            () => Shell.Open("ms-settings:windowsupdate"));

        Add("power-options", "Einstellungen", "Energieoptionen", "Energieplaene und deren erweiterte Einstellungen.",
            () => Shell.Launch("control.exe", "powercfg.cpl"));

        Add("energy-report", "Berichte", "Windows-Energiebericht erstellen", "Sucht Probleme in den Energieeinstellungen (dauert 60 Sekunden).",
            CreateEnergyReportAsync, "Erstellen");

        Add("sleep-study", "Berichte", "Schlafprotokoll erstellen", "Zeigt, wie das System in Energiesparzustaende wechselt.",
            CreateSleepStudyAsync, "Erstellen");

        Add("data-folder", "Berichte", "Datenordner oeffnen", "Messreihen, Vorfaelle und Protokolldatei dieser App.",
            () => Shell.Open(AppInfo.DataDir));

        Add("report-folder", "Berichte", "Berichtsordner oeffnen", "Die erzeugten HTML-Berichte unter Dokumente.",
            () => Shell.Open(AppInfo.ReportDir));

        Add("ddu", "Empfohlene Programme", "Display Driver Uninstaller (DDU)", "Entfernt Grafiktreiber restlos - Pflicht vor einer sauberen Neuinstallation.",
            () => Shell.Open("https://www.wagnardsoft.com/display-driver-uninstaller-DDU-"), "Zur Webseite");

        Add("hwinfo", "Empfohlene Programme", "HWiNFO64", "Liest alle Sensoren aus - Temperaturen, Spannungen, Luefter.",
            () => Shell.Open("https://www.hwinfo.com/download/"), "Zur Webseite");

        Add("memtest", "Empfohlene Programme", "MemTest86", "Gruendlicher Speichertest von USB-Stick, unabhaengig von Windows.",
            () => Shell.Open("https://www.memtest86.com/"), "Zur Webseite");

        Add("crystaldiskinfo", "Empfohlene Programme", "CrystalDiskInfo", "Zeigt die SMART-Werte von SSDs und Festplatten im Klartext.",
            () => Shell.Open("https://crystalmark.info/en/software/crystaldiskinfo/"), "Zur Webseite");

        Add("nvidia-drivers", "Empfohlene Programme", "NVIDIA-Treiber", "Aktuelle Treiber und Treiberarchiv fuer aeltere Versionen.",
            () => Shell.Open("https://www.nvidia.com/de-de/geforce/drivers/"), "Zur Webseite");

        foreach (var group in Tools.GroupBy(t => t.Group))
        {
            var g = new ToolGroup { Name = group.Key };
            foreach (var item in group) g.Items.Add(item);
            Groups.Add(g);
        }
    }

    public ObservableCollection<ToolItem> Tools { get; } = new();

    /// <summary>Nach Themenbereich gruppierte Werkzeuge fuer die Darstellung.</summary>
    public ObservableCollection<ToolGroup> Groups { get; } = new();

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Werkzeuge zu den angegebenen Kennungen, in der Reihenfolge der Kennungen.</summary>
    public IEnumerable<ToolItem> ByIds(IEnumerable<string> ids)
        => ids.Select(id => Tools.FirstOrDefault(t => t.Id == id)).OfType<ToolItem>();

    private void Add(string id, string group, string title, string description, Action action, string buttonText = "Oeffnen")
        => Tools.Add(new ToolItem
        {
            Id = id, Group = group, Title = title, Description = description, ButtonText = buttonText,
            Command = new RelayCommand(_ => action()),
        });

    private void Add(string id, string group, string title, string description, Func<Task> action, string buttonText = "Oeffnen")
        => Tools.Add(new ToolItem
        {
            Id = id, Group = group, Title = title, Description = description, ButtonText = buttonText,
            Command = new RelayCommand(_ => action()),
        });

    private async Task CreateEnergyReportAsync()
    {
        var target = Path.Combine(AppInfo.ReportDir, $"Energiebericht_{DateTime.Now:yyyy-MM-dd_HH-mm}.html");
        Status = "Der Energiebericht wird erstellt - das dauert etwa eine Minute ...";

        var result = await Shell.RunElevatedBatchAsync(
            new[] { $"powercfg /energy /output \"{target}\" /duration 60" }, "energiebericht");

        if (File.Exists(target))
        {
            Status = "Energiebericht erstellt.";
            Shell.Open(target);
        }
        else
        {
            Status = "Der Energiebericht konnte nicht erstellt werden. " + result.Combined.Trim();
        }
    }

    private async Task CreateSleepStudyAsync()
    {
        var target = Path.Combine(AppInfo.ReportDir, $"Schlafprotokoll_{DateTime.Now:yyyy-MM-dd_HH-mm}.html");
        Status = "Das Schlafprotokoll wird erstellt ...";

        var result = await Shell.RunElevatedBatchAsync(
            new[] { $"powercfg /sleepstudy /output \"{target}\"" }, "schlafprotokoll");

        if (File.Exists(target))
        {
            Status = "Schlafprotokoll erstellt.";
            Shell.Open(target);
        }
        else
        {
            Status = "Das Schlafprotokoll ist auf diesem System nicht verfuegbar (nur bei modernem Standby). "
                     + result.Combined.Trim();
        }
    }
}
