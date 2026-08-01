using System.Runtime.InteropServices;
using System.Text;

namespace PCHelper.Core;

/// <summary>Ein aktiver Bildschirm-Ausgabepfad.</summary>
public sealed class DisplayTarget
{
    public string Name { get; init; } = "Unbekannt";
    public string Connection { get; init; } = "Unbekannt";
    public double RefreshHz { get; init; }
    public string DevicePath { get; init; } = "";

    /// <summary>Rohwert von DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY (fuer Fehlersuche).</summary>
    public uint OutputTechnologyRaw { get; init; }

    /// <summary>True, wenn der Bildschirm ueber DisplayPort angebunden ist.</summary>
    public bool IsDisplayPort => OutputTechnologyRaw is 10 or 11 or 18;

    /// <summary>
    /// True bei einer externen digitalen Anbindung (DVI, HDMI, DisplayPort).
    /// Hinweis: Manche Treiber melden HDMI-Verbindungen als DVI - deshalb wird
    /// fuer Signalprobleme diese breitere Einordnung verwendet.
    /// </summary>
    public bool IsExternalDigital => OutputTechnologyRaw is 4 or 5 or 10 or 11 or 12 or 18;

    public override string ToString()
        => $"{Name} - {Connection}{(RefreshHz > 0 ? $" @ {RefreshHz:0.##} Hz" : "")}";
}

/// <summary>
/// Liest die aktive Anzeigekonfiguration ueber QueryDisplayConfig.
/// Damit laesst sich - anders als mit WMI - der tatsaechliche Anschlusstyp
/// (DisplayPort/HDMI/DVI) und die Bildwiederholrate pro Monitor bestimmen.
/// </summary>
public static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Der Modus-Block wird nur als Puffer benoetigt (64 Byte), nicht ausgewertet.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        private ulong m0, m1, m2, m3, m4, m5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    /// <summary>Liefert alle aktiven Bildschirmausgaenge. Bei Fehlern eine leere Liste.</summary>
    public static IReadOnlyList<DisplayTarget> GetActiveTargets()
    {
        var result = new List<DisplayTarget>();
        try
        {
            // Bis zu drei Versuche: zwischen Groessenabfrage und Abruf kann sich
            // die Topologie aendern (genau das passiert beim Schwarzbild!).
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                    return result;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                int status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == ERROR_INSUFFICIENT_BUFFER) continue;
                if (status != ERROR_SUCCESS) return result;

                for (int i = 0; i < pathCount; i++)
                {
                    var t = paths[i].targetInfo;

                    var query = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = t.adapterId,
                            id = t.id
                        }
                    };

                    string name = "Bildschirm";
                    string devicePath = "";
                    if (DisplayConfigGetDeviceInfo(ref query) == ERROR_SUCCESS)
                    {
                        if (!string.IsNullOrWhiteSpace(query.monitorFriendlyDeviceName))
                            name = query.monitorFriendlyDeviceName;
                        devicePath = query.monitorDevicePath ?? "";
                    }

                    double hz = t.refreshRate.Denominator > 0
                        ? Math.Round((double)t.refreshRate.Numerator / t.refreshRate.Denominator, 2)
                        : 0;

                    result.Add(new DisplayTarget
                    {
                        Name = name,
                        Connection = OutputTechnologyName(t.outputTechnology),
                        RefreshHz = hz,
                        DevicePath = devicePath,
                        OutputTechnologyRaw = t.outputTechnology,
                    });
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Anzeigekonfiguration konnte nicht gelesen werden", ex);
        }
        return result;
    }

    /// <summary>
    /// Kompakte Signatur der aktuellen Anzeigekonfiguration. Aendert sie sich,
    /// hat sich die Monitoranbindung geaendert (z. B. Signalverlust).
    /// </summary>
    public static string GetSignature()
    {
        var targets = GetActiveTargets();
        if (targets.Count == 0) return "keine";
        var sb = new StringBuilder();
        foreach (var t in targets.OrderBy(t => t.DevicePath, StringComparer.Ordinal))
            sb.Append(t.Name).Append('|').Append(t.Connection).Append('|').Append(t.RefreshHz.ToString("0.##")).Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Zuordnung gemaess DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.
    /// Achtung: OTHER ist -1 (0xFFFFFFFF), HD15 (VGA) ist 0.
    /// </summary>
    private static string OutputTechnologyName(uint tech) => tech switch
    {
        0xFFFFFFFF => "Sonstige",
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-JPN",
        9 => "SDI",
        10 => "DisplayPort (extern)",
        11 => "DisplayPort (intern)",
        12 => "UDI (extern)",
        13 => "UDI (intern)",
        14 => "SDTV-Adapter",
        15 => "Miracast",
        16 => "Kabelgebunden (indirekt)",
        17 => "Virtuell (indirekt)",
        18 => "DisplayPort ueber USB-C",
        0x80000000 => "Intern (Notebook)",
        _ => $"Unbekannt ({tech})"
    };
}
