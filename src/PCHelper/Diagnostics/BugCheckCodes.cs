using System.Globalization;
using System.Text.RegularExpressions;

namespace PCHelper.Diagnostics;

/// <summary>Aufgeschluesselter Bluescreen-Stoppcode.</summary>
public sealed record BugCheckInfo(
    uint Code,
    string Name,
    string Meaning,
    string Advice,
    IReadOnlyDictionary<Cause, double> Causes)
{
    public string CodeText => "0x" + Code.ToString("X8");
    public string Display => $"{CodeText}  {Name}";
    public bool IsKnown => Name != "Unbekannter Stoppcode";
}

/// <summary>
/// Uebersetzt Windows-Stoppcodes in Klartext und ordnet ihnen Ursachenbereiche zu.
///
/// Der Stoppcode ist bei Bluescreens die mit Abstand wertvollste Information -
/// er sagt, in welchem Teil des Systems der Fehler auftrat.
/// </summary>
public static class BugCheckCodes
{
    private static readonly Dictionary<Cause, double> Memory = new() { [Cause.Memory] = 0.8, [Cause.Bios] = 0.2 };
    private static readonly Dictionary<Cause, double> MemoryOrDriver = new() { [Cause.Memory] = 0.5, [Cause.Software] = 0.4 };
    private static readonly Dictionary<Cause, double> Driver = new() { [Cause.Software] = 0.7, [Cause.OperatingSystem] = 0.2 };
    private static readonly Dictionary<Cause, double> Graphics = new() { [Cause.GpuDriver] = 0.8, [Cause.DisplayLink] = 0.2 };
    private static readonly Dictionary<Cause, double> Hardware = new() { [Cause.Memory] = 0.4, [Cause.Bios] = 0.3, [Cause.PowerSupply] = 0.2, [Cause.Thermal] = 0.2 };
    private static readonly Dictionary<Cause, double> Storage = new() { [Cause.Storage] = 0.7 };
    private static readonly Dictionary<Cause, double> System = new() { [Cause.OperatingSystem] = 0.6, [Cause.Storage] = 0.2 };

    private const string AdviceMemory =
        "Deutet stark auf den Arbeitsspeicher hin. Erster Schritt: EXPO/XMP im BIOS deaktivieren und beobachten. " +
        "Bleiben die Abstuerze, MemTest86 ueber mehrere Durchlaeufe laufen lassen.";

    private const string AdviceDriver =
        "Deutet auf einen Treiber hin. Der in der Meldung genannte Dateiname (z. B. nvlddmkm.sys) benennt den " +
        "Verursacher. Betroffenen Treiber sauber neu installieren.";

    private const string AdviceGraphics =
        "Grafikkarte oder Grafiktreiber. Treiber mit DDU vollstaendig entfernen und neu installieren, " +
        "jegliche GPU-Uebertaktung zuruecknehmen, Overlay-Software beenden.";

    private const string AdviceHardware =
        "Die Hardware selbst meldet einen Fehler. Fast immer: Uebertaktung (EXPO, Curve Optimizer, PBO) " +
        "vollstaendig zuruecksetzen. Bleibt es bestehen, ist von einem Defekt auszugehen - bei einem neuen " +
        "Rechner ist das ein Garantiefall.";

