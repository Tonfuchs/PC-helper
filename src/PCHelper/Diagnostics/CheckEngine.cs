using PCHelper.Core;
using PCHelper.Knowledge;

namespace PCHelper.Diagnostics;

/// <summary>Gemeinsamer Zustand aller Pruefungen eines Durchlaufs.</summary>
public sealed class CheckContext
{
    public required SystemProfile Profile { get; init; }
    public required KnowledgeBase Knowledge { get; init; }

    /// <summary>Betrachteter Zeitraum fuer Ereignisprotokolle.</summary>
    public int LookbackDays { get; init; } = 30;

    /// <summary>Das gemeldete Symptom, sofern die Untersuchung gezielt laeuft.</summary>
    public Symptom? Symptom { get; init; }
}

/// <summary>Eine einzelne Pruefung. Neue Pruefungen einfach hier implementieren und in <see cref="CheckEngine.All"/> registrieren.</summary>
public interface ICheck
{
    string Name { get; }
    string Category { get; }

    /// <summary>
    /// Ursachenbereiche, zu denen diese Pruefung etwas beitragen kann. Bei einer
    /// gezielten Untersuchung laufen nur Pruefungen, deren Themen zum Symptom
    /// passen. Eine leere Liste bedeutet "gehoert zur Grundlage" und laeuft immer mit.
    /// </summary>
    IReadOnlyList<Cause> Topics => Array.Empty<Cause>();

    Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct);
}

/// <summary>Ergebnis eines vollstaendigen Diagnoselaufs.</summary>
public sealed class DiagnosisResult
{
    public required SystemProfile Profile { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<Suspicion> Suspicions { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;
    public TimeSpan Duration { get; init; }

    /// <summary>Das untersuchte Symptom - null bei einem vollstaendigen Rundumlauf.</summary>
    public Symptom? Symptom { get; init; }

    /// <summary>Anzahl ausgefuehrter Pruefungen (bei gezielter Untersuchung kleiner als die Gesamtzahl).</summary>
    public int ChecksRun { get; init; }

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public int OkCount => Findings.Count(f => f.Severity == Severity.Ok);
}

/// <summary>Fuehrt alle registrierten Pruefungen aus.</summary>
public static class CheckEngine
{
    /// <summary>Registrierung aller Pruefungen - Reihenfolge bestimmt die Anzeige.</summary>
    public static IReadOnlyList<ICheck> All() => new ICheck[]
    {
        new Checks.SystemOverviewCheck(),
        new Checks.DisplayConnectionCheck(),
        new Checks.DisplayDriverCrashCheck(),
        new Checks.GpuDriverCheck(),
        new Checks.TdrSettingsCheck(),
        new Checks.RestartHistoryCheck(),
        new Checks.UnexpectedShutdownCheck(),
        new Checks.BugCheckCheck(),
        new Checks.GpuHangPatternCheck(),
        new Checks.LiveKernelReportCheck(),
        new Checks.WheaCheck(),
        new Checks.MemoryConfigCheck(),
        new Checks.MemoryDiagnosticsCheck(),
        new Checks.FastStartupCheck(),
        new Checks.PowerSettingsCheck(),
        new Checks.ThermalThrottleCheck(),
        new Checks.LiveSensorCheck(),
        new Checks.GpuUtilizationDetailCheck(),
        new Checks.StorageHealthCheck(),
        new Checks.DiskSpaceCheck(),
        new Checks.BiosAgeCheck(),
        new Checks.OverlaySoftwareCheck(),
        new Checks.SystemIntegrityCheck(),
        new Checks.AudioDeviceCheck(),
        new Checks.MicrophoneAccessCheck(),
        new Checks.AudioServiceCheck(),
        new Checks.NetworkAdapterCheck(),
        new Checks.NetworkReachabilityCheck(),
        new Checks.NetworkEventCheck(),
        new Checks.ProblemDeviceCheck(),
        new Checks.UsbEventCheck(),
        new Checks.CameraAccessCheck(),
        new Checks.CpuLoadCheck(),
        new Checks.MemoryPressureCheck(),
        new Checks.StartupLoadCheck(),
        new Checks.KnowledgeBaseCheck(),
        new Checks.RecentErrorsCheck(),
    };

    /// <summary>
    /// Waehlt die Pruefungen aus, die zu einem Symptom etwas beitragen koennen.
    /// Pruefungen ohne Themenangabe gehoeren zur Grundlage und laufen immer mit.
    /// </summary>
    public static IReadOnlyList<ICheck> For(Symptom? symptom)
    {
        var all = All();
        if (symptom is null) return all;

        return all
            .Where(c => c.Topics.Count == 0 || c.Topics.Any(symptom.Causes.ContainsKey))
            .ToList();
    }

    /// <summary>
    /// Fuehrt die Diagnose aus. <paramref name="progress"/> meldet (erledigt, gesamt, Name).
    /// Eine fehlgeschlagene Pruefung bricht den Lauf nicht ab.
    /// Mit <paramref name="symptom"/> laufen nur die dazu passenden Pruefungen,
    /// und die Befunde werden nach ihrer Relevanz fuer das Symptom sortiert.
    /// </summary>
    public static async Task<DiagnosisResult> RunAsync(
        KnowledgeBase knowledge,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        Symptom? symptom = null,
        CancellationToken ct = default)
    {
        var started = DateTime.Now;

        progress?.Report((0, 1, "Systemdaten werden erfasst ..."));
        var profile = await SystemProfile.CollectAsync(ct);

        var checks = For(symptom);
        var ctx = new CheckContext { Profile = profile, Knowledge = knowledge, Symptom = symptom };
        var findings = new List<Finding>();

        for (int i = 0; i < checks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var check = checks[i];
            progress?.Report((i, checks.Count, check.Name));

            try
            {
                var result = await check.RunAsync(ctx, ct);
                findings.AddRange(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Pruefung '{check.Name}' fehlgeschlagen", ex);
                findings.Add(new Finding
                {
                    Id = "check-error-" + check.Name,
                    Category = check.Category,
                    Title = $"Pruefung '{check.Name}' nicht durchfuehrbar",
                    Severity = Severity.Info,
                    Summary = "Diese Pruefung konnte auf diesem System nicht ausgefuehrt werden.",
                    Detail = ex.Message,
                });
            }
        }

        progress?.Report((checks.Count, checks.Count, "Auswertung ..."));

        // Ohne Symptom entscheidet der Schweregrad. Mit Symptom steht zuerst,
        // was die gestellte Frage beantwortet - auch wenn es "nur" eine Warnung ist.
        var ordered = (symptom is null
                ? findings.OrderByDescending(f => f.Severity)
                : findings.OrderByDescending(f => f.RelevanceFor(symptom)).ThenByDescending(f => f.Severity))
            .ThenBy(f => f.Category, StringComparer.CurrentCulture)
            .ThenBy(f => f.Title, StringComparer.CurrentCulture)
            .ToList();

        return new DiagnosisResult
        {
            Profile = profile,
            Findings = ordered,
            Suspicions = SuspicionEngine.Rank(ordered, symptom),
            Duration = DateTime.Now - started,
            Symptom = symptom,
            ChecksRun = checks.Count,
        };
    }
}
