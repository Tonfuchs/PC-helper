using System.Collections.ObjectModel;
using System.Windows;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Maintenance;

namespace PCHelper.ViewModels;

/// <summary>Ein Bereich der Wartungsansicht (Autostart, Platz schaffen, Geraete).</summary>
public sealed class MaintSection : ObservableObject
{
    private string _status = "";
    private bool _statusIsError;
    private bool _busy;
    private bool _scanned;
    private bool _current;
    private string _note = "";

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Intro { get; init; }
    public required string BusyText { get; init; }
    public required Func<CancellationToken, Task<IReadOnlyList<MaintItem>>> Scan { get; init; }

    /// <summary>Optionaler Hinweis, der nach dem Pruefen erscheint (z. B. fehlende Administratorrechte).</summary>
    public Func<string?>? NoteProvider { get; init; }

    public ObservableCollection<MaintItem> Items { get; } = new();

    public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(HasStatus)); } }
    public bool StatusIsError { get => _statusIsError; set => Set(ref _statusIsError, value); }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    public bool HasScanned { get => _scanned; set => Set(ref _scanned, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
    public string Note { get => _note; set { Set(ref _note, value); Raise(nameof(HasNote)); } }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Anzahl der Punkte, die noch als Problem zaehlen (bewusste Entscheidungen nicht mitgerechnet).</summary>
    public int ProblemCount => Items.Count(i => i.DisplaySeverity is Severity.Warning or Severity.Critical);

    public string Summary => !HasScanned
        ? ""
        : Items.Count == 0
            ? "Nichts gefunden."
            : $"{ProblemCount} Auffaelligkeit(en), {Items.Count} Punkte insgesamt.";

    public void Refresh()
    {
        Raise(nameof(ProblemCount));
        Raise(nameof(Summary));
    }
}

/// <summary>
/// Die Wartungsansicht: Werkzeuge, die nicht nur melden, sondern auf Knopfdruck etwas aendern. Pruefen ist immer
/// folgenlos; geaendert wird nur, was die Anwenderin ausdruecklich anklickt.
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly Settings _settings;
    private MaintSection _current;

    public MaintenanceViewModel(Settings settings)
    {
        _settings = settings;

        Sections = new ObservableCollection<MaintSection>
        {
            new()
            {
                Id = "startup", Title = "Autostart",
                Intro = "Was startet ungefragt mit? Auch die Wege, die der Task-Manager nicht zeigt: Aufgabenplanung und Dienste. " +
                        "Rauswerfen ist reversibel - das Programm bleibt installiert und laesst sich jederzeit normal starten.",
                BusyText = "Ich sehe alle Startwege durch ...",
                Scan = ct => StartupScanner.ScanAsync(_settings, ct),
            },
            new()
            {
                Id = "cleanup", Title = "Platz schaffen",
                Intro = "Was frisst den Platz? Erst ansehen, dann entscheiden. Geloescht wird nur auf Knopfdruck und konservativ; " +
                        "Downloads und Desktop werden nur gezeigt, nie angefasst.",
                BusyText = "Ich rechne zusammen, was wo liegt. Das dauert einen Moment ...",
                Scan = ct => CleanupScanner.ScanAsync(ct),
                NoteProvider = () => CleanupScanner.AdminNote,
            },
            new()
            {
                Id = "devices", Title = "Geraete",
                Intro = "Kamera, Ton, USB: was klemmt, warum - und der Knopf dagegen. \"Neu einstecken\" startet ein Geraet per " +
                        "Software neu, genau wie Kabel raus und wieder rein. Bei einem Geraet, das nur ab und zu ausfaellt, " +
                        "die Pruefung genau dann starten, wenn es gerade weg ist.",
                BusyText = "Ich sehe alle Geraete durch ...",
                Scan = ct => DeviceItems.ScanAsync(ct),
            },
        };

        _current = Sections[0];
        _current.IsCurrent = true;

        SelectCommand = new RelayCommand(p => SelectAsync(p as MaintSection));
        ScanCommand = new RelayCommand(p => ScanAsync(p as MaintSection ?? _current));
        RunCommand = new RelayCommand(p => RunAsync(p as MaintItem));
        IntentionalCommand = new RelayCommand(p => ToggleIntentional(p as MaintItem));
        SearchCommand = new RelayCommand(p => SearchWeb(p as MaintItem));
    }

    public ObservableCollection<MaintSection> Sections { get; }

    public MaintSection Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public RelayCommand SelectCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand IntentionalCommand { get; }
    public RelayCommand SearchCommand { get; }

    /// <summary>Beim ersten Oeffnen der Seite gleich den sichtbaren Bereich pruefen.</summary>
    public async void OnPageShown()
    {
        try
        {
            if (!Current.HasScanned) await ScanAsync(Current);
        }
        catch (Exception ex)
        {
            Log.Error("Wartung konnte nicht geladen werden", ex);
        }
    }

    public async Task SelectAsync(MaintSection? section)
    {
        if (section is null || section == Current) return;

        foreach (var s in Sections) s.IsCurrent = s == section;
        Current = section;
        if (!section.HasScanned) await ScanAsync(section);
    }

    private async Task ScanAsync(MaintSection section)
    {
        if (section.IsBusy) return;

        section.IsBusy = true;
        section.StatusIsError = false;
        section.Status = section.BusyText;

        try
        {
            var items = await section.Scan(CancellationToken.None);
            Populate(section, items);
            section.Note = section.NoteProvider?.Invoke() ?? "";
            section.Status = "";
        }
        catch (Exception ex)
        {
            Log.Error($"Wartung '{section.Id}' fehlgeschlagen", ex);
            section.StatusIsError = true;
            section.Status = "Die Pruefung ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            section.IsBusy = false;
        }
    }

    /// <summary>Erst was auffaellt, ganz unten die bewussten Entscheidungen.</summary>
    private void Populate(MaintSection section, IEnumerable<MaintItem> items)
    {
        section.Items.Clear();
        foreach (var item in items)
            item.IsIntentional = _settings.IntentionalItems.Contains(item.Key);

        foreach (var item in Sort(items)) section.Items.Add(item);
        section.HasScanned = true;
        section.Refresh();
    }

    private static IEnumerable<MaintItem> Sort(IEnumerable<MaintItem> items) => items
        .OrderBy(i => i.IsIntentional)
        .ThenByDescending(i => i.Severity)
        .ThenByDescending(i => i.Weight)
        .ThenBy(i => i.Title, StringComparer.CurrentCulture);

    private async Task RunAsync(MaintItem? item)
    {
        if (item?.Execute is null) return;
        var section = Sections.FirstOrDefault(s => s.Items.Contains(item)) ?? Current;

        if (item.NeedsConfirmation)
        {
            var text = item.Title;
            if (!string.IsNullOrWhiteSpace(item.ActionDetail)) text += "\n\n" + item.ActionDetail;
            if (item.HasWarning) text += "\n\n" + item.Warning;
            text += "\n\nFortfahren?";

            var answer = MessageBox.Show(text, item.ActionText,
                MessageBoxButton.OKCancel, item.HasWarning ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        item.Busy = true;
        section.StatusIsError = false;
        section.Status = $"'{item.Title}' wird bearbeitet ...";

        ActionResult result;
        try
        {
            result = await item.Execute();
        }
        catch (Exception ex)
        {
            Log.Error($"Wartungsaktion '{item.Key}' fehlgeschlagen", ex);
            result = new ActionResult(false, ex.Message);
        }
        finally
        {
            item.Busy = false;
        }

        // Nach jeder Aktion neu einlesen: gezeigt wird immer der wirkliche Zustand, nicht der erhoffte.
        await ScanAsync(section);
        section.StatusIsError = !result.Success;
        section.Status = result.Message;
    }

    private void ToggleIntentional(MaintItem? item)
    {
        if (item is null) return;

        item.IsIntentional = !item.IsIntentional;
        if (item.IsIntentional)
        {
            if (!_settings.IntentionalItems.Contains(item.Key)) _settings.IntentionalItems.Add(item.Key);
        }
        else
        {
            _settings.IntentionalItems.Remove(item.Key);
        }
        _settings.Save();

        var section = Sections.FirstOrDefault(s => s.Items.Contains(item));
        if (section is null) return;

        // Neu einsortieren: Absichten wandern nach unten, "doch wieder melden" nach oben.
        var sorted = Sort(section.Items.ToList()).ToList();
        section.Items.Clear();
        foreach (var i in sorted) section.Items.Add(i);
        section.Refresh();
    }

    /// <summary>Oeffnet eine Websuche im Browser - nur auf Klick, damit man sieht, wonach gesucht wird.</summary>
    private static void SearchWeb(MaintItem? item)
    {
        if (item is null || !item.HasWebSearch) return;
        Shell.Open("https://duckduckgo.com/?q=" + Uri.EscapeDataString(item.WebSearch));
    }
}