    private static readonly Dictionary<uint, BugCheckInfo> Known = new BugCheckInfo[]
    {
        new(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL", "Ein Treiber hat auf einen ungueltigen Speicherbereich zugegriffen.", AdviceMemory, MemoryOrDriver),
        new(0x00000012, "TRAP_CAUSE_UNKNOWN", "Unbekannte Ausnahme - haeufig instabile Hardware.", AdviceHardware, Hardware),
        new(0x00000019, "BAD_POOL_HEADER", "Die Speicherverwaltung hat beschaedigte Strukturen gefunden.", AdviceMemory, MemoryOrDriver),
        new(0x0000001A, "MEMORY_MANAGEMENT", "Fehler in der Speicherverwaltung.", AdviceMemory, Memory),
        new(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED", "Ein Kernel-Programmteil hat eine Ausnahme nicht behandelt.", AdviceDriver, MemoryOrDriver),
        new(0x00000024, "NTFS_FILE_SYSTEM", "Fehler im Dateisystem des Datentraegers.", "Datentraeger mit chkdsk pruefen und SMART-Werte ansehen.", Storage),
        new(0x0000003B, "SYSTEM_SERVICE_EXCEPTION", "Ausnahme beim Wechsel vom Programm in den Kernel.", AdviceDriver, Driver),
        new(0x0000004E, "PFN_LIST_CORRUPT", "Die Verwaltungsliste der Speicherseiten ist beschaedigt.", AdviceMemory, Memory),
        new(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA", "Zugriff auf nicht vorhandenen Speicher.", AdviceMemory, Memory),
        new(0x0000007A, "KERNEL_DATA_INPAGE_ERROR", "Daten konnten nicht vom Datentraeger gelesen werden.", "Datentraeger und dessen Kabel bzw. M.2-Sitz pruefen, SMART-Werte ansehen.", Storage),
        new(0x0000007B, "INACCESSIBLE_BOOT_DEVICE", "Auf den Systemdatentraeger konnte beim Start nicht zugegriffen werden.", "Speichertreiber und BIOS-Einstellungen zum Datentraeger pruefen.", Storage),
        new(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Ein Systemthread hat eine Ausnahme ausgeloest.", AdviceDriver, Driver),
        new(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP", "Unerwarteter Kernel-Trap, oft bei uebertakteter Hardware.", AdviceHardware, Hardware),
        new(0x00000080, "NMI_HARDWARE_FAILURE", "Die Hardware hat einen nicht maskierbaren Interrupt gemeldet.", AdviceHardware, Hardware),
        new(0x0000009C, "MACHINE_CHECK_EXCEPTION", "Der Prozessor meldet einen internen Hardwarefehler.", AdviceHardware, Hardware),
        new(0x0000009F, "DRIVER_POWER_STATE_FAILURE", "Ein Treiber hat einen Energiezustandswechsel blockiert.", "Tritt typischerweise beim Einschlafen oder Aufwachen auf. Grafik-, Netzwerk- und Chipsatztreiber pruefen.", Driver),
        new(0x000000A0, "INTERNAL_POWER_ERROR", "Fehler in der Energieverwaltung.", "Schnellstart und Ruhezustand testweise deaktivieren.", new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.6, [Cause.Software] = 0.3 }),
        new(0x000000BE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Ein Treiber wollte in schreibgeschuetzten Speicher schreiben.", AdviceDriver, Driver),
        new(0x000000C2, "BAD_POOL_CALLER", "Ein Treiber hat Speicher falsch angefordert oder freigegeben.", AdviceDriver, Driver),
        new(0x000000C5, "DRIVER_CORRUPTED_EXPOOL", "Ein Treiber hat den Systemspeicher beschaedigt.", AdviceDriver, MemoryOrDriver),
        new(0x000000D1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Ein Treiber hat auf ungueltigen Speicher zugegriffen.", AdviceDriver, Driver),
        new(0x000000EA, "THREAD_STUCK_IN_DEVICE_DRIVER", "Der Grafiktreiber hat sich festgefahren.", AdviceGraphics, Graphics),
        new(0x000000EF, "CRITICAL_PROCESS_DIED", "Ein lebenswichtiger Windows-Prozess wurde beendet.", "Systemdateien pruefen (DISM und sfc). Haeufig Folge beschaedigter Systemdateien.", System),
        new(0x000000F4, "CRITICAL_OBJECT_TERMINATION", "Ein lebenswichtiger Systemprozess wurde unerwartet beendet.", "Systemdateien und Datentraeger pruefen.", System),
        new(0x000000F7, "DRIVER_OVERRAN_STACK_BUFFER", "Ein Treiber hat seinen Speicherbereich ueberschrieben.", AdviceDriver, Driver),
        new(0x000000FC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Ausfuehrung aus nicht ausfuehrbarem Speicher.", AdviceDriver, MemoryOrDriver),
        new(0x00000101, "CLOCK_WATCHDOG_TIMEOUT", "Ein Prozessorkern hat nicht mehr geantwortet.", AdviceHardware, Hardware),
        new(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION", "Kritische Kernel-Strukturen wurden veraendert.", AdviceHardware, Hardware),
        new(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "Fehler in der Speicherverwaltung der Grafikkarte.", AdviceGraphics, Graphics),
        new(0x00000113, "VIDEO_DXGKRNL_FATAL_ERROR", "Schwerwiegender Fehler im Grafik-Subsystem.", AdviceGraphics, Graphics),
        new(0x00000116, "VIDEO_TDR_FAILURE", "Der Grafiktreiber liess sich nach einem Haenger nicht zuruecksetzen.", AdviceGraphics, Graphics),
        new(0x00000117, "VIDEO_TDR_TIMEOUT_DETECTED", "Der Grafiktreiber hat nicht rechtzeitig geantwortet.", AdviceGraphics, Graphics),
        new(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "Fehler im Ablaufplaner der Grafikkarte.", AdviceGraphics, Graphics),
        new(0x00000124, "WHEA_UNCORRECTABLE_ERROR", "Die Hardware meldet einen nicht korrigierbaren Fehler.", AdviceHardware, Hardware),
        new(0x00000133, "DPC_WATCHDOG_VIOLATION", "Ein Treiber hat den Prozessor zu lange belegt.", "Haeufig Speicher- oder Chipsatztreiber. Chipsatztreiber und SSD-Firmware aktualisieren.", new Dictionary<Cause, double> { [Cause.Software] = 0.5, [Cause.Storage] = 0.3, [Cause.Bios] = 0.2 }),
        new(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE", "Eine Sicherheitspruefung im Kernel ist fehlgeschlagen.", AdviceDriver, MemoryOrDriver),
        new(0x00000144, "BUGCODE_USB3_DRIVER", "Fehler im USB-3-Treiber.", "USB-Geraete einzeln abziehen, um den Verursacher zu finden. Chipsatztreiber aktualisieren.", Driver),
        new(0x0000014F, "PDC_WATCHDOG_TIMEOUT", "Eine Komponente hat den Energiezustandswechsel blockiert.", "Schnellstart und Energiesparmodus testweise deaktivieren.", new Dictionary<Cause, double> { [Cause.PowerSettings] = 0.5, [Cause.Software] = 0.3 }),
        new(0x00000154, "UNEXPECTED_STORE_EXCEPTION", "Fehler beim komprimierten Speicher.", AdviceMemory, MemoryOrDriver),
        new(0x000001CA, "SYNTHETIC_WATCHDOG_TIMEOUT", "Eine Komponente hat nicht rechtzeitig geantwortet.", AdviceHardware, Hardware),
    }.ToDictionary(b => b.Code);

    /// <summary>Beschreibt einen Stoppcode; unbekannte Codes werden generisch beschrieben.</summary>
    public static BugCheckInfo Describe(uint code)
    {
        if (Known.TryGetValue(code, out var info)) return info;

        // Varianten wie 0x1000007E entsprechen dem Basiscode.
        var basic = code & 0x0000FFFF;
        if (code != basic && Known.TryGetValue(basic, out var variant))
            return variant with { Code = code };

        return new BugCheckInfo(
            code,
            "Unbekannter Stoppcode",
            "Zu diesem Code liegt keine Beschreibung vor.",
            "Den Code zusammen mit der Hardware im Netz nachschlagen oder das Absturzabbild mit WhoCrashed auswerten.",
            new Dictionary<Cause, double> { [Cause.OperatingSystem] = 0.3 });
    }

    // Genau acht Hexziffern. Der Blick nach vorn verhindert, dass die Regex in
    // einen der 16-stelligen Bugcheck-Parameter hineinlaeuft.
    private static readonly Regex HexCode =
        new(@"0x([0-9a-fA-F]{8})(?![0-9a-fA-F])", RegexOptions.Compiled);

    /// <summary>
    /// Sucht den Stoppcode in einem Meldungstext. Bei Ereignis 1001 steht er
    /// als erster achtstelliger Hexwert ("Der Fehlercode war: 0x0000001a (...)").
    /// </summary>
    public static uint? TryParseFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = HexCode.Match(message);
        if (!match.Success) return null;
        return uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    /// <summary>Wandelt einen Dezimalwert aus den Ereignisdaten (z. B. BugcheckCode) in einen Stoppcode.</summary>
    public static uint? TryParseFromData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;

        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }
}
