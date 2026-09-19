using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Maintenance;

/// <summary>Ausgang einer Wartungsaktion. <see cref="Message"/> steht spaeter auf der Karte.</summary>
public sealed record ActionResult(bool Success, string Message);

/// <summary>
/// Ein Punkt der Wartungsansicht: ein Befund in normalem Deutsch, optional mit einem Knopf,
/// der etwas daran aendert. Das Pruefen selbst veraendert nie etwas.
/// </summary>
public sealed class MaintItem : ObservableObject
{
    private bool _intentional;
    private bool _busy;

    /// <summary>Stabiler Schluessel - Grundlage fuer "Ist Absicht".</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>Kurzer Chip neben dem Titel, z. B. "Registry" oder "Aufgabenplanung".</summary>
    public string Group { get; init; } = "";

    /// <summary>Messwert oder Zustand in Kurzform.</summary>
    public string Value { get; init; } = "";

    public string Meaning { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Info;

    /// <summary>Innerhalb desselben Schweregrads: hoeher steht weiter oben.</summary>
    public int Weight { get; init; }

    /// <summary>Beschriftung des Knopfes. Leer heisst: bewusst kein Knopf.</summary>
    public string ActionText { get; init; } = "";

    /// <summary>Text fuer eine einmalige Rueckfrage vor der Aktion. Leer heisst: keine Rueckfrage.</summary>
    public string Warning { get; init; } = "";

    /// <summary>Suchbegriff fuer "Im Web nachsehen". Geoeffnet wird nur auf Klick.</summary>
    public string WebSearch { get; init; } = "";

    /// <summary>Beschreibt, was die Aktion tut (fuer die Rueckfrage). Leer = nur Titel.</summary>
    public string ActionDetail { get; init; } = "";

    /// <summary>Vor der Aktion nachfragen, auch ohne <see cref="Warning"/> - bei allem, was sich nicht zuruecknehmen laesst.</summary>
    public bool Confirm { get; init; }

    public bool NeedsConfirmation => Confirm || HasWarning;

    public Func<Task<ActionResult>>? Execute { get; init; }

    public bool HasAction => Execute is not null && !string.IsNullOrEmpty(ActionText);
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasWebSearch => !string.IsNullOrEmpty(WebSearch);
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);
    public bool HasValue => !string.IsNullOrEmpty(Value);
    public bool HasGroup => !string.IsNullOrEmpty(Group);

    /// <summary>Nur auffaellige Punkte lassen sich als Absicht markieren - oder wieder zuruecknehmen.</summary>
    public bool CanBeIntentional => Severity is Severity.Warning or Severity.Critical;

    public bool IsIntentional
    {
        get => _intentional;
        set
        {
            if (!Set(ref _intentional, value)) return;
            Raise(nameof(DisplaySeverity));
            Raise(nameof(IntentionalText));
        }
    }

    /// <summary>Was die Oberflaeche zeigt: eine bewusste Entscheidung gilt als in Ordnung.</summary>
    public Severity DisplaySeverity => IsIntentional ? Severity.Ok : Severity;

    public string IntentionalText => IsIntentional ? "Doch wieder melden" : "Ist Absicht";

    public bool Busy { get => _busy; set => Set(ref _busy, value); }
}
