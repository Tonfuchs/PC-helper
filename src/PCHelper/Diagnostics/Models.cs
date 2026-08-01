namespace PCHelper.Diagnostics;

/// <summary>Bewertung eines Befunds.</summary>
public enum Severity
{
    /// <summary>Reine Information, kein Handlungsbedarf.</summary>
    Info = 0,
    /// <summary>Geprueft und in Ordnung.</summary>
    Ok = 1,
    /// <summary>Auffaellig - sollte angesehen werden.</summary>
    Warning = 2,
    /// <summary>Deutlicher Hinweis auf die Fehlerursache.</summary>
    Critical = 3,
}

/// <summary>Moegliche Ursachenbereiche, auf die Befunde einzahlen.</summary>
public enum Cause
{
    GpuDriver,
    DisplayLink,
    Memory,
    PowerSupply,
    PowerSettings,
    Thermal,
    Storage,
    Software,
    Bios,
    OperatingSystem,
}

public static class CauseInfo
{
    public static string Title(Cause c) => c switch
    {
        Cause.GpuDriver => "Grafiktreiber / GPU",
        Cause.DisplayLink => "Monitorverbindung (Kabel, DisplayPort)",
        Cause.Memory => "Arbeitsspeicher / EXPO",
        Cause.PowerSupply => "Stromversorgung / Netzteil",
        Cause.PowerSettings => "Windows-Energieeinstellungen",
        Cause.Thermal => "Temperatur / Kuehlung",
        Cause.Storage => "Datentraeger",
        Cause.Software => "Software / Treiber von Drittanbietern",
        Cause.Bios => "BIOS / Mainboard",
        Cause.OperatingSystem => "Windows / Systemdateien",
        _ => c.ToString()
    };

    public static string Hint(Cause c) => c switch
    {
        Cause.GpuDriver => "Der Grafiktreiber setzt sich zurueck oder stuerzt ab. Typisch: Bild kurz weg, Ton laeuft weiter.",
        Cause.DisplayLink => "Die Signalstrecke zum Monitor bricht ab. Typisch: Bild komplett weg, Monitor meldet 'kein Signal', Ton laeuft weiter.",
        Cause.Memory => "Speicherfehler wirken sich zufaellig aus: mal Absturz, mal Schwarzbild, mal gar nichts.",
        Cause.PowerSupply => "Die Stromversorgung bricht unter Lastspitzen ein. Typisch: Rechner geht komplett aus oder startet neu.",
        Cause.PowerSettings => "Windows schaltet Komponenten ab oder haelt Zustaende ueber Neustarts hinweg fest.",
        Cause.Thermal => "Bauteile werden zu warm und drosseln oder schalten ab.",
        Cause.Storage => "Lesefehler oder Aussetzer des Datentraegers koennen das System kurzzeitig einfrieren.",
        Cause.Software => "Overlays, Tuning- und RGB-Software greifen tief ins System ein.",
        Cause.Bios => "Veraltete Firmware verursacht Probleme mit Speicher und PCIe-Anbindung.",
        Cause.OperatingSystem => "Beschaedigte Systemdateien oder ein fehlerhaftes Update.",
        _ => ""
    };
}

/// <summary>Weiterfuehrender Link zu einem Befund.</summary>
public sealed class FindingLink
{
    public required string Label { get; init; }
    public required string Url { get; init; }
}

/// <summary>Ein einzelner Prueferergebnis-Eintrag.</summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>Einzeiler fuer die Uebersicht.</summary>
    public required string Summary { get; init; }

    /// <summary>Ausfuehrliche Details (Messwerte, Ereignisse, Rohdaten).</summary>
    public string? Detail { get; init; }

    /// <summary>Was der Nutzer tun sollte.</summary>
    public string? Recommendation { get; init; }

    /// <summary>IDs passender Ein-Klick-Reparaturen aus dem <see cref="Fixes.FixCatalog"/>.</summary>
    public IReadOnlyList<string> FixIds { get; init; } = Array.Empty<string>();

    /// <summary>Gewichtung auf Ursachenbereiche (0..1), Grundlage der Verdachtsliste.</summary>
    public IReadOnlyDictionary<Cause, double> Causes { get; init; } = new Dictionary<Cause, double>();

    /// <summary>Weiterfuehrende Links.</summary>
    public IReadOnlyList<FindingLink> Links { get; init; } = Array.Empty<FindingLink>();

    /// <summary>Anzahl betroffener Ereignisse, falls zutreffend.</summary>
    public int? Occurrences { get; init; }

    /// <summary>Zeitpunkt des juengsten zugehoerigen Ereignisses.</summary>
    public DateTime? LastOccurrence { get; init; }

    public string SeverityText => Severity switch
    {
        Severity.Critical => "Kritisch",
        Severity.Warning => "Auffaellig",
        Severity.Ok => "In Ordnung",
        _ => "Info"
    };
}

/// <summary>Ein Eintrag der Verdachtsliste.</summary>
public sealed class Suspicion
{
    public required Cause Cause { get; init; }
    public required double Score { get; init; }
    public required IReadOnlyList<Finding> Evidence { get; init; }

    public string Title => CauseInfo.Title(Cause);
    public string Hint => CauseInfo.Hint(Cause);

    /// <summary>Score als Prozentwert relativ zum staerksten Verdacht.</summary>
    public double Percent { get; set; }

    public string Rank => Percent switch
    {
        >= 70 => "Hauptverdacht",
        >= 40 => "Wahrscheinlich",
        >= 15 => "Moeglich",
        _ => "Unwahrscheinlich"
    };
}
