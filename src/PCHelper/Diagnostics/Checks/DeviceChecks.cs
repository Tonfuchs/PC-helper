using System.Text;

namespace PCHelper.Diagnostics.Checks;

/// <summary>Geraete, die im Geraete-Manager einen Fehlercode tragen.</summary>
public sealed class ProblemDeviceCheck : ICheck
{
    public string Name => "Geraete mit Fehlercode";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } =
        new[] { Cause.DeviceDriver, Cause.UsbDevice, Cause.AudioDevice, Cause.Microphone, Cause.Network };

    /// <summary>
    /// Code 45 heisst nur "gerade nicht angesteckt". Das steht bei jedem Rechner
    /// dutzendfach im Bestand und waere als Fehler schlicht Rauschen.
    /// </summary>
    private const int NotConnected = 45;

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var all = ctx.Profile.ProblemDevices;
        var real = all.Where(d => d.ErrorCode != NotConnected).ToList();

        if (real.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "device-errors", Category = Category, Title = "Kein Geraet meldet einen Fehler",
                    Severity = Severity.Ok,
                    Summary = "Im Geraete-Manager traegt kein aktives Geraet ein Ausrufezeichen.",
                    Detail = all.Count == 0
                        ? null
                        : $"{all.Count} Eintrag/Eintraege betreffen nur gerade nicht angeschlossene Geraete (Code 45) " +
                          "und sind voellig normal.",
                }
            });
        }

        var sb = new StringBuilder();
        foreach (var d in real.OrderBy(d => d.DeviceClass, StringComparer.CurrentCulture))
        {
            sb.AppendLine($"{d.Name}");
            sb.AppendLine($"    Klasse: {(string.IsNullOrEmpty(d.DeviceClass) ? "unbekannt" : d.DeviceClass)}, Fehlercode {d.ErrorCode}");
            sb.AppendLine($"    {d.ErrorText}");
            sb.AppendLine($"    {d.DeviceId}");
        }

        // Die Geraeteklasse entscheidet, auf welches Symptom der Befund einzahlt.
        var causes = new Dictionary<Cause, double> { [Cause.DeviceDriver] = 0.9 };
        if (real.Any(d => Is(d, "MEDIA", "AudioEndpoint"))) { causes[Cause.AudioDevice] = 0.7; causes[Cause.Microphone] = 0.6; }
        if (real.Any(d => Is(d, "NET"))) causes[Cause.Network] = 0.8;
        if (real.Any(d => Is(d, "USB", "HIDClass"))) causes[Cause.UsbDevice] = 0.7;
        if (real.Any(d => Is(d, "Camera", "Image"))) causes[Cause.AppPermission] = 0.2;

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "device-errors", Category = Category,
                Title = real.Count == 1 ? "Ein Geraet meldet einen Fehler" : $"{real.Count} Geraete melden einen Fehler",
                Severity = Severity.Critical,
                Summary = string.Join("; ", real.Take(3).Select(d => $"{d.Name} (Code {d.ErrorCode})")) +
                          (real.Count > 3 ? " ..." : ""),
                Detail = sb.ToString().TrimEnd(),
                Recommendation =
                    "Das ist der direkteste Befund ueberhaupt: Windows sagt hier selbst, welches Geraet nicht laeuft.\n\n" +
                    "Code 28 oder 1: Treiber fehlt - beim Hersteller des Geraets oder des Mainboards laden.\n" +
                    "Code 22: Geraet ist deaktiviert - im Geraete-Manager per Rechtsklick aktivieren.\n" +
                    "Code 10 oder 43: Geraet startet nicht - Geraet deinstallieren, neu starten, Treiber neu " +
                    "installieren. Bleibt es dabei, das Geraet an einem anderen Anschluss oder Rechner gegentesten.",
                Causes = causes,
                SymptomIds = new[] { "device-error-code", "device-usb-unknown", "mic-missing-system", "net-no-internet" },
            }
        });
    }

    private static bool Is(ProblemDevice d, params string[] classes)
        => classes.Any(c => d.DeviceClass.Equals(c, StringComparison.OrdinalIgnoreCase));
}

