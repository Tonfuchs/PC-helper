using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using PCHelper.Core;

namespace PCHelper.Diagnostics;

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public string PnpDeviceId { get; init; } = "";

    /// <summary>Bei NVIDIA die uebliche Anzeigeversion (z. B. "576.02"), sonst null.</summary>
    public string? MarketingDriverVersion { get; init; }

    public bool IsNvidia => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                            || Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                            || Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);

    public string DriverDisplay => MarketingDriverVersion is null
        ? DriverVersion
        : $"{MarketingDriverVersion} ({DriverVersion})";

    public int? DriverAgeDays => DriverDate is null ? null : (int)(DateTime.Now - DriverDate.Value).TotalDays;
}

public sealed class MemoryModule
{
    public string Slot { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public double CapacityGb { get; init; }
    /// <summary>Vom SPD gemeldete Nenngeschwindigkeit.</summary>
    public int RatedSpeedMhz { get; init; }
    /// <summary>Tatsaechlich konfigurierte Geschwindigkeit (zeigt EXPO/XMP an).</summary>
    public int ConfiguredSpeedMhz { get; init; }
    public int SmbiosMemoryType { get; init; }

    public string TypeName => SmbiosMemoryType switch
    {
        26 => "DDR4",
        34 => "DDR5",
        24 => "DDR3",
        _ => "DDR"
    };
}

public sealed class DiskInfo
{
    public string Model { get; init; } = "";
    public double SizeGb { get; init; }
    public string MediaType { get; init; } = "";
    public string HealthStatus { get; init; } = "";
    public string BusType { get; init; } = "";
    public int? TemperatureC { get; init; }
}

public sealed class VolumeInfo
{
    public string Letter { get; init; } = "";
    public double TotalGb { get; init; }
    public double FreeGb { get; init; }
    public double FreePercent => TotalGb <= 0 ? 0 : Math.Round(FreeGb / TotalGb * 100, 1);
}

/// <summary>
/// Ein Wiedergabe- oder Aufnahmegeraet, so wie Windows es fuehrt - ausdruecklich
/// inklusive der deaktivierten und abgesteckten Geraete, denn genau die fehlen
/// in den Sound-Einstellungen und werden dadurch uebersehen.
/// </summary>
public sealed class AudioEndpoint
{
    public string Name { get; init; } = "";
    public bool IsCapture { get; init; }

    /// <summary>Rohwert aus der Registrierung (1 aktiv, 2 deaktiviert, 4 nicht vorhanden, 8 nicht angeschlossen).</summary>
    public int StateCode { get; init; }

    public bool IsActive => StateCode == 1;
    public bool IsDisabled => StateCode == 2;
    public bool IsUnplugged => StateCode == 8;

    public string StateText => StateCode switch
    {
        1 => "aktiv",
        2 => "deaktiviert",
        4 => "nicht vorhanden",
        8 => "nicht angeschlossen",
        _ => "unbekannt (" + StateCode + ")",
    };

    public string KindText => IsCapture ? "Aufnahme" : "Wiedergabe";

    public override string ToString() => $"{Name} [{KindText}: {StateText}]";
}

/// <summary>Ein Netzwerkadapter samt IP-Konfiguration.</summary>
public sealed class NetAdapter
{
    public string Name { get; init; } = "";
    public string ConnectionName { get; init; } = "";
    public bool Enabled { get; init; }
    public int ConnectionStatus { get; init; }
    public string MacAddress { get; init; } = "";
    public long SpeedBitsPerSecond { get; init; }
    public List<string> IpAddresses { get; } = new();
    public List<string> DnsServers { get; } = new();
    public string Gateway { get; init; } = "";
    public bool DhcpEnabled { get; init; }

    public bool IsWireless =>
        Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("802.11", StringComparison.OrdinalIgnoreCase) ||
        ConnectionName.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
        ConnectionName.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);

    public bool IsConnected => ConnectionStatus == 2;

    /// <summary>Adresse aus dem 169.254er-Bereich: DHCP hat keine Antwort bekommen.</summary>
    public bool HasApipaAddress => IpAddresses.Any(ip => ip.StartsWith("169.254.", StringComparison.Ordinal));

