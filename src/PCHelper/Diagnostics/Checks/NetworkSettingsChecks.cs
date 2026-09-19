using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;
using PCHelper.Core;

namespace PCHelper.Diagnostics.Checks;

/// <summary>
/// Windows-Einstellungen, die das Netz ausbremsen koennen, ohne dass es jemand merkt: eingetragene Proxy-Reste,
/// Umleitungen in der hosts-Datei, ein verstelltes TCP-Empfangsfenster, Kabel und WLAN gleichzeitig.
/// </summary>
public sealed class NetworkSettingsCheck : ICheck
{
    public string Name => "Netzwerkeinstellungen";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.Software, Cause.Wifi };

    /// <summary>Programme, die im Hintergrund die Leitung fuellen koennen.</summary>
    private static readonly (string Process, string Text, bool Always)[] BandwidthHogs =
    {
        ("BitTorrent", "BitTorrent laeuft und teilt Daten", true),
        ("uTorrent", "uTorrent laeuft und teilt Daten", true),
        ("qbittorrent", "qBittorrent laeuft und teilt Daten", true),
        ("TiWorker", "Windows Update arbeitet im Hintergrund", true),
        ("MoUsoCoreWorker", "Windows Update laedt gerade", true),
        // Launcher und Sync-Dienste laufen bei fast jedem dauerhaft - nur bei einem Netzwerk-Symptom relevant.
        ("steam", "Steam laeuft und laedt womoeglich Spiele oder Updates", false),
        ("EpicGamesLauncher", "Epic Games laeuft und laedt womoeglich im Hintergrund", false),
        ("OneDrive", "OneDrive laeuft und gleicht womoeglich Dateien ab", false),
        ("Dropbox", "Dropbox laeuft und gleicht womoeglich Dateien ab", false),
    };

    public async Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        CheckProxy(findings);
        CheckHostsFile(findings);
        await CheckTcpAutotuningAsync(findings, ct);
        CheckDualLink(findings, ctx.Profile);
        CheckBandwidth(findings, ctx);

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Id = "net-settings", Category = Category, Title = "Netzwerkeinstellungen unauffaellig",
                Severity = Severity.Ok,
                Summary = "Kein Proxy im Weg, das TCP-Empfangsfenster steht richtig, und es laeuft nichts, was die Leitung fuellt.",
            });
        }

        return findings;
    }

    private static void CheckProxy(List<Finding> findings)
    {
        int proxyEnable = 0, autoDetect = 0;
        string? proxyServer = null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            proxyEnable = key?.GetValue("ProxyEnable") as int? ?? 0;
            autoDetect = key?.GetValue("AutoDetect") as int? ?? 0;
            proxyServer = key?.GetValue("ProxyServer") as string;
        }
        catch { /* Einstellungen nicht lesbar: dann kein Befund */ }

        if (proxyEnable == 1 && !string.IsNullOrWhiteSpace(proxyServer))
        {
            findings.Add(new Finding
            {
                Id = "net-proxy", Category = "Netzwerk", Title = "Ein Proxy-Server ist eingetragen",
                Severity = Severity.Warning,
                Summary = $"Jede Internetanfrage laeuft zuerst zu '{proxyServer}'.",
                Detail = "Ist dieser Server nicht erreichbar, wartet Windows bei jeder einzelnen Anfrage auf einen Timeout - das " +
                         "fuehlt sich an wie ein eingefrorener Rechner. Solche Eintraege bleiben oft von alter Software oder " +
                         "Schadprogrammen zurueck.",
                Recommendation = "Im Privathaushalt braucht man praktisch nie einen Proxy. Wenn du keinen eingetragen hast, entfernen. " +
                                 "In einer Firma vorher nachfragen.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.6, [Cause.Software] = 0.5 },
                SymptomIds = new[] { "net-slow", "net-no-internet" },
                FixIds = new[] { "proxy-off" },
            });
        }

        if (autoDetect == 1)
        {
            findings.Add(new Finding
            {
                Id = "net-autoproxy", Category = "Netzwerk", Title = "Automatische Proxy-Suche ist eingeschaltet",
                Severity = Severity.Info,
                Summary = "Windows sucht bei jeder neuen Verbindung selbsttaetig nach einem Proxy-Server.",
                Detail = "Im Heimnetz gibt es keinen, also wartet Windows jedes Mal vergeblich, bis die Suche aufgibt. Das " +
                         "verzoegert spuerbar den ersten Seitenaufruf.",
                Recommendation = "Zuhause gefahrlos abschaltbar. In einem Firmennetz nicht anfassen.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.2 },
                SymptomIds = new[] { "net-slow" },
                FixIds = new[] { "auto-proxy-off" },
            });
        }
    }

    private static void CheckHostsFile(List<Finding> findings)
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
            if (!File.Exists(path)) return;

            var entries = File.ReadLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, "docker|localhost|kubernetes", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToList();
            if (entries.Count == 0) return;

            findings.Add(new Finding
            {
                Id = "net-hosts", Category = "Netzwerk", Title = "Eigene Umleitungen in der hosts-Datei",
                Severity = Severity.Info,
                Summary = $"{entries.Count} Eintraege leiten Webadressen um oder sperren sie.",
                Detail = "Die hosts-Datei hat Vorrang vor allem anderen. Sind Eintraege veraltet, laufen betroffene Seiten ins Leere " +
                         $"oder in lange Wartezeiten.\n\nDatei: {path}\n\nDie ersten Eintraege:\n" +
                         string.Join("\n", entries.Take(8).Select(e => "  " + e)),
                Recommendation = "Wenn du die Eintraege selbst angelegt hast (etwa zum Sperren von Werbung), ist alles in Ordnung. " +
                                 "Kennst du sie nicht oder fehlen dir bestimmte Seiten: Datei ansehen und alte Eintraege mit einem # " +
                                 "am Zeilenanfang stilllegen.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.1, [Cause.Software] = 0.2 },
            });
        }
        catch (Exception ex)
        {
            Log.Warn("hosts-Datei nicht lesbar: " + ex.Message);
        }
    }

    /// <summary>
    /// Steht das TCP-Empfangsfenster auf "disabled" oder "restricted", bricht der Durchsatz auf schnellen Leitungen
    /// massiv ein. Wird gern von alten "Tuning-Werkzeugen" verstellt. Gelesen wird ueber PowerShell, weil die
    /// Ausgabe von netsh je nach Windows-Sprache anders beschriftet ist.
    /// </summary>
    private static async Task CheckTcpAutotuningAsync(List<Finding> findings, CancellationToken ct)
    {
        var result = await PowerShellRunner.RunAsync("(Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal", 20_000, ct);
        var level = result.StdOut.Trim();
        if (!result.Success || level.Length == 0 || level.Equals("Normal", StringComparison.OrdinalIgnoreCase)) return;

        findings.Add(new Finding
        {
            Id = "net-tcp-autotuning", Category = "Netzwerk", Title = "Das TCP-Empfangsfenster ist verstellt",
            Severity = Severity.Warning,
            Summary = $"Die automatische Abstimmung steht auf \"{level}\" statt auf \"Normal\".",
            Detail = "Windows darf dadurch nur kleine Datenmengen am Stueck annehmen, was den Durchsatz auf schnellen Leitungen stark " +
                     "ausbremst. Der Wert wird oft von aelteren Optimierungs-Programmen verstellt.",
            Recommendation = "Auf \"normal\" zuruecksetzen.",
            Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.5, [Cause.Software] = 0.4 },
            SymptomIds = new[] { "net-slow" },
            FixIds = new[] { "tcp-autotuning-normal" },
        });
    }

    /// <summary>Kabel und WLAN im selben Netz: Windows muss sich staendig entscheiden, und manche Programme erwischen die langsamere Leitung.</summary>
    private static void CheckDualLink(List<Finding> findings, SystemProfile profile)
    {
        var connected = profile.NetworkAdapters
            .Where(a => a.IsConnected && a.HasUsableIpv4 && !NetProbe.VpnPattern.IsMatch(a.Name + " " + a.ConnectionName))
            .Select(a => (Adapter: a, Subnet: SubnetOf(a)))
            .Where(x => x.Subnet is not null)
            .ToList();

        foreach (var group in connected.GroupBy(x => x.Subnet))
        {
            var wired = group.Where(x => !x.Adapter.IsWireless).ToList();
            var wireless = group.Where(x => x.Adapter.IsWireless).ToList();
            if (wired.Count == 0 || wireless.Count == 0) continue;

            findings.Add(new Finding
            {
                Id = "net-dual-link", Category = "Netzwerk", Title = "Kabel und WLAN sind gleichzeitig verbunden",
                Severity = Severity.Warning,
                Summary = $"Der Rechner haengt per {string.Join(" und ", group.Select(x => x.Adapter.ConnectionName))} am selben Netz.",
                Detail = "Windows muss sich dann staendig entscheiden, welche Verbindung es nimmt, und manche Programme erwischen die " +
                         "langsamere Leitung.",
                Recommendation = "Wenn das Netzwerkkabel steckt, das WLAN abschalten. Das Kabel ist immer schneller und stabiler.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.4, [Cause.Wifi] = 0.5 },
                SymptomIds = new[] { "net-slow", "net-drops" },
                FixIds = new[] { "wlan-off-with-cable" },
            });
            return;
        }
    }

    private static string? SubnetOf(NetAdapter adapter)
    {
        var ip = adapter.IpAddresses.FirstOrDefault(i => IPAddress.TryParse(i, out var a)
            && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !i.StartsWith("169.254.", StringComparison.Ordinal));
        var parts = ip?.Split('.');
        return parts is { Length: 4 } ? string.Join('.', parts.Take(3)) : null;
    }

    private static void CheckBandwidth(List<Finding> findings, CheckContext ctx)
    {
        bool networkSymptom = ctx.Symptom?.Id is "net-slow" or "net-drops";

        var found = BandwidthHogs
            .Where(h => (h.Always || networkSymptom) && ctx.Profile.RunningProcesses.Contains(h.Process))
            .ToList();
        if (found.Count == 0) return;

        var detail = new StringBuilder();
        foreach (var h in found) detail.AppendLine("- " + h.Text);

        findings.Add(new Finding
        {
            Id = "net-bandwidth", Category = "Netzwerk", Title = "Im Hintergrund laeuft etwas, das die Leitung fuellen kann",
            Severity = Severity.Info,
            Summary = string.Join("; ", found.Select(f => f.Text)) + ".",
            Detail = detail.ToString().TrimEnd(),
            Recommendation = "Wenn das Internet gerade langsam ist, das Programm einmal beenden und neu messen. Programme, die " +
                             "im Hintergrund viel laden, machen alles andere zaehe - auch wenn die Leitung eigentlich schnell ist.",
            Causes = new Dictionary<Cause, double> { [Cause.Software] = 0.3, [Cause.Network] = 0.2 },
            SymptomIds = new[] { "net-slow" },
        });
    }
}