/// <summary>USB-Ereignisse und das selektive Energiesparen an den Anschluessen.</summary>
public sealed class UsbEventCheck : ICheck
{
    public string Name => "USB-Anschluesse";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.UsbDevice, Cause.DeviceDriver };

    private static readonly string[] Providers =
    {
        "Microsoft-Windows-USB-USBHUB3", "Microsoft-Windows-USB-USBXHCI", "Microsoft-Windows-Kernel-PnP", "UASPStor",
    };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        var events = EventLogService.Query("System",
                EventLogService.XpathProviders(Providers, ctx.LookbackDays), 100)
            .Where(e => e.Level is "Fehler" or "Kritisch" or "Warnung")
            .ToList();

        findings.Add(events.Count == 0
            ? new Finding
            {
                Id = "usb-events", Category = Category, Title = "Keine USB-Fehler protokolliert",
                Severity = Severity.Ok,
                Summary = $"In den letzten {ctx.LookbackDays} Tagen hat sich kein USB-Anschluss beschwert.",
            }
            : new Finding
            {
                Id = "usb-events", Category = Category, Title = "USB-Meldungen im Ereignisprotokoll",
                Severity = events.Count > 15 ? Severity.Warning : Severity.Info,
                Summary = $"{events.Count} Meldungen der USB-Verwaltung in den letzten {ctx.LookbackDays} Tagen.",
                Detail = string.Join("\n", events.Take(25).Select(e => e.ToString())),
                Occurrences = events.Count,
                LastOccurrence = events.Max(e => e.Time),
                Recommendation = "Wiederkehrende Meldungen bedeuten, dass sich ein Geraet immer wieder ab- und " +
                                 "anmeldet. Typische Ursachen: zu wenig Strom am Anschluss (Hub oder Frontpanel), " +
                                 "ein defektes Kabel oder das selektive USB-Energiesparen von Windows.",
                Causes = new Dictionary<Cause, double> { [Cause.UsbDevice] = 0.7, [Cause.DeviceDriver] = 0.3 },
                SymptomIds = new[] { "device-usb-unknown", "device-input-lag", "audio-crackle" },
                FixIds = new[] { "usb-suspend-off" },
            });

        if (ctx.Profile.UsbSelectiveSuspend == 1)
        {
            findings.Add(new Finding
            {
                Id = "usb-suspend", Category = Category, Title = "Selektives USB-Energiesparen ist aktiv",
                Severity = Severity.Warning,
                Summary = "Windows darf einzelne USB-Anschluesse im Betrieb abschalten.",
                Detail = "Energieoption 'Einstellung fuer selektives USB-Energiesparen' steht auf 'Aktiviert'.\n\n" +
                         "Das spart im Desktopbetrieb praktisch nichts, ist aber eine haeufige Ursache fuer " +
                         "Aussetzer bei Headsets, Mikrofonen, Funkempfaengern und externen Laufwerken.",
                Recommendation = "Waehrend der Fehlersuche abschalten. Die Reparatur dazu ist umkehrbar.",
                Causes = new Dictionary<Cause, double> { [Cause.UsbDevice] = 0.6, [Cause.PowerSettings] = 0.5 },
                SymptomIds = new[] { "device-input-lag", "device-usb-unknown", "audio-crackle", "mic-not-in-app" },
                FixIds = new[] { "usb-suspend-off" },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}

/// <summary>Der Windows-Datenschutz als Grund fuer eine nicht funktionierende Kamera.</summary>
public sealed class CameraAccessCheck : ICheck
{
    public string Name => "Kamera-Berechtigungen";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.AppPermission };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var finding = ConsentReport.Build(
            ctx.Profile.CameraConsent,
            Category,
            "camera",
            "Einstellungen > Datenschutz und Sicherheit > Kamera oeffnen und dort sowohl den Kamerazugriff als " +
            "auch 'Desktop-Apps den Zugriff auf Ihre Kamera erlauben' einschalten.",
            Cause.DeviceDriver,
            new[] { "device-webcam" },
            new[] { "camera-privacy-allow" });

        return Task.FromResult<IEnumerable<Finding>>(new[] { finding });
    }
}
