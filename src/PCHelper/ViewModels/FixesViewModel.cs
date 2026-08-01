using System.Collections.ObjectModel;
using System.Windows;
using PCHelper.Core;
using PCHelper.Fixes;

namespace PCHelper.ViewModels;

/// <summary>Ein Katalogeintrag samt Ausfuehrungszustand fuer die Oberflaeche.</summary>
public sealed class FixItem : ObservableObject
{
    private string? _lastOutput;
    private bool _busy;

    public required Fix Fix { get; init; }

    public string Title => Fix.Title;
    public string Category => Fix.Category;
    public string Description => Fix.Description;
    public string Why => Fix.Why;
    public string RiskText => Fix.Risk == FixRisk.Gering ? "Risiko gering" : "Risiko mittel";
    public bool NeedsReboot => Fix.NeedsReboot;
    public string DurationText => Fix.Duration;
    public string CommandPreview => Fix.CommandPreview;
    public bool CanRevert => Fix.CanRevert;

    public string? LastOutput { get => _lastOutput; set { Set(ref _lastOutput, value); Raise(nameof(HasOutput)); } }
    public bool HasOutput => !string.IsNullOrWhiteSpace(LastOutput);
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
}

/// <summary>Verwaltet die Ein-Klick-Reparaturen.</summary>
public sealed class FixesViewModel : ObservableObject
{
    private bool _createRestorePoint = true;
    private string _status = "Alle Aenderungen sind nachvollziehbar - der genaue Befehl steht jeweils dabei.";

    public FixesViewModel()
    {
        foreach (var fix in FixCatalog.All)
            Items.Add(new FixItem { Fix = fix });

        ApplyCommand = new RelayCommand(p => ApplyAsync(p as FixItem, revert: false));
        RevertCommand = new RelayCommand(p => ApplyAsync(p as FixItem, revert: true));
        RestorePointCommand = new RelayCommand(_ => CreateRestorePointAsync());
    }

    public ObservableCollection<FixItem> Items { get; } = new();

    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand RestorePointCommand { get; }

    /// <summary>Vor der ersten Aenderung automatisch einen Wiederherstellungspunkt anlegen.</summary>
    public bool CreateRestorePoint { get => _createRestorePoint; set => Set(ref _createRestorePoint, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _restorePointDone;

    private async Task ApplyAsync(FixItem? item, bool revert)
    {
        if (item is null) return;

        var action = revert ? "zurueckgenommen" : "angewendet";
        var confirm = MessageBox.Show(
            $"{item.Title}\n\n{(revert ? item.Fix.RevertPreview : item.Fix.CommandPreview)}\n\n" +
            $"Diese Befehle werden als Administrator ausgefuehrt. Fortfahren?" +
            (item.NeedsReboot ? "\n\nDie Aenderung wirkt erst nach einem Neustart." : ""),
            $"Reparatur {(revert ? "zuruecknehmen" : "anwenden")}",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        item.Busy = true;
        Status = $"'{item.Title}' wird {action} ...";

        try
        {
            if (CreateRestorePoint && !_restorePointDone && !revert)
            {
                Status = "Wiederherstellungspunkt wird angelegt (das kann eine Minute dauern) ...";
                var rp = await FixRunner.CreateRestorePointAsync();
                _restorePointDone = true;
                if (!rp.Success)
                    Log.Warn("Wiederherstellungspunkt nicht angelegt: " + rp.Combined);
            }

            var result = await FixRunner.ApplyAsync(item.Fix, revert);
            item.LastOutput = string.IsNullOrWhiteSpace(result.Combined)
                ? (result.Success ? "Ohne Ausgabe abgeschlossen." : "Kein Ergebnis erhalten.")
                : result.Combined.Trim();

            Status = result.ExitCode == 1223
                ? "Abgebrochen - es wurde nichts geaendert."
                : result.Success
                    ? $"'{item.Title}' wurde {action}." + (item.NeedsReboot ? " Ein Neustart ist noetig." : "")
                    : $"'{item.Title}': Der Vorgang meldete einen Fehler - Details siehe Ausgabe.";
        }
        finally
        {
            item.Busy = false;
        }
    }

    private async Task CreateRestorePointAsync()
    {
        Status = "Wiederherstellungspunkt wird angelegt ...";
        var result = await FixRunner.CreateRestorePointAsync();
        _restorePointDone = result.Success;

        Status = result.Success
            ? "Wiederherstellungspunkt angelegt."
            : "Wiederherstellungspunkt fehlgeschlagen. Meist ist der Computerschutz in den Systemeigenschaften deaktiviert.";
    }
}