    public bool HasUsableIpv4 => IpAddresses.Any(ip =>
        ip.Contains('.') && !ip.StartsWith("169.254.", StringComparison.Ordinal) && ip != "0.0.0.0");

    public string SpeedText => SpeedBitsPerSecond <= 0
        ? "unbekannt"
        : SpeedBitsPerSecond >= 1_000_000_000
            ? $"{SpeedBitsPerSecond / 1_000_000_000.0:0.#} Gbit/s"
            : $"{SpeedBitsPerSecond / 1_000_000.0:0} Mbit/s";

    public string StatusText => ConnectionStatus switch
    {
        0 => "getrennt",
        1 => "verbindet",
        2 => "verbunden",
        3 => "trennt",
        4 => "Hardware nicht vorhanden",
        5 => "Hardware deaktiviert",
        6 => "Hardwarefehler",
        7 => "Medium getrennt (Kabel steckt nicht)",
        _ => "unbekannt",
    };
}

/// <summary>Ein Geraet, dem Windows im Geraete-Manager einen Fehlercode zugewiesen hat.</summary>
public sealed class ProblemDevice
{
    public string Name { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public int ErrorCode { get; init; }

    /// <summary>Klartext zum Fehlercode - die Codes sind seit Jahrzehnten stabil dokumentiert.</summary>
    public string ErrorText => ErrorCode switch
    {
        1 => "Das Geraet ist nicht richtig konfiguriert (kein passender Treiber).",
        3 => "Der Treiber ist beschaedigt oder es fehlt Arbeitsspeicher.",
        10 => "Das Geraet kann nicht gestartet werden - meist Treiber oder Firmware.",
        12 => "Es sind nicht genuegend freie Ressourcen vorhanden.",
        14 => "Das Geraet arbeitet erst nach einem Neustart richtig.",
        18 => "Die Treiber muessen neu installiert werden.",
        19 => "Die Registrierungseintraege des Geraets sind beschaedigt.",
        21 => "Windows entfernt das Geraet gerade.",
        22 => "Das Geraet ist deaktiviert.",
        24 => "Das Geraet ist nicht vorhanden oder nicht richtig angeschlossen.",
        28 => "Fuer dieses Geraet ist kein Treiber installiert.",
        31 => "Windows kann keine Treiber laden, die dieses Geraet benoetigt.",
        32 => "Der Starttyp des Treibers ist deaktiviert.",
        37 => "Der Treiber konnte nicht initialisiert werden.",
        38 => "Eine frueher geladene Treiberinstanz blockiert noch - ein Neustart hilft.",
        39 => "Der Treiber ist beschaedigt oder fehlt.",
        43 => "Windows hat das Geraet gestoppt, weil es Fehler gemeldet hat.",
        45 => "Das Geraet ist derzeit nicht angeschlossen (Eintrag stammt aus einer frueheren Verbindung).",
        _ => "Windows meldet Fehlercode " + ErrorCode + ".",
    };
}

/// <summary>Zugriffsrechte einer Geraeteklasse (Mikrofon, Kamera) laut Windows-Datenschutz.</summary>
public sealed class PrivacyConsent
{
    public required string Kind { get; init; }

    /// <summary>Globale Einstellung des angemeldeten Benutzers ("Allow"/"Deny"/null).</summary>
    public string? UserValue { get; init; }

    /// <summary>Einstellung fuer klassische Desktop-Programme (der oft uebersehene Schalter).</summary>
    public string? DesktopAppsValue { get; init; }

    /// <summary>Per Gruppenrichtlinie gesetzte Sperre, falls vorhanden.</summary>
    public string? PolicyValue { get; init; }

    /// <summary>Namen der Anwendungen, denen der Zugriff ausdruecklich verweigert wurde.</summary>
    public List<string> DeniedApps { get; } = new();

