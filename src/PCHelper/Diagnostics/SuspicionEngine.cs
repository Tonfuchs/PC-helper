namespace PCHelper.Diagnostics;

/// <summary>
/// Verdichtet die Einzelbefunde zu einer Rangliste moeglicher Ursachen.
/// Jeder Befund zahlt gewichtet auf Ursachenbereiche ein; der Schweregrad
/// bestimmt, wie stark er zaehlt.
/// </summary>
public static class SuspicionEngine
{
    private static double SeverityFactor(Severity s) => s switch
    {
        Severity.Critical => 3.0,
        Severity.Warning => 1.5,
        Severity.Info => 0.5,
        _ => 0.0,   // "In Ordnung" erhoeht keinen Verdacht
    };

    /// <summary>
    /// Wie stark ein Symptom die Bewertung verschiebt. Ein Befund zur Netzwerkkarte
    /// zaehlt bei "kein Internet" mehr als derselbe Befund bei "Bildschirm schwarz" -
    /// aber er verschwindet nie ganz, sonst waeren Ueberraschungsfunde nicht mehr sichtbar.
    /// </summary>
    private const double SymptomWeight = 1.0;

    public static IReadOnlyList<Suspicion> Rank(IReadOnlyList<Finding> findings, Symptom? symptom = null)
    {
        var scores = new Dictionary<Cause, double>();
        var evidence = new Dictionary<Cause, List<Finding>>();

        foreach (var f in findings)
        {
            var factor = SeverityFactor(f.Severity);
            if (factor <= 0 || f.Causes.Count == 0) continue;

            foreach (var (cause, weight) in f.Causes)
            {
                var prior = symptom is null ? 0 : symptom.Causes.GetValueOrDefault(cause);
                var boosted = weight * factor * (1 + prior * SymptomWeight);

                scores[cause] = scores.GetValueOrDefault(cause) + boosted;
                if (!evidence.TryGetValue(cause, out var list))
                    evidence[cause] = list = new List<Finding>();
                list.Add(f);
            }
        }

        if (scores.Count == 0) return Array.Empty<Suspicion>();

        var max = scores.Values.Max();
        var ranked = scores
            .Select(kv => new Suspicion
            {
                Cause = kv.Key,
                Score = Math.Round(kv.Value, 2),
                Evidence = evidence[kv.Key]
                    .OrderByDescending(f => f.Severity)
                    .ThenByDescending(f => f.Causes.GetValueOrDefault(kv.Key))
                    .ToList(),
                Percent = max <= 0 ? 0 : Math.Round(kv.Value / max * 100, 0),
            })
            .OrderByDescending(s => s.Score)
            .ToList();

        return ranked;
    }
}
