using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using PCHelper.Core;
using PCHelper.Monitoring;

namespace PCHelper.ViewModels;

/// <summary>Eine Zeile in der Liste der groessten VRAM-Verbraucher.</summary>
public sealed class GpuProcessRow
{
    public required string ProcessName { get; init; }
    public required int Pid { get; init; }
    public required double UsedMemoryMb { get; init; }
    public string MemoryText => UsedMemoryMb >= 1024 ? $"{UsedMemoryMb / 1024:0.#} GB" : $"{UsedMemoryMb:0} MB";
}

/// <summary>Live-Ansicht der Dauerueberwachung und Verwaltung der Vorfaelle.</summary>
public sealed class MonitorViewModel : ObservableObject
{
    private const int HistoryLength = 120;

    private readonly MonitorService _monitor;
    private readonly IncidentStore _incidents;
    private readonly Settings _settings;

    private string _note = "";
    private string _status = "";

    public MonitorViewModel(MonitorService monitor, IncidentStore incidents, Settings settings)
    {
        _monitor = monitor;
        _incidents = incidents;
        _settings = settings;

        ToggleCommand = new RelayCommand(_ => Toggle());
        ReportIncidentCommand = new RelayCommand(_ => ReportIncident());
        RefreshIncidentsCommand = new RelayCommand(_ => RefreshIncidents());
        DeleteIncidentCommand = new RelayCommand(p => DeleteIncident(p as Incident));
        ExportCsvCommand = new RelayCommand(_ => ExportCsvAsync());
        OpenDataFolderCommand = new RelayCommand(_ => Shell.Open(AppInfo.TelemetryDir));
        OpenSessionLogCommand = new RelayCommand(_ => Shell.Open(SessionLog.Path));
        ConfirmBlackscreenRestartCommand = new RelayCommand(_ => ConfirmRestart(true));
        DismissRestartPromptCommand = new RelayCommand(_ => ConfirmRestart(false));

        _monitor.SampleTaken += OnSampleTaken;
        _monitor.IncidentDetected += OnIncidentDetected;

        RefreshIncidents();
        _ = LoadBootHistoryAsync();
    }

    public ObservableCollection<Incident> Incidents { get; } = new();

    /// <summary>Verlauf der GPU-Temperatur fuer die Verlaufsgrafik.</summary>
    public ObservableCollection<double> GpuTempHistory { get; } = new();

    /// <summary>Verlauf der CPU-Auslastung fuer die Verlaufsgrafik.</summary>
    public ObservableCollection<double> CpuHistory { get; } = new();

    public RelayCommand ToggleCommand { get; }
    public RelayCommand ReportIncidentCommand { get; }
    public RelayCommand RefreshIncidentsCommand { get; }
    public RelayCommand DeleteIncidentCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenSessionLogCommand { get; }
    public RelayCommand ConfirmBlackscreenRestartCommand { get; }
    public RelayCommand DismissRestartPromptCommand { get; }

    /// <summary>Verlauf der Windows-Sitzungen (Hoch- und Herunterfahren).</summary>
    public ObservableCollection<BootSession> BootSessions { get; } = new();

    /// <summary>Prozesse, die aktuell am meisten Grafikspeicher belegen (VRAM-genau statt Task-Manager-Prozent).</summary>
    public ObservableCollection<GpuProcessRow> GpuProcesses { get; } = new();

    public bool IsRunning => _monitor.IsRunning;

    public string ToggleText => IsRunning ? "Ueberwachung anhalten" : "Ueberwachung starten";

    public string StateText => IsRunning
        ? $"Aktiv - es wird alle {_settings.SampleIntervalSeconds} Sekunden gemessen, bei hoher GPU-Last jede Sekunde."
        : "Angehalten - es werden keine Messwerte aufgezeichnet.";

