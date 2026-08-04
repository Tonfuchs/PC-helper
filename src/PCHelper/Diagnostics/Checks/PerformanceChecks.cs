using System.Diagnostics;
using System.Text;
using PCHelper.Core;

namespace PCHelper.Diagnostics.Checks;

/// <summary>
/// Misst kurz die tatsaechliche Prozessorlast und benennt die Verursacher.
/// Ohne konkreten Prozessnamen bleibt "der PC ist langsam" eine Vermutung.
/// </summary>
public sealed class CpuLoadCheck : ICheck
{
    public string Name => "Prozessorlast";
    public string Category => "Leistung";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Cpu, Cause.Software, Cause.Thermal };

    /// <summary>Messfenster. Kurz genug, um nicht zu stoeren, lang genug fuer stabile Werte.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var before = Snapshot();
        var started = DateTime.UtcNow;
        await Task.Delay(Window, ct);
        var elapsed = DateTime.UtcNow - started;
        var after = Snapshot();

        int cores = Math.Max(Environment.ProcessorCount, 1);
        var usage = new List<(string Name, double Percent)>();

        foreach (var (id, sample) in after)
        {
            if (!before.TryGetValue(id, out var previous)) continue;
            var delta = (sample.Cpu - previous.Cpu).TotalMilliseconds;
            if (delta <= 0) continue;

            var percent = delta / (elapsed.TotalMilliseconds * cores) * 100;
            if (percent >= 0.5) usage.Add((sample.Name, Math.Round(percent, 1)));
        }

        var top = usage
            .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Percent: Math.Round(g.Sum(x => x.Percent), 1)))
            .OrderByDescending(u => u.Percent)
            .Take(8)
            .ToList();

        var total = Math.Round(Math.Min(usage.Sum(u => u.Percent), 100), 1);

        var sb = new StringBuilder();
        sb.AppendLine($"Messfenster: {elapsed.TotalMilliseconds:0} ms auf {cores} logischen Kernen.");
        sb.AppendLine($"Gesamtlast waehrend der Messung: rund {total:0.#} %.");
        sb.AppendLine();
        if (top.Count == 0) sb.AppendLine("Kein Prozess war nennenswert aktiv.");
        foreach (var t in top) sb.AppendLine($"  {t.Percent,6:0.#} %  {t.Name}");

        var severity = total switch
        {
            >= 80 => Severity.Warning,
            >= 45 => Severity.Info,
            _ => Severity.Ok,
        };

        var finding = new Finding
        {
            Id = "cpu-load",
            Category = Category,
            Title = severity == Severity.Ok ? "Prozessorlast ist unauffaellig" : "Erhoehte Prozessorlast gemessen",
            Severity = severity,
            Summary = top.Count == 0
                ? $"Gesamtlast rund {total:0.#} %."
                : $"Gesamtlast rund {total:0.#} %, staerkster Verursacher: {top[0].Name} ({top[0].Percent:0.#} %).",
            Detail = sb.ToString().TrimEnd() +
                     "\n\nHinweis: Das ist eine Momentaufnahme von unter einer Sekunde. Fuer wiederkehrende " +
                     "Lastspitzen ist die Dauerueberwachung dieser App die bessere Quelle.",
            Recommendation = severity == Severity.Ok
                ? null
                : "Den staerksten Verursacher im Task-Manager wiederfinden und einordnen: Gehoert er zu Windows " +
                  "(Update, Suche, Virenschutz), hoert die Last meist von selbst auf. Gehoert er zu einem Programm, " +
                  "das gar nicht laufen muesste, aus dem Autostart nehmen.",
            Causes = severity == Severity.Ok
                ? new Dictionary<Cause, double>()
                : new Dictionary<Cause, double> { [Cause.Cpu] = 0.7, [Cause.Software] = 0.5, [Cause.Thermal] = 0.2 },
            SymptomIds = new[] { "perf-cpu-high", "perf-slow" },
        };

        return new[] { finding };
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot()
    {
        var map = new Dictionary<int, (string, TimeSpan)>();
        foreach (var proc in Process.GetProcesses())
        {
            try { map[proc.Id] = (proc.ProcessName, proc.TotalProcessorTime); }
            catch { /* geschuetzter Prozess - nicht lesbar */ }
            finally { proc.Dispose(); }
        }
        return map;
    }
}

