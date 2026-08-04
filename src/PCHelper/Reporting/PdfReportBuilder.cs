using System.IO;
using PCHelper.Core;
using PCHelper.Diagnostics;
using PCHelper.Monitoring;

namespace PCHelper.Reporting;

/// <summary>
/// Erzeugt den Diagnosebericht als PDF.
///
/// Bewusst so aufgebaut, dass er sich unveraendert an einen Haendler- oder
/// Herstellersupport weiterreichen laesst: Zusammenfassung zuerst, dann
/// Hardware, dann Belege.
/// </summary>
public static class PdfReportBuilder
{
    private static readonly PdfColor Critical = PdfColor.FromHex("#C62828");
    private static readonly PdfColor Warning = PdfColor.FromHex("#B26A00");
    private static readonly PdfColor Ok = PdfColor.FromHex("#1E7A4E");
    private static readonly PdfColor Info = PdfColor.FromHex("#2C5FB3");
    private static readonly PdfColor Accent = PdfColor.FromHex("#2C5FB3");

    /// <summary>Schreibt den Bericht als PDF und liefert den Pfad zurueck.</summary>
    public static string Write(
        DiagnosisResult result,
        IReadOnlyList<Incident> incidents,
        TelemetryLogger telemetry)
    {
        var path = Path.Combine(AppInfo.ReportDir,
            $"PC-Helper-Bericht_{DateTime.Now:yyyy-MM-dd_HH-mm}.pdf");

        var p = result.Profile;
        var pdf = new PdfWriter(
            "PC-Helper-Diagnosebericht",
            $"Erstellt am {DateTime.Now:dddd, d. MMMM yyyy 'um' HH:mm} mit {AppInfo.Name} {AppInfo.VersionDisplay}");

        // ---------------- Zusammenfassung ----------------
        pdf.Heading("Zusammenfassung", 1);

        if (result.Symptom is { } symptom)
        {
            pdf.KeyValue("Gemeldetes Problem", symptom.Title);
            pdf.Paragraph(symptom.Description, muted: true, size: 8.4);
        }

        pdf.Paragraph(
            $"Es wurden {result.Findings.Count} Pruefungen ausgewertet: " +
            $"{result.CriticalCount} kritische Befunde, {result.WarningCount} auffaellige Befunde, " +
            $"{result.OkCount} ohne Beanstandung. Betrachtungszeitraum der Ereignisprotokolle: 30 Tage.");

        if (result.Suspicions.Count > 0)
        {
            pdf.Heading("Wahrscheinlichste Ursachen", 2);
            var rows = result.Suspicions.Take(6)
                .Select(s => new[] { s.Rank, s.Title, $"{s.Percent:0} %" })
                .ToList();
            pdf.Table(new[] { "Einstufung", "Bereich", "Gewicht" }, rows, new double[] { 1.1, 3.4, 0.8 });

            pdf.Paragraph(
                "Die Gewichtung ergibt sich aus den erhobenen Befunden und ihrer Einstufung. " +
                "Sie ersetzt keine Pruefung durch den Hersteller, gibt aber die Reihenfolge vor, " +
                "in der eine Eingrenzung sinnvoll ist.", muted: true, size: 8.4);
        }

        // ---------------- System ----------------
        pdf.Heading("System", 1);
        pdf.KeyValue("Prozessor", $"{p.CpuName} ({p.CpuCores} Kerne / {p.CpuThreads} Threads)");
        pdf.KeyValue("Mainboard", $"{p.BoardManufacturer} {p.BoardProduct}");
        pdf.KeyValue("BIOS", $"{p.BiosVersion}, vom {p.FormatBiosAge()}");

        var memory = $"{p.TotalMemoryGb:0.#} GB in {p.MemoryModules.Count} Modul(en)";
        if (p.MemoryModules.Count > 0)
            memory += $", betrieben mit {p.MemoryModules.Max(m => m.ConfiguredSpeedMhz)} MT/s " +
                      $"({(p.MemoryOverclocked ? "EXPO/XMP aktiv" : "Standardtakt")})";
        pdf.KeyValue("Arbeitsspeicher", memory);

        foreach (var m in p.MemoryModules)
            pdf.KeyValue("  " + m.Slot, $"{m.CapacityGb:0.#} GB {m.TypeName} {m.Manufacturer} {m.PartNumber}");

        foreach (var g in p.Gpus)
            pdf.KeyValue("Grafikkarte", $"{g.Name}, Treiber {g.DriverDisplay}" +
                                        (g.DriverDate is null ? "" : $" vom {g.DriverDate:d}"));

        foreach (var d in p.Displays)
            pdf.KeyValue("Bildschirm", $"{d.Name}, {d.Connection}, {d.RefreshHz:0.##} Hz");

        foreach (var d in p.Disks)
            pdf.KeyValue("Datentraeger", $"{d.Model}, {d.SizeGb:0} GB, {d.BusType} {d.MediaType}, Status: {d.HealthStatus}");

        pdf.KeyValue("Betriebssystem", $"{p.OsCaption} {p.OsDisplayVersion} (Build {p.OsBuild})");
        pdf.KeyValue("Energieplan", p.PowerPlanName);
        pdf.KeyValue("Schnellstart", p.FastStartupEnabled ? "aktiv" : "deaktiviert");
        pdf.KeyValue("Laufzeit", $"{p.Uptime.Days} Tage, {p.Uptime.Hours} Std, {p.Uptime.Minutes} Min");

        // ---------------- Neustart-Verlauf ----------------
        var sessions = BootHistory.Read(30, 40);
        if (sessions.Count > 0)
        {
            var (total, unexpected, median) = BootHistory.Summarize(sessions);

            pdf.Heading("Neustart-Protokoll", 1);
            pdf.Paragraph(
                $"{total} Startvorgaenge in den letzten 30 Tagen" +
                (unexpected > 0 ? $", davon {unexpected} ohne ordentliches Herunterfahren" : "") +
                (median is null ? "." : $". Typische Sitzungsdauer: {MonitorService.FormatUptime(median.Value)}."));

            var rows = sessions.Take(30)
                .Select(s => new[] { s.StartText, s.EndText, s.DurationText, s.EndKindText })
                .ToList();
            pdf.Table(new[] { "Windows gestartet", "Beendet", "Dauer", "Ende" }, rows,
                      new double[] { 1.3, 1.3, 1, 1.5 });
        }

        // ---------------- Befunde ----------------
        pdf.Heading("Befunde", 1);

        foreach (var group in result.Findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            pdf.Heading(HeadingFor(group.Key), 2);

            foreach (var f in group)
            {
                pdf.FindingHeader(f.SeverityText, ColorFor(f.Severity), f.Title, f.Category);
                pdf.Paragraph(f.Summary, indent: 10);

                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    pdf.Paragraph("Empfehlung", size: 8.4, indent: 10);
                    pdf.Paragraph(f.Recommendation, muted: true, size: 8.6, indent: 10);
                }

                // Details nur bei relevanten Befunden - sonst wird der Bericht unlesbar.
                if (f.Severity >= Severity.Warning && !string.IsNullOrWhiteSpace(f.Detail))
                    pdf.Mono(Shorten(f.Detail!, 4000));

                pdf.Space(4);
            }
        }