    public string Note { get => _note; set => Set(ref _note, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    // --- Momentanwerte fuer die Kacheln ---
    public string CpuText => Fmt(_monitor.Latest?.CpuPercent, "%");
    public string RamText => Fmt(_monitor.Latest?.RamPercent, "%");
    public string GpuTempText => Fmt(_monitor.Latest?.GpuTempC, "°C");
    public string GpuLoadText => Fmt(_monitor.Latest?.GpuUtilPercent, "%");
    public string GpuPowerText => Fmt(_monitor.Latest?.GpuPowerWatt, "W");
    public string DisplayCountText => _monitor.Latest is null ? "-" : _monitor.Latest.DisplayCount.ToString();
    public string LastSampleText => _monitor.Latest is null ? "noch keine Messung" : $"zuletzt {_monitor.Latest.TimeText}";

    // --- GPU im Detail: was der Task-Manager nicht zeigt ---
    public bool HasGpuDetail => _monitor.LatestGpu is not null;

    public string GpuMemoryText
    {
        get
        {
            var g = _monitor.LatestGpu;
            if (g is null) return "-";
            return g.MemoryUsedMb is { } used && g.MemoryTotalMb is { } total
                ? $"{used / 1024:0.#} / {total / 1024:0.#} GB ({g.MemoryLoadPercent:0}%)"
                : "-";
        }
    }

    public string GpuFanText => Fmt(_monitor.LatestGpu?.FanPercent, "%");
    public string GpuMemoryUtilText => Fmt(_monitor.LatestGpu?.MemoryUtilPercent, "%");
    public string GpuEncoderText => Fmt(_monitor.LatestGpu?.EncoderUtilPercent, "%");
    public string GpuDecoderText => Fmt(_monitor.LatestGpu?.DecoderUtilPercent, "%");

    public string GpuPcieText
    {
        get
        {
            var g = _monitor.LatestGpu;
            if (g?.PcieLinkGenCurrent is null || g.PcieLinkWidthCurrent is null) return "-";
            var text = $"Gen{g.PcieLinkGenCurrent} x{g.PcieLinkWidthCurrent}";
            if (g.PcieLinkGenMax is { } genMax && g.PcieLinkWidthMax is { } widthMax)
                text += $" (max Gen{genMax} x{widthMax})";
            return text;
        }
    }

    public bool GpuIsThrottled => _monitor.LatestGpu?.IsThrottled == true;

    public string GpuThrottleText
    {
        get
        {
            var g = _monitor.LatestGpu;
            if (g is null || !g.IsThrottled) return "";

            var reasons = new List<string>();
            if (g.ThrottlePowerCap == true) reasons.Add("Leistungslimit");
            if (g.ThrottleThermal == true) reasons.Add("Temperatur");
            if (g.ThrottleHwSlowdown == true) reasons.Add("Hardware-Schutz");
            return string.Join(", ", reasons);
        }
    }

    private void Toggle()
    {
        if (_monitor.IsRunning) _monitor.Stop();
        else _monitor.Start();

        Raise(nameof(IsRunning));
        Raise(nameof(ToggleText));
        Raise(nameof(StateText));
    }

    private void OnSampleTaken(object? sender, TelemetrySample sample)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Push(GpuTempHistory, sample.GpuTempC ?? 0);
            Push(CpuHistory, sample.CpuPercent ?? 0);

            Raise(nameof(CpuText));
            Raise(nameof(RamText));
            Raise(nameof(GpuTempText));
            Raise(nameof(GpuLoadText));
            Raise(nameof(GpuPowerText));
            Raise(nameof(DisplayCountText));
            Raise(nameof(LastSampleText));

            Raise(nameof(HasGpuDetail));
            Raise(nameof(GpuMemoryText));
            Raise(nameof(GpuFanText));
            Raise(nameof(GpuMemoryUtilText));
            Raise(nameof(GpuEncoderText));
            Raise(nameof(GpuDecoderText));
            Raise(nameof(GpuPcieText));
            Raise(nameof(GpuIsThrottled));
            Raise(nameof(GpuThrottleText));

            RefreshGpuProcesses();
        });
    }

    private void RefreshGpuProcesses()
    {
        GpuProcesses.Clear();
        foreach (var p in _monitor.LatestGpuProcesses.Take(6))
            GpuProcesses.Add(new GpuProcessRow { ProcessName = p.ProcessName, Pid = p.Pid, UsedMemoryMb = p.UsedMemoryMb });
    }

    private void OnIncidentDetected(object? sender, Incident incident)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Incidents.Insert(0, incident);
            Status = $"Vorfall aufgezeichnet: {incident.Title} ({incident.TimeText}).";
        });
    }

    private static void Push(ObservableCollection<double> series, double value)
    {
        series.Add(value);
        while (series.Count > HistoryLength) series.RemoveAt(0);
    }

    // ---------- Neustart-Verlauf und Rueckfrage ----------

    private bool _showRestartPrompt;
    private string _restartPromptText = "";
    private string _bootSummary = "Der Neustart-Verlauf wird gelesen ...";
    private DateTime _promptRestartTime;

    /// <summary>Rueckfrage anzeigen, ob der letzte Neustart wegen eines Schwarzbildes noetig war.</summary>
    public bool ShowRestartPrompt { get => _showRestartPrompt; private set => Set(ref _showRestartPrompt, value); }

    public string RestartPromptText { get => _restartPromptText; private set => Set(ref _restartPromptText, value); }

    public string BootSummary { get => _bootSummary; private set => Set(ref _bootSummary, value); }

    private async Task LoadBootHistoryAsync()
    {
        var sessions = await Task.Run(() => BootHistory.Read(30));

        BootSessions.Clear();
        foreach (var s in sessions) BootSessions.Add(s);

        var (total, unexpected, median) = BootHistory.Summarize(sessions);
        BootSummary = total == 0
            ? "Im Ereignisprotokoll wurden keine Startvorgaenge gefunden."
            : $"{total} Startvorgaenge in den letzten 30 Tagen" +
              (unexpected > 0 ? $", davon {unexpected} ohne ordentliches Herunterfahren" : "") +
              (median is null ? "." : $". Typische Sitzungsdauer: {MonitorService.FormatUptime(median.Value)}.");

        EvaluateRestartPrompt(sessions);
    }

    /// <summary>
    /// Fragt einmal pro Windows-Start nach, ob der Neustart wegen eines
    /// Schwarzbildes noetig war. Genau das ist bei diesem Fehlerbild die
    /// verlaesslichste Information - der Nutzer weiss es, das Protokoll nicht.
    /// </summary>
    private void EvaluateRestartPrompt(IReadOnlyList<BootSession> sessions)
    {
        var uptime = Native.GetUptime();
        if (uptime > TimeSpan.FromHours(12)) return;      // zu lange her, um sich zu erinnern

        var bootTime = DateTime.Now - uptime;
        if (_settings.LastRestartPromptBootTime is { } asked &&
            Math.Abs((asked - bootTime).TotalMinutes) < 5) return;   // fuer diesen Start schon gefragt

        // sessions[0] ist die laufende Sitzung, sessions[1] die davor.
        var previous = sessions.Skip(1).FirstOrDefault();
        if (previous is null) return;

        _promptRestartTime = previous.End ?? bootTime;
        RestartPromptText =
            $"Der Rechner wurde am {_promptRestartTime:dd.MM.yyyy 'um' HH:mm} neu gestartet " +
            $"(die Sitzung davor lief {previous.DurationText}). War das noetig, weil der Bildschirm schwarz war?";
        ShowRestartPrompt = true;
    }

    private void ConfirmRestart(bool wasBlackscreen)
    {
        var bootTime = DateTime.Now - Native.GetUptime();
        _settings.LastRestartPromptBootTime = bootTime;
        _settings.Save();

        ShowRestartPrompt = false;

        if (!wasBlackscreen)
        {
            Status = "Alles klar - der Neustart wird als normal gewertet.";
            return;
        }

        var incident = _monitor.ConfirmBlackscreenRestart(
            _promptRestartTime,
            "Vom Nutzer bestaetigt: Der Neustart war noetig, um ein Schwarzbild zu beheben.");

        Status = $"Festgehalten: Neustart am {incident.TimeText} wegen Schwarzbild.";
    }

    private void ReportIncident()
    {
        var note = string.IsNullOrWhiteSpace(Note)
            ? "Ohne Beschreibung gemeldet."
            : Note.Trim();

        _monitor.ReportManual(note);
        Note = "";
        Status = "Danke - der Zeitpunkt ist festgehalten. Die Messwerte davor stehen jetzt im Bericht.";
    }

    private void RefreshIncidents()
    {
        Incidents.Clear();
        foreach (var i in _incidents.LoadAll(100)) Incidents.Add(i);
        Status = Incidents.Count == 0
            ? "Bisher wurden keine Vorfaelle aufgezeichnet."
            : $"{Incidents.Count} aufgezeichnete Vorfaelle.";
    }

    private void DeleteIncident(Incident? incident)
    {
        if (incident is null) return;
        _incidents.Delete(incident);
        Incidents.Remove(incident);
        Status = "Vorfall geloescht.";
    }

    private async Task ExportCsvAsync()
    {
        var target = Path.Combine(AppInfo.ReportDir, $"Messwerte_{DateTime.Now:yyyy-MM-dd_HH-mm}.csv");
        await Task.Run(() => _monitor.Telemetry.ExportCsv(
            DateTime.Now.AddDays(-_settings.TelemetryRetentionDays), DateTime.Now, target));

        Status = "Messwerte exportiert: " + target;
        Shell.Open(Path.GetDirectoryName(target)!);
    }

    private static string Fmt(double? v, string unit) => v is null ? "-" : $"{v.Value:0.#} {unit}";
}
