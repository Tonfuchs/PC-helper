using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Der Energiesparmodus als unsichtbare Handbremse.</summary>
public sealed class PowerPlanCheck : ICheck
{
    public string Name => "Energieplan";
    public string Category => "Energie";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.PowerSettings, Cause.Cpu };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var plan = ctx.Profile.PowerPlanName;
        bool saver = Regex.IsMatch(plan, "Energiesparmodus|Power saver", RegexOptions.IgnoreCase);

        var finding = saver
            ? new Finding
            {
                Id = "power-plan", Category = Category, Title = "Der Energieplan steht auf Sparen",
                Severity = Severity.Warning,
                Summary = $"Aktiver Plan: {plan}. Windows drosselt in diesem Modus absichtlich den Prozessor.",
                Detail = "Auf einem Rechner, der spielen, streamen oder schwere Programme ausfuehren soll, ist das eine unsichtbare " +
                         "Handbremse: alles fuehlt sich traege an, obwohl die Hardware genug koennte.",
                Recommendation = "Auf \"Ausbalanciert\" oder \"Hoechstleistung\" umstellen.",
                Causes = new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.5, [Cause.Cpu] = 0.5 },
                SymptomIds = new[] { "perf-slow", "perf-cpu-high" },
                FixIds = new[] { "power-balanced", "power-high-performance" },
            }
            : new Finding
            {
                Id = "power-plan", Category = Category, Title = "Der Energieplan ist in Ordnung",
                Severity = Severity.Ok,
                Summary = $"Aktiver Plan: {plan}.",
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}

/// <summary>
/// Wie lange laeuft der aktuelle Windows-Zustand schon? Bei eingeschaltetem Schnellstart ist "Herunterfahren" kein echter
/// Neustart - haengende Verbindungen und muede Dienste ueberleben das Ausschalten und schleppen sich tagelang mit.
/// </summary>
public sealed class LastRealBootCheck : ICheck
{
    public string Name => "Letzter echter Start";
    public string Category => "Windows";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.OperatingSystem, Cause.PowerSettings, Cause.Software, Cause.Network };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var boot = ctx.Profile.LastBootTime;
        if (boot is null) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        int days = (int)(DateTime.Now - boot.Value).TotalDays;
        bool fast = ctx.Profile.FastStartupEnabled;
        bool stale = days > 7 || (fast && days > 2);

        var finding = stale
            ? new Finding
            {
                Id = "last-real-boot", Category = Category,
                Title = $"Der letzte echte Start liegt {days} Tage zurueck",
                Severity = Severity.Warning,
                Summary = fast
                    ? $"Dieser Windows-Zustand laeuft seit {days} Tagen ununterbrochen, obwohl der Rechner zwischendurch aus war: " +
                      "Der Schnellstart macht aus \"Herunterfahren\" einen Winterschlaf."
                    : $"Dieser Windows-Zustand laeuft seit {days} Tagen ununterbrochen.",
                Detail = $"Letzter echter Start: {boot.Value:dd.MM.yyyy HH:mm}.\n\nJe laenger das her ist, desto mehr Kleinkram sammelt sich " +
                         "an: Arbeitsspeicherreste, haengende Verbindungen, Dienste, die sich verrannt haben.",
                Recommendation = "Einmal \"Neu starten\" waehlen statt \"Herunterfahren\" - das ist immer ein echter Neustart, auch bei " +
                                 "eingeschaltetem Schnellstart." + (fast ? " Dauerhaft hilft es, den Schnellstart abzuschalten." : ""),
                Causes = new Dictionary<Cause, double> { [Cause.OperatingSystem] = 0.3, [Cause.PowerSettings] = fast ? 0.4 : 0.1 },
                SymptomIds = new[] { "perf-slow", "net-slow", "net-drops" },
                FixIds = fast ? new[] { "fast-startup-off" } : Array.Empty<string>(),
            }
            : new Finding
            {
                Id = "last-real-boot", Category = Category, Title = "Der letzte echte Start ist noch nicht lange her",
                Severity = Severity.Ok,
                Summary = $"Letzter echter Start: {boot.Value:dd.MM.yyyy HH:mm} (vor {days} Tagen).",
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}

/// <summary>Zwei aktive Virenscanner kommen sich gegenseitig in die Quere und bremsen spuerbar.</summary>
public sealed class SecurityProductCheck : ICheck
{
    public string Name => "Virenschutz";
    public string Category => "Software";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Software, Cause.Cpu, Cause.Storage };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        // Windows fuehrt neben einem Drittanbieter-Scanner meist auch den Defender - aber ausgeschaltet. Gezaehlt wird
        // deshalb nur, was wirklich aktiv ist: Bits 12 bis 15 des Produktzustands, 1 = eingeschaltet.
        var active = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2")
            .Where(r => r.Long("productState") is { } state && ((state >> 12) & 0xF) == 1)
            .Select(r => r.Str("displayName"))
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        if (active.Count == 0) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        var finding = active.Count > 1
            ? new Finding
            {
                Id = "security-products", Category = Category, Title = "Mehrere Virenschutz-Programme sind aktiv",
                Severity = Severity.Warning,
                Summary = $"Gleichzeitig aktiv: {string.Join(", ", active)}.",
                Detail = "Sie pruefen dieselben Dateien doppelt und bremsen dadurch spuerbar - besonders beim Oeffnen von Programmen " +
                         "und beim Herunterladen. Ausserdem stoeren sie sich gegenseitig.",
                Recommendation = "Auf einen reduzieren. Der Windows Defender allein reicht fuer die allermeisten Faelle voellig.",
                Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.5, [Cause.Cpu] = 0.2, [Cause.Storage] = 0.2 },
                SymptomIds = new[] { "perf-slow", "win-boot-slow" },
            }
            : new Finding
            {
                Id = "security-products", Category = Category, Title = "Genau ein Virenschutz ist aktiv",
                Severity = Severity.Ok,
                Summary = active[0] + ".",
            };

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}

/// <summary>Sehr viele laufende Programme machen den Rechner traege, auch wenn keines davon auffaellt.</summary>
public sealed class ProcessCountCheck : ICheck
{
    public string Name => "Anzahl laufender Prozesse";
    public string Category => "Leistung";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Software, Cause.Cpu, Cause.Memory };

    private const int Limit = 300;

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        int count;
        try
        {
            var processes = Process.GetProcesses();
            count = processes.Length;
            foreach (var p in processes) p.Dispose();
        }
        catch
        {
            return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());
        }

        if (count <= Limit) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "process-count", Category = Category, Title = "Sehr viele Prozesse laufen gleichzeitig",
                Severity = Severity.Info,
                Summary = $"Gerade laufen {count} Prozesse. Das ist viel.",
                Detail = "Jeder einzelne will ab und zu Rechenzeit, und in Summe macht das den Rechner traege - auch wenn keiner davon " +
                         "auffaellt.",
                Recommendation = "In PC Helper unter \"Wartung\" > \"Autostart\" nachsehen, was davon ungefragt mitgestartet wird.",
                Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.3, [Cause.Cpu] = 0.2 },
                SymptomIds = new[] { "perf-slow", "win-boot-slow" },
            }
        });
    }
}
