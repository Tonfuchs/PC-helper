using System.Windows;
using PCHelper.Core;
using PCHelper.Knowledge;
using PCHelper.Monitoring;
using PCHelper.Update;

namespace PCHelper.ViewModels;

/// <summary>Wurzel-ViewModel: Navigation, Update-Banner und Einstellungen.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly GitHubUpdateService _updateService;

    private string _currentPage = "dashboard";
    private UpdateInfo? _update;
    private string _updateStatus = "";
    private double _downloadProgress;
    private bool _isDownloading;

    public MainViewModel(Settings settings, KnowledgeBase knowledge, IncidentStore incidents, MonitorService monitor)
    {
        Settings = settings;
        Knowledge = knowledge;
        Monitor = monitor;

        _updateService = new GitHubUpdateService(settings);

        Diagnose = new DiagnoseViewModel(knowledge, incidents, monitor);
        Monitoring = new MonitorViewModel(monitor, incidents, settings);
        Fixes = new FixesViewModel();
        Tools = new ToolsViewModel();
        Symptoms = new SymptomViewModel(Diagnose, Tools, page => CurrentPage = page);

        NavigateCommand = new RelayCommand(p => CurrentPage = p as string ?? "dashboard");
        CheckUpdateCommand = new RelayCommand(_ => CheckForUpdateAsync(silent: false));
        InstallUpdateCommand = new RelayCommand(_ => InstallUpdateAsync(), _ => _update is not null && !_isDownloading);
        DismissUpdateCommand = new RelayCommand(_ => DismissUpdate());
        OpenRepoCommand = new RelayCommand(_ => Shell.Open(Settings.RepoUrl));
        OpenReleaseNotesCommand = new RelayCommand(_ => Shell.Open(_update?.ReleasePageUrl ?? Settings.RepoUrl + "/releases"));
        OpenLogCommand = new RelayCommand(_ => Shell.Open(AppInfo.LogFile));
        RefreshKnowledgeCommand = new RelayCommand(_ => RefreshKnowledgeAsync());
    }

    public Settings Settings { get; }
    public KnowledgeBase Knowledge { get; }
    public MonitorService Monitor { get; }

    public DiagnoseViewModel Diagnose { get; }
    public MonitorViewModel Monitoring { get; }
    public FixesViewModel Fixes { get; }
    public ToolsViewModel Tools { get; }
    public SymptomViewModel Symptoms { get; }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand InstallUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }
    public RelayCommand OpenRepoCommand { get; }
    public RelayCommand OpenReleaseNotesCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand RefreshKnowledgeCommand { get; }

    // ---------- Navigation ----------

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, value)) return;
            Raise(nameof(IsDashboard));
            Raise(nameof(IsSymptom));
            Raise(nameof(IsDiagnose));
            Raise(nameof(IsMonitor));
            Raise(nameof(IsFixes));
            Raise(nameof(IsTools));
            Raise(nameof(IsSettings));
            Raise(nameof(PageTitle));
            Raise(nameof(PageSubtitle));
        }
    }

    public bool IsDashboard => CurrentPage == "dashboard";
    public bool IsSymptom => CurrentPage == "symptom";
    public bool IsDiagnose => CurrentPage == "diagnose";
    public bool IsMonitor => CurrentPage == "monitor";
    public bool IsFixes => CurrentPage == "fixes";
    public bool IsTools => CurrentPage == "tools";
    public bool IsSettings => CurrentPage == "settings";

    public string PageTitle => CurrentPage switch
    {
        "symptom" => "Problem melden",
        "diagnose" => "Diagnose",
        "monitor" => "Dauerueberwachung",
        "fixes" => "Reparaturen",
        "tools" => "Werkzeuge",
        "settings" => "Einstellungen",
        _ => "Uebersicht",
    };

    public string PageSubtitle => CurrentPage switch
    {
        "symptom" => "Beschreiben, was nicht funktioniert - der Rest ergibt sich daraus",
        "diagnose" => "Pruefung von Hardware, Treibern, Ton, Netzwerk, Geraeten und Ereignisprotokoll",
        "monitor" => "Zeichnet fortlaufend auf, was kurz vor einem Ausfall passiert",
        "fixes" => "Nachvollziehbare Systemaenderungen, jederzeit umkehrbar",
        "tools" => "Bordmittel von Windows und bewaehrte Zusatzprogramme",
        "settings" => "Updates, Autostart und Aufzeichnung",
        _ => "Status auf einen Blick",
    };

    // ---------- Update ----------

    public UpdateInfo? Update
    {
        get => _update;
        private set
        {
            Set(ref _update, value);
            Raise(nameof(UpdateAvailable));
            Raise(nameof(UpdateHeadline));
            Raise(nameof(UpdateNotes));
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    public bool UpdateAvailable => _update is not null;

    public string UpdateHeadline => _update is null
        ? ""
        : $"Version {_update.Version} ist verfuegbar (installiert: {AppInfo.Version})" +
          (string.IsNullOrEmpty(_update.SizeText) ? "" : $" - {_update.SizeText}");

    public string UpdateNotes => _update?.Notes ?? "";

    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }
    public bool IsDownloading { get => _isDownloading; private set => Set(ref _isDownloading, value); }

    public string VersionText => $"{AppInfo.Name} {AppInfo.VersionDisplay}";

    public string KnowledgeText =>
        $"{Knowledge.Issues.Count} Eintraege, Quelle: {Knowledge.Source}" +
        (Knowledge.Updated is null ? "" : $", Stand {Knowledge.Updated}");

    /// <summary>Sucht nach Updates. Im stillen Modus bleiben Fehlermeldungen aus.</summary>
    public async Task CheckForUpdateAsync(bool silent)
    {
        UpdateStatus = "Es wird nach Updates gesucht ...";
        var info = await _updateService.CheckAsync();

        if (info is null)
        {
            Update = null;
            UpdateStatus = silent ? "" : $"Kein Update gefunden - {AppInfo.VersionDisplay} ist aktuell.";
            return;
        }

        if (silent && Settings.SkippedVersion == info.Version.ToString())
        {
            UpdateStatus = "";
            return;
        }

        Update = info;
        UpdateStatus = "";
    }

    private void DismissUpdate()
    {
        if (_update is not null)
        {
            Settings.SkippedVersion = _update.Version.ToString();
            Settings.Save();
        }
        Update = null;
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is null) return;

        IsDownloading = true;
        InstallUpdateCommand.RaiseCanExecuteChanged();
        UpdateStatus = "Update wird geladen ...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                UpdateStatus = $"Update wird geladen ... {p:0} %";
            });

            var file = await _updateService.DownloadAsync(_update, progress);

            var answer = MessageBox.Show(
                $"Version {_update.Version} ist bereit.\n\n" +
                "PC Helper wird jetzt beendet, ausgetauscht und neu gestartet.\n\nFortfahren?",
                "Update installieren", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (answer != MessageBoxResult.OK)
            {
                UpdateStatus = "Update liegt bereit und wird beim naechsten Mal angeboten.";
                return;
            }

            GitHubUpdateService.ApplyAndRestart(file);
            MonitorService.MarkCleanExit();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Update fehlgeschlagen", ex);
            UpdateStatus = "Update fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RefreshKnowledgeAsync()
    {
        UpdateStatus = "Wissensdatenbank wird geladen ...";
        var ok = await Knowledge.TryRefreshAsync(Settings.RawIssuesUrl);
        Raise(nameof(KnowledgeText));
        UpdateStatus = ok
            ? "Wissensdatenbank aktualisiert."
            : "Die Wissensdatenbank konnte nicht geladen werden - es gilt der eingebaute Stand.";
    }

    // ---------- Einstellungen (direkt an die Oberflaeche gebunden) ----------

    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set { Settings.StartWithWindows = value; Raise(); }
    }

    public bool AutoStartMonitoring
    {
        get => Settings.AutoStartMonitoring;
        set { Settings.AutoStartMonitoring = value; Settings.Save(); Raise(); }
    }

    public bool AutoCheckUpdates
    {
        get => Settings.AutoCheckUpdates;
        set { Settings.AutoCheckUpdates = value; Settings.Save(); Raise(); }
    }

    public bool AutoRefreshKnowledgeBase
    {
        get => Settings.AutoRefreshKnowledgeBase;
        set { Settings.AutoRefreshKnowledgeBase = value; Settings.Save(); Raise(); }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { Settings.MinimizeToTray = value; Settings.Save(); Raise(); }
    }

    public int SampleIntervalSeconds
    {
        get => Settings.SampleIntervalSeconds;
        set { Settings.SampleIntervalSeconds = Math.Clamp(value, 2, 120); Settings.Save(); Raise(); }
    }

    public int TelemetryRetentionDays
    {
        get => Settings.TelemetryRetentionDays;
        set { Settings.TelemetryRetentionDays = Math.Clamp(value, 1, 365); Settings.Save(); Raise(); }
    }

    public string RepoOwner
    {
        get => Settings.RepoOwner;
        set { Settings.RepoOwner = value; Settings.Save(); Raise(); }
    }

    public string RepoName
    {
        get => Settings.RepoName;
        set { Settings.RepoName = value; Settings.Save(); Raise(); }
    }
}
