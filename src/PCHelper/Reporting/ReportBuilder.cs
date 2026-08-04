using System.IO;
using System.Net;
using System.Text;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Monitoring;

namespace PCHelper.Reporting;

/// <summary>Erzeugt Berichte als HTML-Datei und als Markdown fuer die Zwischenablage.</summary>
public static class ReportBuilder
{
    /// <summary>Schreibt den vollstaendigen Bericht als HTML-Datei und liefert den Pfad.</summary>
    public static string WriteHtml(
        DiagnosisResult result,
        IReadOnlyList<Incident> incidents,
        TelemetryLogger telemetry)
    {
        var path = Path.Combine(AppInfo.ReportDir,
            $"PC-Helper-Bericht_{DateTime.Now:yyyy-MM-dd_HH-mm}.html");

        File.WriteAllText(path, BuildHtml(result, incidents, telemetry), new UTF8Encoding(true));
        Log.Info("Bericht geschrieben: " + path);
        return path;
    }

    private static string BuildHtml(
        DiagnosisResult result,
        IReadOnlyList<Incident> incidents,
        TelemetryLogger telemetry)
    {
        var p = result.Profile;
        var sb = new StringBuilder();

        sb.AppendLine("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>PC-Helper-Bericht {DateTime.Now:dd.MM.yyyy}</title>");
        sb.AppendLine(Css);
        sb.AppendLine("</head><body><div class=\"wrap\">");

        sb.AppendLine("<header>");
        sb.AppendLine("<h1>PC-Helper-Diagnosebericht</h1>");
        sb.AppendLine($"<p class=\"sub\">Erstellt am {DateTime.Now:dddd, d. MMMM yyyy 'um' HH:mm} &middot; {AppInfo.Name} {AppInfo.VersionDisplay} &middot; Dauer {result.Duration.TotalSeconds:0.#} s</p>");
        sb.AppendLine("</header>");

        // --- Gemeldetes Symptom ---
        if (result.Symptom is { } symptom)
        {
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<h2>Gemeldetes Problem</h2><p><strong>{E(symptom.Title)}</strong></p>");
            sb.AppendLine($"<p class=\"muted\">{E(symptom.Description)}</p>");
            sb.AppendLine($"<p class=\"muted small\">Gezielte Untersuchung: {result.ChecksRun} Pruefungen. " +
                          "Die Befunde stehen nach ihrer Bedeutung fuer dieses Problem.</p>");
            sb.AppendLine("</div>");
        }

        // --- Kennzahlen ---
        sb.AppendLine("<div class=\"tiles\">");
        sb.AppendLine(Tile("Kritisch", result.CriticalCount.ToString(), "crit"));
        sb.AppendLine(Tile("Auffaellig", result.WarningCount.ToString(), "warn"));
        sb.AppendLine(Tile("In Ordnung", result.OkCount.ToString(), "ok"));
        sb.AppendLine(Tile("Vorfaelle", incidents.Count.ToString(), "info"));
        sb.AppendLine("</div>");

        // --- Verdachtsliste ---
        if (result.Suspicions.Count > 0)
        {
            sb.AppendLine("<h2>Wo das Problem am wahrscheinlichsten liegt</h2>");
            sb.AppendLine("<div class=\"card\">");
            foreach (var s in result.Suspicions.Take(6))
            {
                sb.AppendLine("<div class=\"susp\">");
                sb.AppendLine($"<div class=\"susp-head\"><span class=\"susp-title\">{E(s.Title)}</span><span class=\"chip {ChipClass(s.Percent)}\">{E(s.Rank)}</span></div>");
                sb.AppendLine($"<div class=\"bar\"><div class=\"bar-fill\" style=\"width:{s.Percent:0}%\"></div></div>");
                sb.AppendLine($"<p class=\"muted\">{E(s.Hint)}</p>");
                sb.AppendLine($"<p class=\"muted small\">Beitragende Befunde: {E(string.Join(", ", s.Evidence.Take(4).Select(f => f.Title)))}</p>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        // --- System ---
        sb.AppendLine("<h2>System</h2>");
        sb.AppendLine("<div class=\"card\"><table>");
        Row(sb, "Prozessor", $"{p.CpuName} ({p.CpuCores} Kerne / {p.CpuThreads} Threads)");
        Row(sb, "Mainboard", $"{p.BoardManufacturer} {p.BoardProduct}");
        Row(sb, "BIOS", $"{p.BiosVersion} &middot; {p.FormatBiosAge()}");
        Row(sb, "Arbeitsspeicher", $"{p.TotalMemoryGb:0.#} GB" +
            (p.MemoryModules.Count > 0 ? $" &middot; {p.MemoryModules.Max(m => m.ConfiguredSpeedMhz)} MT/s" +
                (p.MemoryOverclocked ? " <strong>(EXPO/XMP aktiv)</strong>" : " (Standardtakt)") : ""));
        foreach (var g in p.Gpus) Row(sb, "Grafik", $"{g.Name} &middot; Treiber {g.DriverDisplay}");
        foreach (var d in p.Displays) Row(sb, "Bildschirm", $"{d.Name} &middot; {d.Connection} &middot; {d.RefreshHz:0.##} Hz");
        foreach (var d in p.Disks) Row(sb, "Datentraeger", $"{d.Model} &middot; {d.SizeGb:0} GB &middot; {d.HealthStatus}");
        Row(sb, "Windows", $"{p.OsCaption} {p.OsDisplayVersion} (Build {p.OsBuild})");
        Row(sb, "Energieplan", p.PowerPlanName);
        Row(sb, "Schnellstart", p.FastStartupEnabled ? "aktiv" : "deaktiviert");
        Row(sb, "Laufzeit", $"{p.Uptime.Days} Tage, {p.Uptime.Hours} Std, {p.Uptime.Minutes} Min");
        sb.AppendLine("</table></div>");

        // --- Neustart-Verlauf ---
        var sessions = BootHistory.Read(30, 40);
        if (sessions.Count > 0)
        {
            var (totalSessions, unexpected, median) = BootHistory.Summarize(sessions);

            sb.AppendLine("<h2>Neustart-Protokoll</h2>");
            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<p>{totalSessions} Startvorgaenge in den letzten 30 Tagen" +
                          (unexpected > 0 ? $", davon <strong>{unexpected} ohne ordentliches Herunterfahren</strong>" : "") +
                          (median is null ? "." : $". Typische Sitzungsdauer: {E(MonitorService.FormatUptime(median.Value))}.") +
                          "</p>");
            sb.AppendLine("<table class=\"data\"><tr><th>Windows gestartet</th><th>Beendet</th><th>Dauer</th><th>Ende</th></tr>");

            foreach (var s in sessions)
            {
                var cls = s.IsUnexpected ? " style=\"color:var(--crit)\"" : "";
                sb.AppendLine($"<tr><td>{E(s.StartText)}</td><td>{E(s.EndText)}</td>" +
                              $"<td>{E(s.DurationText)}</td><td{cls}>{E(s.EndKindText)}</td></tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<p class=\"muted small\">Aus dem Windows-Ereignisprotokoll rekonstruiert. " +
                          "Wenn ein Schwarzbild nur durch einen Neustart zu beheben ist, markiert jeder " +
                          "dieser Zeitpunkte einen moeglichen Vorfall.</p>");
            sb.AppendLine("</div>");
        }

        // --- Befunde ---
        sb.AppendLine("<h2>Befunde</h2>");
        foreach (var group in result.Findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            sb.AppendLine($"<h3>{E(SeverityHeading(group.Key))}</h3>");
            foreach (var f in group)
            {
                sb.AppendLine($"<div class=\"card finding {SevClass(f.Severity)}\">");
                sb.AppendLine($"<div class=\"finding-head\"><span class=\"chip {SevClass(f.Severity)}\">{E(f.SeverityText)}</span>");
                sb.AppendLine($"<span class=\"finding-title\">{E(f.Title)}</span><span class=\"cat\">{E(f.Category)}</span></div>");
                sb.AppendLine($"<p>{E(f.Summary)}</p>");

                if (!string.IsNullOrWhiteSpace(f.Detail))
                    sb.AppendLine($"<details><summary>Details</summary><pre>{E(f.Detail)}</pre></details>");

                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                    sb.AppendLine($"<div class=\"reco\"><strong>Empfehlung</strong><pre>{E(f.Recommendation)}</pre></div>");

                if (f.Links.Count > 0)
                {
                    sb.AppendLine("<p class=\"links\">");
                    foreach (var link in f.Links)
                        sb.AppendLine($"<a href=\"{E(link.Url)}\" rel=\"noreferrer\">{E(link.Label)}</a>");
                    sb.AppendLine("</p>");
                }
                sb.AppendLine("</div>");
            }
        }

        // --- Vorfaelle mit Messwerten ---
        if (incidents.Count > 0)
        {
            sb.AppendLine("<h2>Aufgezeichnete Vorfaelle</h2>");
            foreach (var incident in incidents.Take(10))
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<div class=\"finding-head\"><span class=\"chip info\">{E(incident.KindText)}</span>");
                sb.AppendLine($"<span class=\"finding-title\">{E(incident.Title)}</span><span class=\"cat\">{E(incident.TimeText)}</span></div>");

                if (!string.IsNullOrWhiteSpace(incident.Note))
                    sb.AppendLine($"<pre>{E(incident.Note)}</pre>");

                if (incident.DisplaySignatureBefore is not null)
                {
                    sb.AppendLine("<pre>Anzeige vorher:  " + E(incident.DisplaySignatureBefore) +
                                  "\nAnzeige nachher: " + E(incident.DisplaySignatureAfter ?? "-") + "</pre>");
                }

                var samples = telemetry.Read(incident.Time.AddMinutes(-10), incident.Time.AddMinutes(2));
                if (samples.Count > 0)
                {
                    sb.AppendLine("<details open><summary>Messwerte der letzten 10 Minuten davor</summary>");
                    sb.AppendLine("<table class=\"data\"><tr><th>Zeit</th><th>CPU %</th><th>RAM %</th><th>GPU °C</th><th>GPU %</th><th>GPU W</th><th>Bildschirme</th><th>Ereignis</th></tr>");
                    foreach (var s in samples.TakeLast(80))
                    {
                        sb.AppendLine($"<tr><td>{s.Time:HH:mm:ss}</td><td>{Num(s.CpuPercent)}</td><td>{Num(s.RamPercent)}</td>" +
                                      $"<td>{Num(s.GpuTempC)}</td><td>{Num(s.GpuUtilPercent)}</td><td>{Num(s.GpuPowerWatt)}</td>" +
                                      $"<td>{s.DisplayCount}</td><td>{E(s.Event ?? "")}</td></tr>");
                    }
                    sb.AppendLine("</table></details>");
                }

                var around = EventLogService.Around("System", incident.Time, TimeSpan.FromMinutes(5), 40);
                if (around.Count > 0)
                {
                    sb.AppendLine("<details><summary>Ereignisprotokoll rund um den Vorfall</summary><pre>");
                    foreach (var e in around) sb.AppendLine(E(e.ToString()));
                    sb.AppendLine("</pre></details>");
                }

                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("<footer><p class=\"muted small\">Erzeugt von " + E(AppInfo.Name) + " " + E(AppInfo.VersionDisplay) +
                      ". Dieser Bericht enthaelt Hardware- und Ereignisprotokolldaten dieses Rechners. " +
                      "Vor dem Weitergeben pruefen, ob darin Geraete- oder Benutzernamen stehen, die nicht oeffentlich werden sollen.</p></footer>");
        sb.AppendLine("</div></body></html>");

        return sb.ToString();
    }

    /// <summary>Kompakte Zusammenfassung fuer Forenbeitraege oder Nachrichten.</summary>
    public static string BuildMarkdown(DiagnosisResult result, IReadOnlyList<Incident> incidents)
    {
        var p = result.Profile;
        var sb = new StringBuilder();

        sb.AppendLine("## PC-Helper-Diagnose");
        sb.AppendLine();
        sb.AppendLine($"*Erstellt am {DateTime.Now:dd.MM.yyyy HH:mm} mit {AppInfo.Name} {AppInfo.VersionDisplay}*");
        sb.AppendLine();

        if (result.Symptom is { } symptom)
        {
            sb.AppendLine($"**Gemeldetes Problem:** {symptom.Title}");
            sb.AppendLine();
        }

        sb.AppendLine("### System");
        sb.AppendLine($"- **CPU:** {p.CpuName}");
        sb.AppendLine($"- **Mainboard:** {p.BoardManufacturer} {p.BoardProduct}, BIOS {p.BiosVersion} ({p.FormatBiosAge()})");
        sb.AppendLine($"- **RAM:** {p.TotalMemoryGb:0.#} GB" +
                      (p.MemoryModules.Count > 0
                          ? $", {p.MemoryModules.Max(m => m.ConfiguredSpeedMhz)} MT/s, EXPO/XMP: {(p.MemoryOverclocked ? "aktiv" : "aus")}"
                          : ""));
        foreach (var g in p.Gpus) sb.AppendLine($"- **GPU:** {g.Name}, Treiber {g.DriverDisplay}");
        foreach (var d in p.Displays) sb.AppendLine($"- **Monitor:** {d.Name}, {d.Connection}, {d.RefreshHz:0.##} Hz");
        sb.AppendLine($"- **Windows:** {p.OsCaption} {p.OsDisplayVersion} (Build {p.OsBuild})");
        sb.AppendLine();

        if (result.Suspicions.Count > 0)
        {
            sb.AppendLine("### Verdachtsreihenfolge");
            foreach (var s in result.Suspicions.Take(5))
                sb.AppendLine($"1. **{s.Title}** - {s.Rank} ({s.Percent:0} %)");
            sb.AppendLine();
        }

        var relevant = result.Findings.Where(f => f.Severity >= Severity.Warning).ToList();
        if (relevant.Count > 0)
        {
            sb.AppendLine("### Auffaellige Befunde");
            foreach (var f in relevant)
            {
                sb.AppendLine($"- **[{f.SeverityText}] {f.Title}** ({f.Category})");
                sb.AppendLine($"  {f.Summary}");
            }
            sb.AppendLine();
        }

        if (incidents.Count > 0)
        {
            sb.AppendLine("### Aufgezeichnete Vorfaelle");
            foreach (var i in incidents.Take(10))
                sb.AppendLine($"- {i.TimeText} - {i.Title} ({i.KindText})");
            sb.AppendLine();
        }

        var sessions = BootHistory.Read(30, 40);
        if (sessions.Count > 0)
        {
            var (total, unexpected, median) = BootHistory.Summarize(sessions);
            sb.AppendLine("### Neustart-Verhalten");
            sb.AppendLine($"- {total} Startvorgaenge in 30 Tagen, davon {unexpected} ohne ordentliches Herunterfahren");
            if (median is not null)
                sb.AppendLine($"- Typische Sitzungsdauer: {MonitorService.FormatUptime(median.Value)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ---------- Hilfsfunktionen ----------

    private static void Row(StringBuilder sb, string label, string value)
        => sb.AppendLine($"<tr><th>{E(label)}</th><td>{value}</td></tr>");

    private static string Tile(string label, string value, string cls)
        => $"<div class=\"tile {cls}\"><div class=\"tile-value\">{E(value)}</div><div class=\"tile-label\">{E(label)}</div></div>";

    private static string Num(double? v) => v is null ? "-" : v.Value.ToString("0.#");

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string SevClass(Severity s) => s switch
    {
        Severity.Critical => "crit",
        Severity.Warning => "warn",
        Severity.Ok => "ok",
        _ => "info",
    };

    private static string ChipClass(double percent) => percent switch
    {
        >= 70 => "crit",
        >= 40 => "warn",
        _ => "info",
    };

    private static string SeverityHeading(Severity s) => s switch
    {
        Severity.Critical => "Kritische Befunde",
        Severity.Warning => "Auffaellige Befunde",
        Severity.Ok => "Geprueft und in Ordnung",
        _ => "Informationen",
    };

    private const string Css = """
        <style>
          :root {
            --bg:#0f1115; --card:#171a21; --card2:#1e222b; --line:#2a2f3a;
            --text:#e6e9ef; --dim:#98a0b0; --accent:#4c8dff;
            --ok:#35c48a; --warn:#f3b13c; --crit:#f2555a; --info:#6fa8ff;
          }
          @media (prefers-color-scheme: light) {
            :root { --bg:#f6f7f9; --card:#ffffff; --card2:#f0f2f5; --line:#dfe3e9;
                    --text:#1a1d23; --dim:#5c6572; }
          }
          * { box-sizing: border-box; }
          body { margin:0; background:var(--bg); color:var(--text);
                 font: 15px/1.6 "Segoe UI", system-ui, -apple-system, sans-serif; }
          .wrap { max-width: 1000px; margin: 0 auto; padding: 32px 20px 64px; }
          header { border-bottom:1px solid var(--line); padding-bottom:16px; margin-bottom:24px; }
          h1 { font-size:26px; margin:0 0 4px; letter-spacing:-.02em; }
          h2 { font-size:19px; margin:34px 0 12px; letter-spacing:-.01em; }
          h3 { font-size:15px; margin:24px 0 8px; color:var(--dim); text-transform:uppercase;
               letter-spacing:.08em; font-weight:600; }
          .sub, .muted { color:var(--dim); }
          .small { font-size:13px; }
          .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; }
          .tile { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px; }
          .tile-value { font-size:28px; font-weight:600; line-height:1; }
          .tile-label { color:var(--dim); font-size:13px; margin-top:6px; }
          .tile.crit .tile-value { color:var(--crit); }
          .tile.warn .tile-value { color:var(--warn); }
          .tile.ok .tile-value { color:var(--ok); }
          .tile.info .tile-value { color:var(--info); }
          .card { background:var(--card); border:1px solid var(--line); border-radius:12px;
                  padding:18px; margin-bottom:12px; }
          .finding.crit { border-left:3px solid var(--crit); }
          .finding.warn { border-left:3px solid var(--warn); }
          .finding.ok   { border-left:3px solid var(--ok); }
          .finding.info { border-left:3px solid var(--info); }
          .finding-head { display:flex; align-items:center; gap:10px; flex-wrap:wrap; margin-bottom:8px; }
          .finding-title { font-weight:600; font-size:16px; }
          .cat { color:var(--dim); font-size:13px; margin-left:auto; }
          .chip { font-size:11px; font-weight:700; text-transform:uppercase; letter-spacing:.06em;
                  padding:3px 9px; border-radius:99px; }
          .chip.crit { background:rgba(242,85,90,.16); color:var(--crit); }
          .chip.warn { background:rgba(243,177,60,.16); color:var(--warn); }
          .chip.ok   { background:rgba(53,196,138,.16); color:var(--ok); }
          .chip.info { background:rgba(111,168,255,.16); color:var(--info); }
          p { margin:6px 0; }
          pre { background:var(--card2); border:1px solid var(--line); border-radius:8px; padding:12px;
                overflow-x:auto; white-space:pre-wrap; word-break:break-word;
                font: 13px/1.55 "Cascadia Mono", Consolas, monospace; margin:8px 0; }
          details { margin-top:8px; } summary { cursor:pointer; color:var(--accent); font-size:14px; }
          .reco { margin-top:10px; }
          .links a { color:var(--accent); margin-right:14px; }
          table { width:100%; border-collapse:collapse; }
          table th { text-align:left; color:var(--dim); font-weight:500; padding:6px 12px 6px 0;
                     vertical-align:top; white-space:nowrap; width:1%; }
          table td { padding:6px 0; }
          table.data { font-size:13px; }
          table.data th, table.data td { padding:4px 10px 4px 0; white-space:nowrap; width:auto; }
          table.data tr:nth-child(even) { background:var(--card2); }
          .susp { padding:12px 0; border-bottom:1px solid var(--line); }
          .susp:last-child { border-bottom:none; }
          .susp-head { display:flex; align-items:center; gap:10px; }
          .susp-title { font-weight:600; }
          .bar { height:6px; background:var(--card2); border-radius:99px; overflow:hidden; margin:8px 0; }
          .bar-fill { height:100%; background:var(--accent); border-radius:99px; }
          footer { margin-top:40px; padding-top:16px; border-top:1px solid var(--line); }
        </style>
        """;
}