    public bool GloballyBlocked => string.Equals(UserValue, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool DesktopAppsBlocked => string.Equals(DesktopAppsValue, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool PolicyBlocked => string.Equals(PolicyValue, "Deny", StringComparison.OrdinalIgnoreCase);
    public bool AnyBlock => GloballyBlocked || DesktopAppsBlocked || PolicyBlocked || DeniedApps.Count > 0;
}

/// <summary>
/// Einmalig erhobene Bestandsaufnahme des Systems. Alle Pruefungen arbeiten
/// auf diesem Objekt, damit WMI nicht mehrfach abgefragt wird.
/// </summary>
public sealed class SystemProfile
{
    public DateTime CollectedAt { get; private init; } = DateTime.Now;

    // CPU
    public string CpuName { get; private set; } = "unbekannt";
    public int CpuCores { get; private set; }
    public int CpuThreads { get; private set; }

    // Mainboard / BIOS
    public string BoardManufacturer { get; private set; } = "";
    public string BoardProduct { get; private set; } = "";
    public string BiosVersion { get; private set; } = "";
    public DateTime? BiosDate { get; private set; }
    public string SystemManufacturer { get; private set; } = "";
    public string SystemModel { get; private set; } = "";

    // Grafik / Anzeige
    public List<GpuInfo> Gpus { get; } = new();
    public IReadOnlyList<DisplayTarget> Displays { get; private set; } = Array.Empty<DisplayTarget>();

    // Speicher
    public List<MemoryModule> MemoryModules { get; } = new();
    public double TotalMemoryGb { get; private set; }

    // Datentraeger
    public List<DiskInfo> Disks { get; } = new();
    public List<VolumeInfo> Volumes { get; } = new();

    // Betriebssystem
    public string OsCaption { get; private set; } = "";
    public string OsDisplayVersion { get; private set; } = "";
    public string OsBuild { get; private set; } = "";
    public DateTime? OsInstallDate { get; private set; }
    public DateTime? LastBootTime { get; private set; }
    public TimeSpan Uptime => Native.GetUptime();

    // Energie
    public string PowerPlanName { get; private set; } = "";
    public bool FastStartupEnabled { get; private set; }
    public uint? VideoIdleTimeoutSec { get; private set; }
    public uint? StandbyIdleTimeoutSec { get; private set; }
    public uint? PciExpressAspm { get; private set; }
    public uint? UsbSelectiveSuspend { get; private set; }
    public uint? ProcessorMinState { get; private set; }

    // Ton
    public List<AudioEndpoint> AudioEndpoints { get; } = new();

    // Netzwerk
    public List<NetAdapter> NetworkAdapters { get; } = new();

    // Geraete-Manager
    public List<ProblemDevice> ProblemDevices { get; } = new();

    // Dienste, die fuer Ton, Netzwerk und Geraete zustaendig sind
    public Dictionary<string, (string State, string StartMode)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Windows-Datenschutz
    public PrivacyConsent? MicrophoneConsent { get; private set; }
    public PrivacyConsent? CameraConsent { get; private set; }

    // Software
    public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> StartupEntries { get; } = new();

    // Registry / Grafiktreiber-Feinheiten
    public int? TdrDelaySeconds { get; private set; }
    public int? TdrLevel { get; private set; }

    public string PrimaryGpuName => Gpus.FirstOrDefault(g => g.IsNvidia)?.Name ?? Gpus.FirstOrDefault()?.Name ?? "unbekannt";

    /// <summary>Ist mindestens ein Monitor per DisplayPort angebunden?</summary>
    public bool HasDisplayPort => Displays.Any(d => d.IsDisplayPort);

    /// <summary>Laeuft der Speicher schneller als der DDR5-/DDR4-Standardtakt (EXPO/XMP aktiv)?</summary>
    public bool MemoryOverclocked
    {
        get
        {
            foreach (var m in MemoryModules)
            {
                if (m.ConfiguredSpeedMhz <= 0) continue;
                int jedecBase = m.SmbiosMemoryType == 34 ? 5600 : 3200; // DDR5 bzw. DDR4 Obergrenze ohne Profil
                if (m.ConfiguredSpeedMhz > jedecBase) return true;
            }
            return false;
        }
    }

    public static async Task<SystemProfile> CollectAsync(CancellationToken ct = default)
    {
        var p = new SystemProfile();
        await Task.Run(() =>
        {
            p.CollectCpu();
            p.CollectBoard();
            p.CollectGpu();
            p.CollectMemory();
            p.CollectStorage();
            p.CollectOs();
            p.CollectPower();
            p.CollectSoftware();
            p.CollectRegistry();
            p.CollectAudioEndpoints();
            p.CollectNetwork();
            p.CollectProblemDevices();
            p.CollectServices();
            p.CollectPrivacy();
        }, ct);

        p.Displays = DisplayConfig.GetActiveTargets();
        return p;
    }

    private void CollectCpu()
    {
        var rows = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        if (rows.Count == 0) return;
        CpuName = rows[0].Str("Name");
        CpuCores = rows.Sum(r => r.Int("NumberOfCores") ?? 0);
        CpuThreads = rows.Sum(r => r.Int("NumberOfLogicalProcessors") ?? 0);
    }

    private void CollectBoard()
    {
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        if (board is not null)
        {
            BoardManufacturer = board.Str("Manufacturer");
            BoardProduct = board.Str("Product");
        }

        var bios = Wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS").FirstOrDefault();
        if (bios is not null)
        {
            BiosVersion = bios.Str("SMBIOSBIOSVersion");
            BiosDate = bios.Date("ReleaseDate");
        }

        var sys = Wmi.Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        if (sys is not null)
        {
            SystemManufacturer = sys.Str("Manufacturer");
            SystemModel = sys.Str("Model");
            var total = sys.Long("TotalPhysicalMemory");
            if (total is > 0) TotalMemoryGb = Math.Round(total.Value / (1024.0 * 1024 * 1024), 1);
        }
    }

    private void CollectGpu()
    {
        foreach (var row in Wmi.Query(
            "SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = row.Str("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var driver = row.Str("DriverVersion");
            Gpus.Add(new GpuInfo
            {
                Name = name,
                DriverVersion = driver,
                DriverDate = row.Date("DriverDate"),
                PnpDeviceId = row.Str("PNPDeviceID"),
                MarketingDriverVersion = TryNvidiaMarketingVersion(name, driver),
            });
        }
    }

    /// <summary>
    /// NVIDIA-Treiber melden sich als "32.0.15.7602"; die Anzeigeversion (576.02)
    /// steckt in den letzten fuenf Ziffern der zusammengesetzten Zahl.
    /// </summary>
    internal static string? TryNvidiaMarketingVersion(string gpuName, string driverVersion)
    {
        if (!gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            && !gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return null;

        var digits = new string(driverVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;

        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    private void CollectMemory()
    {
        foreach (var row in Wmi.Query(
            "SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            var cap = row.Long("Capacity") ?? 0;
            MemoryModules.Add(new MemoryModule
            {
                Slot = row.Str("DeviceLocator"),
                Manufacturer = row.Str("Manufacturer"),
                PartNumber = row.Str("PartNumber"),
                CapacityGb = Math.Round(cap / (1024.0 * 1024 * 1024), 1),
                RatedSpeedMhz = row.Int("Speed") ?? 0,
                ConfiguredSpeedMhz = row.Int("ConfiguredClockSpeed") ?? 0,
                SmbiosMemoryType = row.Int("SMBIOSMemoryType") ?? 0,
            });
        }

        if (TotalMemoryGb <= 0 && MemoryModules.Count > 0)
            TotalMemoryGb = Math.Round(MemoryModules.Sum(m => m.CapacityGb), 1);
    }

    private void CollectStorage()
    {
        // Physische Datentraeger inkl. Gesundheitsstatus (Storage-Namespace, Win 8+).
        var physical = Wmi.Query(
            "SELECT FriendlyName, Size, MediaType, HealthStatus, BusType FROM MSFT_PhysicalDisk",
            @"root\Microsoft\Windows\Storage");

        foreach (var row in physical)
        {
            Disks.Add(new DiskInfo
            {
                Model = row.Str("FriendlyName"),
                SizeGb = Math.Round((row.Long("Size") ?? 0) / (1024.0 * 1024 * 1024), 0),
                MediaType = (row.Int("MediaType")) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "unbekannt" },
                BusType = (row.Int("BusType")) switch { 17 => "NVMe", 11 => "SATA", 8 => "RAID", 7 => "USB", _ => "" },
                HealthStatus = (row.Int("HealthStatus")) switch { 0 => "Fehlerfrei", 1 => "Warnung", 2 => "Ungesund", _ => "unbekannt" },
            });
        }

        // Rueckfallebene, falls der Storage-Namespace nicht verfuegbar ist.
        if (Disks.Count == 0)
        {
            foreach (var row in Wmi.Query("SELECT Model, Size, Status FROM Win32_DiskDrive"))
            {
                Disks.Add(new DiskInfo
                {
                    Model = row.Str("Model"),
                    SizeGb = Math.Round((row.Long("Size") ?? 0) / (1024.0 * 1024 * 1024), 0),
                    HealthStatus = row.Str("Status") == "OK" ? "Fehlerfrei" : row.Str("Status"),
                });
            }
        }

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                Volumes.Add(new VolumeInfo
                {
                    Letter = d.Name.TrimEnd('\\'),
                    TotalGb = Math.Round(d.TotalSize / (1024.0 * 1024 * 1024), 1),
                    FreeGb = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1),
                });
            }
            catch { /* Laufwerk nicht lesbar -> ueberspringen */ }
        }
    }

    private void CollectOs()
    {
        var os = Wmi.Query("SELECT Caption, BuildNumber, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        if (os is not null)
        {
            OsCaption = os.Str("Caption");
            OsBuild = os.Str("BuildNumber");
            OsInstallDate = os.Date("InstallDate");
            LastBootTime = os.Date("LastBootUpTime");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                OsDisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? "";
                var ubr = key.GetValue("UBR");
                var build = key.GetValue("CurrentBuildNumber")?.ToString() ?? OsBuild;
                OsBuild = ubr is null ? build : $"{build}.{ubr}";
            }
        }
        catch (Exception ex) { Log.Warn("Windows-Version aus Registry: " + ex.Message); }
    }

    private void CollectPower()
    {
        PowerPlanName = PowerApi.GetActiveSchemeName();
        VideoIdleTimeoutSec = PowerApi.ReadAcValue(PowerApi.SubVideo, PowerApi.VideoIdleTimeout);
        StandbyIdleTimeoutSec = PowerApi.ReadAcValue(PowerApi.SubSleep, PowerApi.StandbyIdleTimeout);
        PciExpressAspm = PowerApi.ReadAcValue(PowerApi.SubPciExpress, PowerApi.PciExpressAspm);
        UsbSelectiveSuspend = PowerApi.ReadAcValue(PowerApi.SubUsb, PowerApi.UsbSelectiveSuspend);
        ProcessorMinState = PowerApi.ReadAcValue(PowerApi.SubProcessor, PowerApi.ProcessorMinState);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            FastStartupEnabled = key?.GetValue("HiberbootEnabled") is int i && i != 0;
        }
        catch { FastStartupEnabled = false; }
    }

    private void CollectSoftware()
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try { RunningProcesses.Add(proc.ProcessName); } catch { }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Warn("Prozessliste: " + ex.Message); }

        foreach (var row in Wmi.Query("SELECT Name, Command, Location FROM Win32_StartupCommand"))
        {
            var name = row.Str("Name");
            if (!string.IsNullOrWhiteSpace(name)) StartupEntries.Add(name);
        }
    }

