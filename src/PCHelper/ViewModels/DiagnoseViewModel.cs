using System.Collections.ObjectModel;
using System.Windows;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Knowledge;
using PCHelper.Monitoring;
using PCHelper.Reporting;

namespace PCHelper.ViewModels;

/// <summary>Steuert den Diagnoselauf und die Darstellung der Befunde.</summary>
public sealed class DiagnoseViewModel : ObservableObject
{
    private readonly KnowledgeBase _knowledge;
    private readonly IncidentStore _incidents;
    private readonly MonitorService _monitor;

    private DiagnosisResult? _result;
    private bool _isRunning;
    private string _progressText = "Noch keine Diagnose ausgefuehrt.";
    private double _progressValue;
    private bool _showOnlyProblems = true;

    public DiagnoseViewModel(KnowledgeBase knowledge, IncidentStore incidents, MonitorService monitor)
    {
        _knowledge = knowledge;
        _incidents = incidents;
        _monitor = monitor;

        RunCommand = new RelayCommand(_ => RunAsync(), _ => !IsRunning);
        ExportPdfCommand = new RelayCommand(_ => ExportPdf(), _ => Result is not null);
        ExportHtmlCommand = new RelayCommand(_ => ExportHtmlAsync(), _ => Result is not null);
        CopyMarkdownCommand = new RelayCommand(_ => CopyMarkdown(), _ => Result is not null);
    }

    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<Suspicion> Suspicions { get; } = new();

    /// <summary>Startet den vollstaendigen Rundumlauf ohne Vorannahme.</summary>
    public RelayCommand RunCommand { get; }

    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand ExportHtmlCommand { get; }
    public RelayCommand CopyMarkdownCommand { get; }

    public DiagnosisResult? Result
    {
        get => _result;
        private set
        {
            Set(ref _result, value);
            Raise(nameof(HasResult));
            Raise(nameof(SummaryText));
            Raise(nameof(SymptomTitle));
            Raise(nameof(HasSymptom));
            Raise(nameof(CriticalCount));
            Raise(nameof(WarningCount));
            Raise(nameof(OkCount));
            Raise(nameof(TopSuspicion));
            ExportPdfCommand.RaiseCanExecuteChanged();
            ExportHtmlCommand.RaiseCanExecuteChanged();
            CopyMarkdownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasResult => Result is not null;

    public bool IsRunning
    {
        get => _isRunning;
        private set { Set(ref _isRunning, value); RunCommand.RaiseCanExecuteChanged(); }
    }

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    /// <summary>Blendet die unauffaelligen Befunde aus.</summary>
    public bool ShowOnlyProblems
    {
        get => _showOnlyProblems;
        set { if (Set(ref _showOnlyProblems, value)) ApplyFilter(); }
    }

    public int CriticalCount => Result?.CriticalCount ?? 0;
    public int WarningCount => Result?.WarningCount ?? 0;
    public int OkCount => Result?.OkCount ?? 0;

    public Suspicion? TopSuspicion => Result?.Suspicions.FirstOrDefault();

    /// <summary>Das zuletzt untersuchte Symptom - null bei einem vollstaendigen Lauf.</summary>
    public string SymptomTitle => Result?.Symptom?.Title ?? "";

    public bool HasSymptom => Result?.Symptom is not null;

    public string SummaryText => Result is null
        ? "Die Diagnose prueft Hardware, Treiber, Ton, Netzwerk, Geraete, Energieeinstellungen und das " +
          "Ereignisprotokoll der letzten 30 Tage."
        : $"{Result.CriticalCount} kritisch, {Result.WarningCount} auffaellig, {Result.OkCount} in Ordnung " +
          $"(geprueft am {Result.CompletedAt:dd.MM.yyyy 'um' HH:mm}).";

    /// <summary>
    /// Fuehrt die Diagnose aus. Mit Symptom laufen nur die dazu passenden
    /// Pruefungen, und die Befunde stehen nach Relevanz statt nach Schweregrad.
    /// </summary>
    public async Task RunAsync(Symptom? symptom = null)
    {
        IsRunning = true;
        ProgressValue = 0;
        ProgressText = symptom is null
            ? "Diagnose wird vorbereitet ..."
            : $"Untersuchung zu '{symptom.Title}' wird vorbereitet ...";

        try
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                ProgressValue = p.Total <= 0 ? 0 : Math.Round(p.Done * 100.0 / p.Total, 0);
                ProgressText = $"{p.Name} ({p.Done}/{p.Total})";
            });

            var result = await CheckEngine.RunAsync(_knowledge, progress, symptom);
            Result = result;

            Suspicions.Clear();
            foreach (var s in result.Suspicions.Take(6)) Suspicions.Add(s);

            ApplyFilter();

            ProgressValue = 100;
            ProgressText = $"Fertig - {result.Findings.Count} Befunde aus {result.ChecksRun} Pruefungen " +
                           $"in {result.Duration.TotalSeconds:0.#} Sekunden.";
        }
        catch (Exception ex)
        {
            Log.Error("Diagnose fehlgeschlagen", ex);
            ProgressText = "Die Diagnose ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void ApplyFilter()
    {
        Findings.Clear();
        if (Result is null) return;

        var source = ShowOnlyProblems
            ? Result.Findings.Where(f => f.Severity is Severity.Critical or Severity.Warning or Severity.Info)
            : Result.Findings;

        foreach (var f in source) Findings.Add(f);
        Raise(nameof(FilterHint));
    }

    public string FilterHint => Result is null
        ? ""
        : ShowOnlyProblems
            ? $"{OkCount} unauffaellige Befunde ausgeblendet."
            : "Alle Befunde werden angezeigt.";

    /// <summary>
    /// PDF-Export. Laeuft bewusst auf dem Oberflaechen-Thread, weil die
    /// Textvermessung fuer den Zeilenumbruch WPF-Schriftarten benutzt.
    /// </summary>
    private void ExportPdf()
    {
        if (Result is null) return;

        var path = PdfReportBuilder.Write(Result, _incidents.LoadAll(20), _monitor.Telemetry);
        ProgressText = "PDF gespeichert: " + path;
        Shell.Open(path);
    }

    private async Task ExportHtmlAsync()
    {
        if (Result is null) return;

        var path = await Task.Run(() =>
            ReportBuilder.WriteHtml(Result, _incidents.LoadAll(20), _monitor.Telemetry));

        ProgressText = "Bericht gespeichert: " + path;
        Shell.Open(path);
    }

    private void CopyMarkdown()
    {
        if (Result is null) return;

        var markdown = ReportBuilder.BuildMarkdown(Result, _incidents.LoadAll(10));
        try
        {
            Clipboard.SetText(markdown);
            ProgressText = "Zusammenfassung in die Zwischenablage kopiert.";
        }
        catch (Exception ex)
        {
            Log.Error("Zwischenablage nicht verfuegbar", ex);
            ProgressText = "Die Zwischenablage war nicht verfuegbar.";
        }
    }
}
