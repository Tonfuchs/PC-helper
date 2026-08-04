using System.Collections.ObjectModel;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Fixes;

namespace PCHelper.ViewModels;

/// <summary>Eine Gruppe von Symptomen fuer die Darstellung.</summary>
public sealed class SymptomGroup
{
    public required string Name { get; init; }
    public ObservableCollection<Symptom> Items { get; } = new();
}

/// <summary>
/// "Was ist das Problem?" - Einstieg fuer alle, die kein Ereignisprotokoll lesen
/// wollen. Aus dem gewaehlten Symptom ergeben sich die passenden Pruefungen,
/// Sofortschritte, Reparaturen und Werkzeuge.
/// </summary>
public sealed class SymptomViewModel : ObservableObject
{
    private readonly DiagnoseViewModel _diagnose;
    private readonly ToolsViewModel _tools;
    private readonly Action<string> _navigate;

    private string _query = "";
    private Symptom? _selected;
    private string _resultHint = "";

    public SymptomViewModel(DiagnoseViewModel diagnose, ToolsViewModel tools, Action<string> navigate)
    {
        _diagnose = diagnose;
        _tools = tools;
        _navigate = navigate;

        SelectCommand = new RelayCommand(p => Selected = p as Symptom);
        ClearCommand = new RelayCommand(_ => { Query = ""; Selected = null; });
        StartDiagnosisCommand = new RelayCommand(_ => StartDiagnosis(), _ => Selected is not null);
        ShowFixesCommand = new RelayCommand(_ => _navigate("fixes"), _ => RecommendedFixes.Count > 0);
        ShowAllToolsCommand = new RelayCommand(_ => _navigate("tools"));

        Rebuild();
    }

    public ObservableCollection<SymptomGroup> Groups { get; } = new();
    public ObservableCollection<Fix> RecommendedFixes { get; } = new();
    public ObservableCollection<ToolItem> RecommendedTools { get; } = new();

    public RelayCommand SelectCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand StartDiagnosisCommand { get; }
    public RelayCommand ShowFixesCommand { get; }
    public RelayCommand ShowAllToolsCommand { get; }

    /// <summary>Freie Beschreibung des Problems in eigenen Worten.</summary>
    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value)) Rebuild(); }
    }

    public Symptom? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;

            RecommendedFixes.Clear();
            RecommendedTools.Clear();

            if (value is not null)
            {
                foreach (var id in value.FixIds)
                    if (FixCatalog.ById(id) is { } fix) RecommendedFixes.Add(fix);

                foreach (var tool in _tools.ByIds(value.ToolIds))
                    RecommendedTools.Add(tool);
            }

            Raise(nameof(HasSelection));
            Raise(nameof(FirstSteps));
            Raise(nameof(CauseSummary));
            Raise(nameof(CheckCountText));
            StartDiagnosisCommand.RaiseCanExecuteChanged();
            ShowFixesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public IReadOnlyList<string> FirstSteps => Selected?.FirstSteps ?? Array.Empty<string>();

    public string CauseSummary => Selected?.CauseSummary ?? "";

    /// <summary>Wie viele Pruefungen die gezielte Untersuchung ausfuehren wird.</summary>
    public string CheckCountText => Selected is null
        ? ""
        : $"Gezielte Untersuchung: {CheckEngine.For(Selected).Count} von {CheckEngine.All().Count} Pruefungen " +
          "laufen zu diesem Problem.";

    public string ResultHint { get => _resultHint; private set => Set(ref _resultHint, value); }

    /// <summary>Baut die Liste neu auf - gefiltert, sobald etwas eingegeben wurde.</summary>
    private void Rebuild()
    {
        Groups.Clear();

        if (string.IsNullOrWhiteSpace(Query))
        {
            foreach (var group in SymptomCatalog.ByGroup())
            {
                var g = new SymptomGroup { Name = group.Key };
                foreach (var s in group) g.Items.Add(s);
                Groups.Add(g);
            }

            ResultHint = $"{SymptomCatalog.All.Count} bekannte Faelle - oder das Problem oben einfach in " +
                         "eigenen Worten beschreiben.";
            return;
        }

        var matches = SymptomMatcher.Match(Query);
        if (matches.Count == 0)
        {
            ResultHint = "Dazu ist kein Fall hinterlegt. Andere Worte versuchen (zum Beispiel 'Mikrofon', " +
                         "'kein Ton', 'Internet', 'schwarz', 'langsam') - oder unten die vollstaendige Diagnose starten, " +
                         "die alles prueft.";
            return;
        }

        var hits = new SymptomGroup { Name = "Das koennte es sein" };
        foreach (var (symptom, _) in matches) hits.Items.Add(symptom);
        Groups.Add(hits);

        ResultHint = matches.Count == 1
            ? "Ein passender Fall gefunden."
            : $"{matches.Count} passende Faelle - der oberste passt am besten.";
    }

    private void StartDiagnosis()
    {
        if (Selected is null) return;

        _navigate("diagnose");
        _ = _diagnose.RunAsync(Selected);
    }
}
