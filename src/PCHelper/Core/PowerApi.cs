using System.Runtime.InteropServices;
using System.Text;

namespace PCHelper.Core;

/// <summary>
/// Lesezugriff auf die Windows-Energieoptionen ueber powrprof.dll.
/// Bewusst nicht ueber "powercfg /q": die API ist sprachunabhaengig und
/// damit auf deutschen wie englischen Systemen zuverlaessig.
/// </summary>
public static class PowerApi
{
    // --- Bekannte GUIDs der Energieoptionen ---
    public static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdleTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    public static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid StandbyIdleTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    public static readonly Guid HibernateIdleTimeout = new("9d7815a6-7ee4-497e-8888-515a05f02364");

    public static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    public static readonly Guid PciExpressAspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

    public static readonly Guid SubDisk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid DiskIdleTimeout = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");

    public static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid ProcessorMinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid ProcessorMaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey,
        ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey,
        ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid,
        StringBuilder? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>GUID des aktiven Energiesparplans, oder null bei Fehler.</summary>
    public static Guid? GetActiveSchemeGuid()
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out ptr) != 0 || ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        catch (Exception ex)
        {
            Log.Error("Aktiver Energiesparplan konnte nicht gelesen werden", ex);
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) LocalFree(ptr);
        }
    }

    /// <summary>Anzeigename des aktiven Energiesparplans.</summary>
    public static string GetActiveSchemeName()
    {
        var guid = GetActiveSchemeGuid();
        if (guid is null) return "unbekannt";

        try
        {
            var g = guid.Value;
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
            if (size == 0) return guid.Value.ToString();

            var sb = new StringBuilder((int)size / 2 + 1);
            if (PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, sb, ref size) != 0)
                return guid.Value.ToString();

            return sb.ToString();
        }
        catch { return guid.Value.ToString(); }
    }

    /// <summary>
    /// Liest den Netzbetrieb-Wert einer Energieeinstellung des aktiven Plans.
    /// Zeitwerte sind in Sekunden, 0 bedeutet "nie".
    /// </summary>
    public static uint? ReadAcValue(Guid subGroup, Guid setting)
    {
        var scheme = GetActiveSchemeGuid();
        if (scheme is null) return null;

        try
        {
            var s = scheme.Value;
            var sub = subGroup;
            var set = setting;
            if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref set, out uint value) != 0) return null;
            return value;
        }
        catch { return null; }
    }

    /// <summary>Formatiert einen Zeitwert in Sekunden als lesbaren Text.</summary>
    public static string FormatTimeout(uint? seconds) => seconds switch
    {
        null => "unbekannt",
        0 => "nie",
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h {(seconds % 3600) / 60} min"
    };
}
