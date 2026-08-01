using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCHelper.Core;

namespace PCHelper.Monitoring;

/// <summary>Ein Messpunkt der Dauerueberwachung.</summary>
public sealed class TelemetrySample
{
    [JsonPropertyName("t")] public DateTime Time { get; set; }
    [JsonPropertyName("cpu")] public double? CpuPercent { get; set; }
    [JsonPropertyName("ram")] public double? RamPercent { get; set; }
    [JsonPropertyName("ramGb")] public double? RamUsedGb { get; set; }
    [JsonPropertyName("gpuT")] public double? GpuTempC { get; set; }
    [JsonPropertyName("gpuU")] public double? GpuUtilPercent { get; set; }
    [JsonPropertyName("gpuW")] public double? GpuPowerWatt { get; set; }
    [JsonPropertyName("gpuC")] public double? GpuClockMhz { get; set; }
    [JsonPropertyName("gpuM")] public double? GpuMemoryMb { get; set; }
    [JsonPropertyName("gpuP")] public string? GpuState { get; set; }
    [JsonPropertyName("disp")] public int DisplayCount { get; set; }
    [JsonPropertyName("sig")] public string? DisplaySignature { get; set; }
    [JsonPropertyName("up")] public long UptimeSeconds { get; set; }

    /// <summary>Gesetzt, wenn zu diesem Zeitpunkt ein Ereignis auffiel (z. B. Monitor verloren).</summary>
    [JsonPropertyName("ev")] public string? Event { get; set; }

    [JsonIgnore] public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>
/// Schreibt Messreihen zeilenweise als JSON (JSONL) in Tagesdateien.
/// Bewusst simpel: auch ein hart abgeschalteter Rechner hinterlaesst so
/// alle Daten bis unmittelbar vor dem Ausfall.
/// </summary>
public sealed class TelemetryLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public void Append(TelemetrySample sample)
    {
        try
        {
            var line = JsonSerializer.Serialize(sample, JsonOptions);
            lock (_gate)
            {
                // FileShare.Read, damit die Datei parallel gelesen werden kann,
                // und ohne Puffer, damit bei Stromverlust nichts verloren geht.
                using var stream = new FileStream(PathForDay(sample.Time), FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Messwert konnte nicht geschrieben werden: " + ex.Message);
        }
    }

    /// <summary>Liest alle Messpunkte eines Zeitraums (ueber Tagesgrenzen hinweg).</summary>
    public List<TelemetrySample> Read(DateTime from, DateTime to)
    {
        var result = new List<TelemetrySample>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var path = PathForDay(day);
            if (!File.Exists(path)) continue;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var sample = JsonSerializer.Deserialize<TelemetrySample>(line, JsonOptions);
                        if (sample is not null && sample.Time >= from && sample.Time <= to) result.Add(sample);
                    }
                    catch { /* unvollstaendige letzte Zeile nach hartem Ausfall - erwartbar */ }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Messreihe {path} nicht lesbar: {ex.Message}");
            }
        }
        return result.OrderBy(s => s.Time).ToList();
    }

    /// <summary>Zeitpunkt des zuletzt geschriebenen Messpunkts (ueber alle Tagesdateien).</summary>
    public DateTime? LastSampleTime()
    {
        try
        {
            var newest = new DirectoryInfo(AppInfo.TelemetryDir)
                .GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            return newest?.LastWriteTime;
        }
        catch { return null; }
    }

    /// <summary>Loescht Messreihen, die aelter als die eingestellte Aufbewahrungsdauer sind.</summary>
    public void Cleanup(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var file in new DirectoryInfo(AppInfo.TelemetryDir).GetFiles("*.jsonl"))
                if (file.LastWriteTime < cutoff) file.Delete();
        }
        catch (Exception ex)
        {
            Log.Warn("Aufraeumen der Messreihen fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>Exportiert einen Zeitraum als CSV (z. B. fuer Tabellenkalkulation).</summary>
    public string ExportCsv(DateTime from, DateTime to, string targetPath)
    {
        var samples = Read(from, to);
        var sb = new StringBuilder();
        sb.AppendLine("Zeit;CPU %;RAM %;RAM GB;GPU °C;GPU %;GPU W;GPU MHz;GPU MB;Zustand;Bildschirme;Ereignis");

        foreach (var s in samples)
        {
            sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
              .Append(N(s.CpuPercent)).Append(';')
              .Append(N(s.RamPercent)).Append(';')
              .Append(N(s.RamUsedGb)).Append(';')
              .Append(N(s.GpuTempC)).Append(';')
              .Append(N(s.GpuUtilPercent)).Append(';')
              .Append(N(s.GpuPowerWatt)).Append(';')
              .Append(N(s.GpuClockMhz)).Append(';')
              .Append(N(s.GpuMemoryMb)).Append(';')
              .Append(s.GpuState ?? "").Append(';')
              .Append(s.DisplayCount).Append(';')
              .Append(s.Event ?? "")
              .AppendLine();
        }

        File.WriteAllText(targetPath, sb.ToString(), Encoding.UTF8);
        return targetPath;

        static string N(double? v) => v is null ? "" : v.Value.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));
    }

    private static string PathForDay(DateTime day)
        => Path.Combine(AppInfo.TelemetryDir, day.ToString("yyyy-MM-dd") + ".jsonl");
}
