using System.Globalization;
using System.IO;
using System.Text;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Monitoring;

/// <summary>
/// Flugschreiber fuer die Grafikkarte.
///
/// Hintergrund: Bei einem GPU-Haenger (Bild schwarz, Ton laeuft weiter) bleibt von den
/// Detailwerten der Live-Anzeige - PCIe-Link, Drosselung, Leistung - nach dem erzwungenen
/// Neustart nichts uebrig. Diese Datei haelt sie zeilenweise fest und schreibt jede Zeile
/// direkt auf die Platte. Unter hoher GPU-Last misst die Ueberwachung jede Sekunde, damit auch
/// ein Ausfall nach wenigen Sekunden noch Werte hinterlaesst.
///
/// Zwei Dateien (aktuell + alt) begrenzen den Platzbedarf auf wenige MB.
/// </summary>
public static class GpuBlackbox
{
    /// <summary>Marker in der Hinweis-Spalte, wenn die Karte nicht mehr abfragbar war.</summary>
    public const string LostMarker = "GPU NICHT MEHR ABFRAGBAR";

    /// <summary>Marker, wenn die Karte wieder geantwortet hat.</summary>
    public const string BackMarker = "GPU WIEDER ERREICHBAR";

    private const long MaxBytes = 1_500_000;
    private const string Header =
        "Zeit;Last %;Temp C;Leistung W;Limit W;Takt MHz;VRAM MB;PCIe Gen (jetzt/max);PCIe Breite (jetzt/max);Drosselung;Luefter %;Zustand;Hinweis";

    private static readonly object Gate = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Path => System.IO.Path.Combine(AppInfo.DataDir, "gpu-blackbox.csv");
    private static string OldPath => System.IO.Path.Combine(AppInfo.DataDir, "gpu-blackbox.alt.csv");

    /// <summary>Haengt einen Messpunkt an.</summary>
    public static void Append(DateTime time, GpuSample g)
    {
        var throttle = new List<string>();
        if (g.ThrottlePowerCap == true) throttle.Add("Leistung");
        if (g.ThrottleThermal == true) throttle.Add("Temperatur");
        if (g.ThrottleHwSlowdown == true) throttle.Add("Hardware-Schutz");

        var line = string.Join(';',
            Stamp(time),
            N(g.UtilizationPercent), N(g.TemperatureC), N(g.PowerWatt), N(g.PowerLimitWatt),
            N(g.ClockMhz), N(g.MemoryUsedMb),
            $"{I(g.PcieLinkGenCurrent)}/{I(g.PcieLinkGenMax)}",
            $"{I(g.PcieLinkWidthCurrent)}/{I(g.PcieLinkWidthMax)}",
            throttle.Count == 0 ? "-" : string.Join("+", throttle),
            N(g.FanPercent), g.PerformanceState ?? "",
            "");
        Write(line);
    }

    /// <summary>Haelt fest, dass die Karte nicht mehr auf Abfragen antwortet.</summary>
    public static void AppendNote(DateTime time, string marker, string? detail = null)
    {
        var text = marker + (string.IsNullOrWhiteSpace(detail) ? "" : ": " + Flatten(detail));
        Write(string.Join(';', Stamp(time), "", "", "", "", "", "", "", "", "", "", "", text));
    }

    /// <summary>Alle Zeilen eines Zeitfensters, in Spalten zerlegt (aelteste zuerst).</summary>
    public static List<string[]> Read(DateTime from, DateTime to)
    {
        var rows = new List<string[]>();
        foreach (var path in new[] { OldPath, Path })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length < 19 || !TryStamp(line, out var t)) continue;
                    if (t < from || t > to) continue;

                    var cols = line.Split(';');
                    if (cols.Length < 13) Array.Resize(ref cols, 13);
                    rows.Add(cols.Select(c => c ?? "").ToArray());
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"GPU-Blackbox {path} nicht lesbar: {ex.Message}");
            }
        }
        return rows;
    }

    /// <summary>Anzahl der Ausfaelle der GPU-Abfrage seit einem Zeitpunkt.</summary>
    public static int CountLostEvents(DateTime since)
        => Read(since, DateTime.Now.AddMinutes(1)).Count(r => r[12].StartsWith(LostMarker, StringComparison.Ordinal));

    /// <summary>
    /// Die letzten Werte bis zu einem Zeitpunkt als Klartext fuer Vorfallberichte.
    /// Null, wenn die Blackbox dafuer nichts enthaelt.
    /// </summary>
    public static string? Describe(DateTime until, int lines = 6)
    {
        var rows = Read(until.AddMinutes(-15), until.AddMinutes(1));
        if (rows.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Letzte GPU-Werte vor dem Ausfall (aus gpu-blackbox.csv):");
        sb.AppendLine("  Zeit      Last  Temp  Leistung  Takt   PCIe        Drosselung");
        foreach (var r in rows.TakeLast(lines))
        {
            if (r[12].Length > 0)
            {
                sb.AppendLine($"  {r[0][11..]}  {r[12]}");
                continue;
            }

            sb.AppendLine(
                $"  {r[0][11..],-8}  {r[1],3} %  {r[2],3}°  {r[3],5} W  {r[5],4}  " +
                $"Gen {r[7],-4} x{r[8],-6} {r[9]}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---------- intern ----------

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Rotate();
                bool isNew = !File.Exists(Path);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var text = (isNew ? Header + Environment.NewLine : "") + line + Environment.NewLine;
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                // Direkt auf die Platte: Nach einem harten Ausfall zaehlt jede Zeile.
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU-Blackbox nicht schreibbar: " + ex.Message);
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(Path, OldPath, overwrite: true);
        }
        catch { /* Rotation ist Beiwerk */ }
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    private static bool TryStamp(string line, out DateTime t)
        => DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out t);

    private static string N(double? v) => v is null ? "" : v.Value.ToString("0.#", Inv);
    private static string I(int? v) => v is null ? "?" : v.Value.ToString(Inv);

    private static string Flatten(string s)
    {
        var flat = s.Replace("\r", " ").Replace("\n", " ").Replace(';', ',').Trim();
        return flat.Length > 300 ? flat[..300] + " ..." : flat;
    }
}