/// <summary>Netzwerkkarten, die in Windows eingetragen sind, aber nicht mehr im Rechner stecken.</summary>
public sealed class GhostNetworkAdapterCheck : ICheck
{
    public string Name => "Netzwerkkarten-Karteileichen";
    public string Category => "Netzwerk";
    public IReadOnlyList<Cause> Topics { get; } = new[] { Cause.Network, Cause.DeviceDriver };

    /// <summary>IF_OPER_STATUS "NotPresent" - so nennt Windows Adapter, die nicht mehr vorhanden sind.</summary>
    private const int NotPresent = 6;

    public Task<IEnumerable<Finding>> RunAsync(CheckContext ctx, CancellationToken ct)
    {
        var ghosts = Wmi.Query(
                "SELECT Name, InterfaceDescription, InterfaceOperationalStatus FROM MSFT_NetAdapter", @"root\StandardCimv2")
            .Where(r => r.Int("InterfaceOperationalStatus") == NotPresent)
            .Select(r => r.Str("Name"))
            .Where(n => n.Length > 0)
            .ToList();

        if (ghosts.Count == 0) return Task.FromResult<IEnumerable<Finding>>(Array.Empty<Finding>());

        return Task.FromResult<IEnumerable<Finding>>(new[]
        {
            new Finding
            {
                Id = "net-ghost-adapters", Category = Category, Title = "Karteileichen unter den Netzwerkkarten",
                Severity = Severity.Info,
                Summary = $"{ghosts.Count} Netzwerkkarten sind eingetragen, stecken aber nicht mehr im Rechner: {string.Join(", ", ghosts)}.",
                Detail = "Reste von alten Treibern, VPN-Programmen oder USB-Adaptern. Sie bremsen nichts direkt, machen aber die " +
                         "Netzwerkeinstellungen unuebersichtlich und koennen die Reihenfolge durcheinanderbringen.",
                Recommendation = "Koennen weg. Steckst du das Geraet wieder ein, richtet Windows es von selbst neu ein.",
                Causes = new Dictionary<Cause, double> { [Cause.Network] = 0.1, [Cause.DeviceDriver] = 0.1 },
                FixIds = new[] { "ghost-adapters-remove" },
            }
        });
    }
}