        // ---------------- Vorfaelle ----------------
        if (incidents.Count > 0)
        {
            pdf.Heading("Aufgezeichnete Vorfaelle", 1);
            pdf.Paragraph(
                "Automatisch erkannte oder vom Nutzer gemeldete Ereignisse, jeweils mit den " +
                "Messwerten unmittelbar davor.", muted: true, size: 8.6);

            foreach (var incident in incidents.Take(8))
            {
                pdf.FindingHeader(incident.KindText, Accent, incident.Title, incident.TimeText);

                if (!string.IsNullOrWhiteSpace(incident.Note))
                    pdf.Paragraph(incident.Note!, muted: true, size: 8.6, indent: 10);

                var samples = telemetry.Read(incident.Time.AddMinutes(-10), incident.Time.AddMinutes(2));
                if (samples.Count > 0)
                {
                    var rows = samples.TakeLast(20)
                        .Select(s => new[]
                        {
                            s.Time.ToString("HH:mm:ss"),
                            Num(s.CpuPercent), Num(s.RamPercent),
                            Num(s.GpuTempC), Num(s.GpuUtilPercent), Num(s.GpuPowerWatt),
                            s.DisplayCount.ToString(),
                        })
                        .ToList();

                    pdf.Table(new[] { "Zeit", "CPU %", "RAM %", "GPU C", "GPU %", "GPU W", "Monitore" },
                              rows, new double[] { 1.1, 1, 1, 1, 1, 1, 1.1 });
                }

                pdf.Space(4);
            }
        }

        pdf.Heading("Hinweis", 2);
        pdf.Paragraph(
            "Dieser Bericht wurde automatisch aus Systeminformationen und dem Windows-Ereignisprotokoll " +
            "dieses Rechners erzeugt. Er enthaelt keine persoenlichen Dateien und keine Zugangsdaten, " +
            "wohl aber Geraete- und Modellbezeichnungen.", muted: true, size: 8.4);

        pdf.Save(path);
        Log.Info("PDF-Bericht geschrieben: " + path);
        return path;
    }

    private static string HeadingFor(Severity s) => s switch
    {
        Severity.Critical => "Kritische Befunde",
        Severity.Warning => "Auffaellige Befunde",
        Severity.Ok => "Geprueft und in Ordnung",
        _ => "Informationen",
    };

    private static PdfColor ColorFor(Severity s) => s switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        Severity.Ok => Ok,
        _ => Info,
    };

    private static string Num(double? v) => v is null ? "-" : v.Value.ToString("0.#");

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n... (gekuerzt, vollstaendig im HTML-Bericht)";
}
