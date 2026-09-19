using PCHelper.Maintenance;

namespace PCHelper.Diagnostics.Checks;

/// <summary>
/// Das Haekchen "Computer kann das Geraet ausschalten, um Energie zu sparen" sitzt pro Geraet und ist unabhaengig vom
/// Energieplan - eine zweite Stromspar-Ebene, die dafuer sorgt, dass Kameras und Mikrofone mitten im Betrieb verschwinden.
/// </summary>
public sealed class DevicePowerSavingCheck : ICheck
{
    public string Name => "Stromsparen einzelner Geraete";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.UsbDevice, Cause.PowerSettings, Cause.DeviceDriver };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var devices = DeviceScanner.PowerSavingDevices(out var readable, onlyChangeable: true);

        // Ohne Administratorrechte liefert Windows hier unter Umstaenden nichts - dann lieber schweigen als "alles gut" behaupten.
        if (!readable) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        if (devices.Count == 0)
        {
            return Task.FromResult<IEnumerable<Finding>>(new[]
            {
                new Finding
                {
                    Id = "device-power-saving", Category = Category, Title = "Windows darf keine USB-Geraete abschalten",
                    Severity = Severity.Ok,
                    Summary = "Bei USB-Verteilern, USB-Geraeten und Netzwerkkarten ist das Stromsparen abgeschaltet.",
                }
            });
        }

        var names = string.Join(", ", devices.Select(DeviceScanner.ShortName).Distinct().Take(6));
        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "device-power-saving", Category = Category, Title = "Windows darf einzelne Geraete abschalten",
                Severity = Severity.Warning,
                Summary = $"Bei {devices.Count} USB-Geraeten oder Netzwerkkarten ist das Stromsparen erlaubt: {names}.",
                Detail = "Im Geraete-Manager ist bei diesen Geraeten das Haekchen \"Computer kann das Geraet ausschalten, um Energie zu " +
                         "sparen\" gesetzt. Das ist eine zweite, vom Energieplan unabhaengige Ebene - deshalb bringt es oft nichts, nur den " +
                         "Energieplan umzustellen.\n\nWacht ein Geraet aus dem Stromsparen nicht sauber auf, bleibt ein \"Unbekanntes " +
                         "USB-Geraet\" zurueck.",
                Recommendation = "Fuer USB-Verteiler und Netzwerkkarten abschalten. Der eingesparte Strom liegt im Bereich einer " +
                                 "Gluehbirne, die kurz blinkt.",
                Causes = new Dictionary<Cause, double> { [Cause.UsbDevice] = 0.6, [Cause.PowerSettings] = 0.4, [Cause.DeviceDriver] = 0.2 },
                SymptomIds = new[] { "device-usb-unknown", "device-webcam", "device-input-lag", "net-drops" },
                FixIds = new[] { "device-power-saving-off" },
            }
        });
    }
}

/// <summary>"Unbekanntes USB-Geraet": Ein Anschluss hat versucht, ein Geraet anzumelden, und ist steckengeblieben.</summary>
public sealed class DeadUsbEntryCheck : ICheck
{
    public string Name => "Haengengebliebene USB-Anmeldungen";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.UsbDevice, Cause.DeviceDriver };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var dead = DeviceScanner.DeadUsbEntries();
        if (dead.Count == 0) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "device-dead-usb", Category = Category, Title = "Ein USB-Geraet hat sich nicht sauber angemeldet",
                Severity = Severity.Warning,
                Summary = $"{dead.Count} Eintrag/Eintraege \"Unbekanntes USB-Geraet\" (VID_0000).",
                Detail = string.Join("\n", dead.Select(d => $"{(d.Name.Length > 0 ? d.Name : "Unbekanntes USB-Geraet")}\n    {d.DeviceId}")),
                Recommendation = "Solche Leichen sammeln sich an, wenn Geraete aus dem Stromsparen nicht sauber aufwachen - und sie koennen " +
                                 "dazu fuehren, dass der Anschluss beim naechsten Mal gar nicht mehr will. In PC Helper unter " +
                                 "\"Wartung\" > \"Geraete\" laesst sich der Eintrag wegraeumen; steckt das Geraet noch, meldet Windows es " +
                                 "sofort sauber neu an. Danach das USB-Stromsparen abschalten, damit es nicht wiederkommt.",
                Causes = new Dictionary<Cause, double> { [Cause.UsbDevice] = 0.8, [Cause.PowerSettings] = 0.3, [Cause.DeviceDriver] = 0.3 },
                SymptomIds = new[] { "device-usb-unknown", "device-webcam" },
                FixIds = new[] { "usb-suspend-off", "device-power-saving-off" },
            }
        });
    }
}

/// <summary>Mehrere Kameras und Programme, die die Kamera gerade belegen - die haeufigsten Gruende fuer "kein Bild".</summary>
public sealed class CameraDeviceCheck : ICheck
{
    public string Name => "Kameras";
    public string Category => "Geraete";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.AppPermission, Cause.UsbDevice, Cause.DeviceDriver, Cause.Software };

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        var users = DeviceScanner.CameraUsers();
        if (users.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "camera-in-use", Category = Category, Title = "Die Kamera ist gerade in Benutzung",
                Severity = Severity.Info,
                Summary = $"Belegt von: {string.Join(", ", users)}.",
                Detail = "Eine Webcam kann meist nur von einem Programm gleichzeitig benutzt werden. Wenn ein zweites Programm " +
                         "\"kein Bild\" zeigt oder das Bild schwarz bleibt, ist das oft der ganze Grund.",
                Recommendation = "Das andere Programm schliessen, dann geht die Kamera wieder.",
                Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.5, [Cause.AppPermission] = 0.3 },
                SymptomIds = new[] { "device-webcam" },
            });
        }

        var cameras = DeviceScanner.Cameras();
        if (cameras.Count > 1)
        {
            findings.Add(new Finding
            {
                Id = "camera-multiple", Category = Category, Title = "Mehrere Kameras sind angemeldet",
                Severity = Severity.Info,
                Summary = string.Join(", ", cameras.Select(c => c.Name)) + ".",
                Detail = "Programme greifen sich beim Start oft einfach die erste Kamera in der Liste. Meldet ein Programm \"die Kamera " +
                         "geht nicht\", hat es womoeglich nur die falsche erwischt - etwa eine virtuelle Kamera statt der echten.",
                Recommendation = "Im Programm selbst (OBS, Discord, Teams) nachsehen, welche Kamera ausgewaehlt ist.",
                Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.3, [Cause.AppPermission] = 0.2 },
                SymptomIds = new[] { "device-webcam" },
            });
        }

        return Task.FromResult<IEnumerable<Finding>>(findings);
    }
}
