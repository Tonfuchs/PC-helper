using System.IO;
using System.Text;
using PCHelper.Core;
using PCHelper.Diagnostics;

namespace PCHelper.Monitoring;

/// <summary>
/// Menschenlesbares Sitzungsprotokoll: Bei jedem Start und bei jedem
/// Herunterfahren/Neustart wird ein Eintrag angehaengt.
///
/// Hintergrund: Wenn ein Schwarzbild nur durch einen Neustart wegzubekommen
/// ist, dann ist jeder Neustart selbst ein Hinweis auf einen Vorfall. Diese
/// Datei macht das ohne Umwege sichtbar - und laesst sich einfach weitergeben.
/// </summary>
public static class SessionLog
{
    private static readonly object _gate = new();

    /// <summary>Pfad der Protokolldatei.</summary>
    public static string Path => System.IO.Path.Combine(AppInfo.DataDir, "sitzungsprotokoll.txt");

    /// <summary>Eintrag beim Programmstart (also praktisch beim Hochfahren).</summary>
    public static void AppendStart(string systemSummary, bool previousSessionWasClean, DateTime? lastHeartbeat)
    {
        var bootTime = DateTime.Now - Native.GetUptime();
        var sb = new StringBuilder();

        sb.AppendLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}]  START");
        sb.AppendLine($"    Windows gestartet um: {bootTime:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"    Vorherige Sitzung:    {(previousSessionWasClean ? "sauber beendet" : "NICHT sauber beendet")}");

        if (lastHeartbeat is not null)
            sb.AppendLine($"    Letztes Lebenszeichen der Ueberwachung: {lastHeartbeat:dd.MM.yyyy HH:mm:ss}");

        sb.AppendLine($"    System: {systemSummary}");
        Append(sb.ToString());
    }

    /// <summary>
    /// Eintrag beim Herunterfahren, Neustart oder Abmelden.
    /// Muss schnell sein: Windows raeumt der Anwendung dafuer nur wenig Zeit ein.
    /// </summary>
    public static void AppendShutdown(string reason, TelemetrySample? lastSample, string? displaySignature)
    {
        var uptime = Native.GetUptime();
        var sb = new StringBuilder();

        sb.AppendLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}]  {reason.ToUpperInvariant()}");
        sb.AppendLine($"    Laufzeit dieser Sitzung: {(int)uptime.TotalHours} Std {uptime.Minutes} Min");

        if (displaySignature is not null)
            sb.AppendLine($"    Anzeige beim Beenden:    {displaySignature}");

        if (lastSample is not null)
        {
            sb.AppendLine($"    Letzte Messwerte ({lastSample.Time:HH:mm:ss}): " +
                          $"CPU {Fmt(lastSample.CpuPercent, "%")}, RAM {Fmt(lastSample.RamPercent, "%")}, " +
                          $"GPU {Fmt(lastSample.GpuTempC, "°C")} / {Fmt(lastSample.GpuUtilPercent, "%")} / " +
                          $"{Fmt(lastSample.GpuPowerWatt, "W")}, Bildschirme: {lastSample.DisplayCount}");
        }

        Append(sb.ToString());
    }

    /// <summary>Freier Eintrag, z. B. wenn der Nutzer einen Neustart als Schwarzbild bestaetigt.</summary>
    public static void AppendNote(string title, string? detail = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}]  {title.ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(detail))
            foreach (var line in detail.Split('\n'))
                sb.AppendLine("    " + line.TrimEnd());
        Append(sb.ToString());
    }

    /// <summary>Liest die letzten Zeilen des Protokolls (neueste zuletzt).</summary>
    public static string ReadTail(int maxLines = 400)
    {
        try
        {
            if (!File.Exists(Path)) return "Es wurden noch keine Sitzungen protokolliert.";
            var lines = File.ReadAllLines(Path);
            return lines.Length <= maxLines
                ? string.Join(Environment.NewLine, lines)
                : string.Join(Environment.NewLine, lines[^maxLines..]);
        }
        catch (Exception ex)
        {
            return "Das Sitzungsprotokoll konnte nicht gelesen werden: " + ex.Message;
        }
    }

    private static void Append(string block)
    {
        try
        {
            lock (_gate)
            {
                // Direkt auf die Platte schreiben - beim Herunterfahren bleibt
                // keine Zeit, auf einen Puffer zu vertrauen.
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = new UTF8Encoding(false).GetBytes(block + Environment.NewLine);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Sitzungsprotokoll nicht schreibbar: " + ex.Message);
        }
    }

    private static string Fmt(double? v, string unit) => v is null ? "-" : $"{v.Value:0.#} {unit}";
}
