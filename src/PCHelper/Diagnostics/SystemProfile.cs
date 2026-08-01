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
