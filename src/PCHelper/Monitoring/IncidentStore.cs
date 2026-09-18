using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCHelper.Core;

namespace PCHelper.Monitoring;

public enum IncidentKind
{
    /// <summary>Vom Nutzer selbst gemeldet ("es ist gerade passiert").</summary>
    Manual,
    /// <summary>Die Anzeigekonfiguration hat sich unerwartet geaendert.</summary>
    DisplayChange,
    /// <summary>Die vorherige Sitzung endete ohne sauberes Beenden.</summary>
    UncleanShutdown,
    /// <summary>Rueckkehr aus dem Energiesparmodus.</summary>
    PowerResume,
    /// <summary>Windows wurde heruntergefahren oder neu gestartet.</summary>
    SystemShutdown,
    /// <summary>Ein Neustart, den der Nutzer als Folge eines Schwarzbildes bestaetigt hat.</summary>
    BlackscreenRestart,
    /// <summary>Die Grafikkarte antwortet nicht mehr, Windows laeuft aber weiter (GPU-Haenger).</summary>
    GpuUnresponsive,
}

/// <summary>Ein festgehaltener Vorfall samt Zeitpunkt und Kurzbeschreibung.</summary>
public sealed class Incident
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Time { get; set; } = DateTime.Now;
    public IncidentKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public string? DisplaySignatureBefore { get; set; }
    public string? DisplaySignatureAfter { get; set; }

    [JsonIgnore]
    public string KindText => Kind switch
    {
        IncidentKind.Manual => "Selbst gemeldet",
        IncidentKind.DisplayChange => "Anzeige veraendert",
        IncidentKind.UncleanShutdown => "Unsauberes Sitzungsende",
        IncidentKind.PowerResume => "Aufwachen aus Energiesparmodus",
        IncidentKind.SystemShutdown => "Neustart / Herunterfahren",
        IncidentKind.BlackscreenRestart => "Neustart wegen Schwarzbild",
        IncidentKind.GpuUnresponsive => "Grafikkarte antwortet nicht mehr",
        _ => Kind.ToString()
    };

    [JsonIgnore]
    public string TimeText => Time.ToString("dd.MM.yyyy HH:mm:ss");

    [JsonIgnore]
    public string Display => $"{TimeText}  -  {Title}";
}

/// <summary>Ablage der Vorfaelle als einzelne JSON-Dateien.</summary>
public sealed class IncidentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Incident Add(IncidentKind kind, string title, string? note = null,
                        string? before = null, string? after = null, DateTime? time = null)
    {
        var incident = new Incident
        {
            Time = time ?? DateTime.Now,
            Kind = kind,
            Title = title,
            Note = note,
            DisplaySignatureBefore = before,
            DisplaySignatureAfter = after,
        };

        try
        {
            var path = Path.Combine(AppInfo.IncidentDir,
                $"{incident.Time:yyyy-MM-dd_HH-mm-ss}_{incident.Id}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(incident, JsonOptions));
            Log.Info($"Vorfall festgehalten: {kind} - {title}");
        }
        catch (Exception ex)
        {
            Log.Error("Vorfall konnte nicht gespeichert werden", ex);
        }

        return incident;
    }

    /// <summary>Liest alle gespeicherten Vorfaelle, neueste zuerst.</summary>
    public List<Incident> LoadAll(int max = 200)
    {
        var list = new List<Incident>();
        try
        {
            foreach (var file in new DirectoryInfo(AppInfo.IncidentDir)
                         .GetFiles("*.json")
                         .OrderByDescending(f => f.Name)
                         .Take(max))
            {
                try
                {
                    var incident = JsonSerializer.Deserialize<Incident>(File.ReadAllText(file.FullName), JsonOptions);
                    if (incident is not null) list.Add(incident);
                }
                catch { /* beschaedigte Datei ueberspringen */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Vorfaelle konnten nicht gelesen werden: " + ex.Message);
        }
        return list.OrderByDescending(i => i.Time).ToList();
    }

    public void Delete(Incident incident)
    {
        try
        {
            foreach (var file in new DirectoryInfo(AppInfo.IncidentDir).GetFiles($"*{incident.Id}.json"))
                file.Delete();
        }
        catch (Exception ex)
        {
            Log.Warn("Vorfall konnte nicht geloescht werden: " + ex.Message);
        }
    }
}