/// <summary>Belegung des Arbeitsspeichers und die groessten Verbraucher.</summary>
public sealed class MemoryPressureCheck : ICheck
{
    public string Name => "Speicherauslastung";
    public string Category => "Leistung";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Memory, Cause.Cpu, Cause.Software };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var (loadPercent, usedGb, totalGb) = Native.GetMemoryStatus();
        if (double.IsNaN(loadPercent))
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "memory-load", Category = Category, Title = "Speicherauslastung nicht messbar",
                    Severity = Severity.Info,
                    Summary = "Die Speicherbelegung konnte nicht ermittelt werden.",
                }
            });
        }

        var top = new List<(string Name, double Mb)>();
        foreach (var proc in Process.GetProcesses())
        {
            try { top.Add((proc.ProcessName, proc.WorkingSet64 / (1024.0 * 1024))); }
            catch { /* nicht lesbar */ }
            finally { proc.Dispose(); }
        }

        var biggest = top
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Mb: g.Sum(x => x.Mb)))
            .OrderByDescending(t => t.Mb)
            .Take(8)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Belegt: {usedGb:0.#} GB von {totalGb:0.#} GB ({loadPercent:0} %).");
        sb.AppendLine();
        foreach (var b in biggest) sb.AppendLine($"  {b.Mb,8:0} MB  {b.Name}");

        var severity = loadPercent switch
        {
            >= 92 => Severity.Warning,
            >= 80 => Severity.Info,
            _ => Severity.Ok,
        };

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "memory-load", Category = Category,
                Title = severity == Severity.Ok ? "Arbeitsspeicher ist ausreichend frei" : "Arbeitsspeicher ist knapp",
                Severity = severity,
                Summary = $"{usedGb:0.#} GB von {totalGb:0.#} GB belegt ({loadPercent:0} %)." +
                          (biggest.Count > 0 ? $" Groesster Verbraucher: {biggest[0].Name}." : ""),
                Detail = sb.ToString().TrimEnd(),
                Recommendation = severity == Severity.Ok
                    ? null
                    : "Bei dauerhaft ueber 90 % lagert Windows staendig auf den Datentraeger aus - das fuehlt sich " +
                      "wie ein langsamer Rechner an, obwohl CPU und SSD in Ordnung sind. Speicherhungrige Programme " +
                      "schliessen oder den Arbeitsspeicher erweitern.",
                Causes = severity == Severity.Ok
                    ? new Dictionary<Cause, double>()
                    : new Dictionary<Cause, double> { [Cause.Memory] = 0.5, [Cause.Software] = 0.4, [Cause.Storage] = 0.2 },
                SymptomIds = new[] { "perf-slow" },
            }
        });
    }
}

/// <summary>Autostart-Ballast als haeufigster Grund fuer lange Startzeiten.</summary>
public sealed class StartupLoadCheck : ICheck
{
    public string Name => "Autostart";
    public string Category => "Leistung";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Software, Cause.Cpu, Cause.Storage };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var entries = ctx.Profile.StartupEntries;
        var detail = entries.Count == 0
            ? "Es sind keine Autostart-Eintraege registriert."
            : string.Join("\n", entries.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.CurrentCulture)
                .Select(e => "  - " + e));

        var severity = entries.Count switch
        {
            > 20 => Severity.Warning,
            > 10 => Severity.Info,
            _ => Severity.Ok,
        };

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "startup-load", Category = Category,
                Title = severity == Severity.Ok ? "Autostart ist ueberschaubar" : "Viele Programme starten mit Windows",
                Severity = severity,
                Summary = $"{entries.Count} Eintrag/Eintraege starten automatisch mit.",
                Detail = detail,
                Recommendation = severity == Severity.Ok
                    ? null
                    : "Task-Manager > Autostart oeffnen und alles deaktivieren, was nicht sofort beim Anmelden " +
                      "gebraucht wird. Das verkuerzt die Startzeit spuerbar und nimmt gleichzeitig moegliche " +
                      "Stoerquellen aus dem Spiel - deaktivieren ist jederzeit umkehrbar.",
                Causes = severity == Severity.Ok
                    ? new Dictionary<Cause, double>()
                    : new Dictionary<Cause, double> { [Cause.Software] = 0.5, [Cause.Storage] = 0.2 },
                SymptomIds = new[] { "win-boot-slow", "perf-slow" },
            }
        });
    }
}
