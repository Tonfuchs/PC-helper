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
}

/// <summary>Eine einzelne Pruefung. Neue Pruefungen einfach hier implementieren und in <see cref="CheckEngine.All"/> registrieren.</summary>
public interface ICheck
{
    string Name { get; }
    string Category { get; }
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
        new Checks.LiveKernelReportCheck(),
        new Checks.WheaCheck(),
        new Checks.MemoryConfigCheck(),
        new Checks.MemoryDiagnosticsCheck(),
        new Checks.FastStartupCheck(),
        new Checks.PowerSettingsCheck(),
        new Checks.ThermalThrottleCheck(),
        new Checks.LiveSensorCheck(),
        new Checks.StorageHealthCheck(),
        new Checks.DiskSpaceCheck(),
        new Checks.BiosAgeCheck(),
        new Checks.OverlaySoftwareCheck(),
        new Checks.SystemIntegrityCheck(),
        new Checks.KnowledgeBaseCheck(),
        new Checks.RecentErrorsCheck(),
    };

    /// <summary>
    /// Fuehrt die Diagnose aus. <paramref name="progress"/> meldet (erledigt, gesamt, Name).
    /// Eine fehlgeschlagene Pruefung bricht den Lauf nicht ab.
    /// </summary>
    public static async Task<DiagnosisResult> RunAsync(
        KnowledgeBase knowledge,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTime.Now;

        progress?.Report((0, 1, "Systemdaten werden erfasst ..."));
        var profile = await SystemProfile.CollectAsync(ct);

        var checks = All();
        var ctx = new CheckContext { Profile = profile, Knowledge = knowledge };
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

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category, StringComparer.CurrentCulture)
            .ThenBy(f => f.Title, StringComparer.CurrentCulture)
            .ToList();

        return new DiagnosisResult
        {
            Profile = profile,
            Findings = ordered,
            Suspicions = SuspicionEngine.Rank(ordered),
            Duration = DateTime.Now - started,
        };
    }
}