    private void CollectRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key is null) return;
            if (key.GetValue("TdrDelay") is int d) TdrDelaySeconds = d;
            if (key.GetValue("TdrLevel") is int l) TdrLevel = l;
        }
        catch { /* Standardwerte gelten, wenn die Werte fehlen */ }
    }

    /// <summary>
    /// Liest die Audiogeraete direkt aus der Registrierung statt ueber die
    /// Sound-Einstellungen. Nur dort stehen auch die deaktivierten und
    /// abgesteckten Geraete - und genau die sind bei "Mikrofon wird nicht
    /// erkannt" der haeufigste Treffer.
    /// </summary>
    private void CollectAudioEndpoints()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

        foreach (var (branch, isCapture) in new[] { ("Render", false), ("Capture", true) })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{branch}");
                if (key is null) continue;

                foreach (var id in key.GetSubKeyNames())
                {
                    try
                    {
                        using var device = key.OpenSubKey(id);
                        if (device is null) continue;

                        var state = device.GetValue("DeviceState") as int? ?? 0;
                        var name = ReadEndpointName(device) ?? id;

                        AudioEndpoints.Add(new AudioEndpoint { Name = name, IsCapture = isCapture, StateCode = state });
                    }
                    catch { /* einzelnes Geraet nicht lesbar -> ueberspringen */ }
                }
            }
            catch (Exception ex) { Log.Warn($"Audiogeraete ({branch}): {ex.Message}"); }
        }
    }

    /// <summary>Anzeigename eines Audiogeraets aus den Property-Keys der Registrierung.</summary>
    private static string? ReadEndpointName(RegistryKey device)
    {
        using var props = device.OpenSubKey("Properties");
        if (props is null) return null;

        // Reihenfolge: vollstaendiger Anzeigename, Geraetebeschreibung, Schnittstellenname.
        foreach (var value in new[]
                 {
                     "{a45c254e-df1c-4efd-8020-67d146a850e0},14",
                     "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
                     "{b3f8fa53-0004-438e-9003-51a46e139bfc},6",
                 })
        {
            if (props.GetValue(value) is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    private void CollectNetwork()
    {
        var configs = Wmi.Query(
            "SELECT InterfaceIndex, IPAddress, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled, IPEnabled " +
            "FROM Win32_NetworkAdapterConfiguration");

        foreach (var row in Wmi.Query(
            "SELECT Name, NetConnectionID, NetConnectionStatus, NetEnabled, MACAddress, Speed, InterfaceIndex " +
            "FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
        {
            var name = row.Str("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var index = row.Int("InterfaceIndex");
            var config = configs.FirstOrDefault(c => c.Int("InterfaceIndex") == index);

            var adapter = new NetAdapter
            {
                Name = name,
                ConnectionName = row.Str("NetConnectionID"),
                Enabled = row.Bool("NetEnabled") ?? false,
                ConnectionStatus = row.Int("NetConnectionStatus") ?? -1,
                MacAddress = row.Str("MACAddress"),
                SpeedBitsPerSecond = row.Long("Speed") ?? 0,
                Gateway = config is null ? "" : string.Join(", ", StringArray(config, "DefaultIPGateway")),
                DhcpEnabled = config?.Bool("DHCPEnabled") ?? false,
            };

            if (config is not null)
            {
                adapter.IpAddresses.AddRange(StringArray(config, "IPAddress"));
                adapter.DnsServers.AddRange(StringArray(config, "DNSServerSearchOrder"));
            }

            NetworkAdapters.Add(adapter);
        }
    }

    private static IEnumerable<string> StringArray(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is not string[] array) return Array.Empty<string>();
        return array.Where(s => !string.IsNullOrWhiteSpace(s));
    }

    private void CollectProblemDevices()
    {
        foreach (var row in Wmi.Query(
            "SELECT Name, PNPClass, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
            "WHERE ConfigManagerErrorCode <> 0"))
        {
            var code = row.Int("ConfigManagerErrorCode") ?? 0;
            if (code == 0) continue;

            ProblemDevices.Add(new ProblemDevice
            {
                Name = row.Str("Name"),
                DeviceClass = row.Str("PNPClass"),
                DeviceId = row.Str("DeviceID"),
                ErrorCode = code,
            });
        }
    }

    /// <summary>Dienste, ohne die Ton, Netzwerk oder Geraeteerkennung nicht funktionieren.</summary>
    internal static readonly (string Name, string Purpose)[] WatchedServices =
    {
        ("Audiosrv", "Windows-Audio"),
        ("AudioEndpointBuilder", "Audio-Geraeteverwaltung"),
        ("Dhcp", "DHCP-Client (IP-Adresse beziehen)"),
        ("Dnscache", "DNS-Client (Namensaufloesung)"),
        ("WlanSvc", "WLAN-Dienst"),
        ("nlasvc", "Netzwerkstandort-Erkennung"),
        ("NlaSvc", "Netzwerkstandort-Erkennung"),
        ("PlugPlay", "Geraeteerkennung"),
        ("DeviceAssociationService", "Geraetekopplung"),
        ("FrameServer", "Kamera-Bildverarbeitung"),
    };

    private void CollectServices()
    {
        var names = WatchedServices.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        var filter = string.Join(" OR ", names.Select(n => $"Name='{n}'"));

        foreach (var row in Wmi.Query($"SELECT Name, State, StartMode FROM Win32_Service WHERE {filter}"))
        {
            var name = row.Str("Name");
            if (!string.IsNullOrWhiteSpace(name))
                Services[name] = (row.Str("State"), row.Str("StartMode"));
        }
    }

    private void CollectPrivacy()
    {
        MicrophoneConsent = ReadConsent("microphone", "Mikrofon");
        CameraConsent = ReadConsent("webcam", "Kamera");
    }

    /// <summary>
    /// Liest den Windows-Datenschutz fuer eine Geraeteklasse. Der Schalter
    /// "Desktop-Apps duerfen zugreifen" liegt im Unterschluessel "NonPackaged" -
    /// er ist der Grund, warum ein Mikrofon in Discord fehlt, in den
    /// Windows-Einstellungen aber einwandfrei aussieht.
    /// </summary>
    private static PrivacyConsent? ReadConsent(string capability, string label)
    {
        const string consentRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
        const string policyRoot = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{consentRoot}\{capability}");
            if (key is null) return null;

            string? desktop = null;
            using (var nonPackaged = key.OpenSubKey("NonPackaged"))
                desktop = nonPackaged?.GetValue("Value") as string;

            string? policy = null;
            try
            {
                using var pol = Registry.LocalMachine.OpenSubKey(policyRoot);
                var name = capability == "webcam" ? "LetAppsAccessCamera" : "LetAppsAccessMicrophone";
                // 0 = Benutzer entscheidet, 1 = erzwungen erlaubt, 2 = erzwungen verweigert
                if (pol?.GetValue(name) is int v) policy = v == 2 ? "Deny" : v == 1 ? "Allow" : null;
            }
            catch { /* keine Richtlinie gesetzt */ }

            var consent = new PrivacyConsent
            {
                Kind = label,
                UserValue = key.GetValue("Value") as string,
                DesktopAppsValue = desktop,
                PolicyValue = policy,
            };

            // Einzelne Anwendungen, denen der Zugriff verweigert wurde.
            CollectDeniedApps(key, consent.DeniedApps);
            using (var nonPackaged = key.OpenSubKey("NonPackaged"))
                if (nonPackaged is not null) CollectDeniedApps(nonPackaged, consent.DeniedApps);

            return consent;
        }
        catch (Exception ex)
        {
            Log.Warn($"Datenschutzeinstellungen ({capability}): {ex.Message}");
            return null;
        }
    }

    /// <summary>Sammelt alle Anwendungen unterhalb eines Zustimmungsschluessels, die auf "Deny" stehen.</summary>
    private static void CollectDeniedApps(RegistryKey root, List<string> target)
    {
        foreach (var appKeyName in root.GetSubKeyNames())
        {
            if (appKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var appKey = root.OpenSubKey(appKeyName);
                if (appKey?.GetValue("Value") as string == "Deny")
                    target.Add(FriendlyAppName(appKeyName));
            }
            catch { /* einzelner Eintrag nicht lesbar */ }
        }
    }

    /// <summary>
    /// Desktop-Programme stehen mit ihrem Pfad im Schluessel, wobei "#" als
    /// Trennzeichen dient (C:#Program Files#Discord#Discord.exe).
    /// </summary>
    private static string FriendlyAppName(string keyName)
    {
        if (!keyName.Contains('#')) return keyName;
        var path = keyName.Replace('#', '\\');
        try { return $"{Path.GetFileName(path)} ({path})"; }
        catch { return path; }
    }

    /// <summary>Kurzfassung fuer Kopfzeilen und Berichte.</summary>
    public string OneLine =>
        $"{CpuName} | {PrimaryGpuName} | {TotalMemoryGb:0.#} GB RAM | {OsCaption} {OsDisplayVersion} (Build {OsBuild})";

    public string FormatBiosAge()
    {
        if (BiosDate is null) return "unbekannt";
        var days = (int)(DateTime.Now - BiosDate.Value).TotalDays;
        return $"{BiosDate.Value.ToString("d", CultureInfo.CurrentCulture)} ({days} Tage alt)";
    }
}
