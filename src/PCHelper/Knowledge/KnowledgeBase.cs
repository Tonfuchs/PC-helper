using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Knowledge;

/// <summary>Bedingungen, unter denen ein bekannter Fall auf dieses System zutrifft.</summary>
public sealed class IssueMatch
{
    public List<string>? GpuNameContains { get; set; }
    public List<string>? CpuNameContains { get; set; }
    public List<string>? BoardContains { get; set; }
    public List<string>? ProcessNameContains { get; set; }

    /// <summary>Trifft nur zu, wenn mindestens ein Monitor per DisplayPort angebunden ist.</summary>
    public bool? RequiresDisplayPort { get; set; }

    /// <summary>Trifft nur zu, wenn EXPO/XMP aktiv ist.</summary>
    public bool? MemoryOverclocked { get; set; }

    /// <summary>Trifft nur zu, wenn der Windows-Schnellstart aktiv ist.</summary>
    public bool? FastStartupEnabled { get; set; }

    /// <summary>Trifft zu, wenn das BIOS aelter als die angegebene Anzahl Tage ist.</summary>
    public int? BiosOlderThanDays { get; set; }
}

/// <summary>Ein Eintrag der Wissensdatenbank.</summary>
public sealed class KnownIssue
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "Bekannte Faelle";
    public string Severity { get; set; } = "info";
    public IssueMatch? Match { get; set; }
    public string Summary { get; set; } = "";
    public string? Detail { get; set; }
    public string? Recommendation { get; set; }
    public Dictionary<string, double>? Causes { get; set; }
    public List<KnownIssueLink>? Links { get; set; }
}

public sealed class KnownIssueLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class KnowledgeDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string? Updated { get; set; }
    public List<KnownIssue> Issues { get; set; } = new();
}

/// <summary>
/// Wissensdatenbank bekannter Problemmuster.
///
/// Die Datei liegt im Repo unter knowledge/known-issues.json und wird beim
/// Start per HTTPS nachgeladen. Dadurch koennen neue Erkenntnisse ohne
/// Programm-Update bei allen Nutzern wirksam werden; faellt der Abruf aus,
/// gilt die in der EXE eingebettete Kopie.
/// </summary>
public sealed class KnowledgeBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public IReadOnlyList<KnownIssue> Issues { get; private set; } = Array.Empty<KnownIssue>();
    public string Source { get; private set; } = "eingebettet";
    public string? Updated { get; private set; }

    private KnowledgeBase() { }

    /// <summary>Laedt die eingebettete Kopie (funktioniert immer, auch offline).</summary>
    public static KnowledgeBase LoadEmbedded()
    {
        var kb = new KnowledgeBase();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCHelper.known-issues.json");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                kb.Apply(reader.ReadToEnd(), "eingebettet");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Eingebettete Wissensdatenbank konnte nicht gelesen werden", ex);
        }
        return kb;
    }

    /// <summary>
    /// Versucht, die Wissensdatenbank aus dem Repo zu aktualisieren.
    /// Schlaegt der Abruf fehl, bleibt der bisherige Stand erhalten.
    /// </summary>
    public async Task<bool> TryRefreshAsync(string rawUrl, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PCHelper/{AppInfo.Version}");

            var json = await http.GetStringAsync(rawUrl, ct);
            if (Apply(json, "GitHub"))
            {
                Log.Info($"Wissensdatenbank aktualisiert ({Issues.Count} Eintraege, Stand {Updated ?? "unbekannt"}).");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Wissensdatenbank konnte nicht aktualisiert werden: " + ex.Message);
        }
        return false;
    }

    private bool Apply(string json, string source)
    {
        var doc = JsonSerializer.Deserialize<KnowledgeDocument>(json, JsonOptions);
        if (doc?.Issues is null || doc.Issues.Count == 0) return false;

        Issues = doc.Issues;
        Updated = doc.Updated;
        Source = source;
        return true;
    }

    /// <summary>Wertet alle Eintraege gegen das erhobene Systemprofil aus.</summary>
    public IEnumerable<Finding> Evaluate(SystemProfile profile)
    {
        foreach (var issue in Issues)
        {
            if (!Matches(issue.Match, profile)) continue;

            yield return new Finding
            {
                Id = "kb-" + issue.Id,
                Category = issue.Category,
                Title = issue.Title,
                Severity = ParseSeverity(issue.Severity),
                Summary = issue.Summary,
                Detail = issue.Detail,
                Recommendation = issue.Recommendation,
                Causes = ParseCauses(issue.Causes),
                Links = issue.Links?
                    .Select(l => new FindingLink { Label = l.Label, Url = l.Url })
                    .ToList() ?? new List<FindingLink>(),
            };
        }
    }

    private static bool Matches(IssueMatch? m, SystemProfile p)
    {
        if (m is null) return true;

        if (m.GpuNameContains is { Count: > 0 } &&
            !m.GpuNameContains.Any(needle => p.Gpus.Any(g => g.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (m.CpuNameContains is { Count: > 0 } &&
            !m.CpuNameContains.Any(needle => p.CpuName.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (m.BoardContains is { Count: > 0 } &&
            !m.BoardContains.Any(needle =>
                p.BoardProduct.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                p.BoardManufacturer.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (m.ProcessNameContains is { Count: > 0 } &&
            !m.ProcessNameContains.Any(needle =>
                p.RunningProcesses.Any(proc => proc.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                p.StartupEntries.Any(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase))))
            return false;

        if (m.RequiresDisplayPort == true && !p.HasDisplayPort) return false;
        if (m.MemoryOverclocked == true && !p.MemoryOverclocked) return false;
        if (m.FastStartupEnabled == true && !p.FastStartupEnabled) return false;

        if (m.BiosOlderThanDays is { } days)
        {
            if (p.BiosDate is null) return false;
            if ((DateTime.Now - p.BiosDate.Value).TotalDays < days) return false;
        }

        return true;
    }

    private static Severity ParseSeverity(string s) => s?.ToLowerInvariant() switch
    {
        "critical" or "kritisch" => Severity.Critical,
        "warning" or "warnung" => Severity.Warning,
        "ok" => Severity.Ok,
        _ => Severity.Info,
    };

    private static IReadOnlyDictionary<Cause, double> ParseCauses(Dictionary<string, double>? raw)
    {
        var result = new Dictionary<Cause, double>();
        if (raw is null) return result;

        foreach (var (key, value) in raw)
            if (Enum.TryParse<Cause>(key, ignoreCase: true, out var cause))
                result[cause] = value;

        return result;
    }
}
