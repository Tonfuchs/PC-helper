using System.Runtime.InteropServices;

namespace PCHelper.Core;

/// <summary>Schlanke Win32-Aufrufe fuer Systemwerte und Fensterdarstellung.</summary>
public static class Native
{
    // ---------- Dunkle Titelleiste ----------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Faerbt die Titelleiste dunkel (Windows 10 1809+).</summary>
    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref on, sizeof(int));
    }

    // ---------- CPU-Auslastung (lokalisierungsunabhaengig, ohne PerformanceCounter) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    private static ulong _prevIdle, _prevKernel, _prevUser;

    /// <summary>
    /// CPU-Gesamtauslastung in Prozent seit dem letzten Aufruf.
    /// Der erste Aufruf liefert 0 (es fehlt der Vergleichswert).
    /// </summary>
    public static double GetCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return double.NaN;

        ulong i = ToUInt64(idle), k = ToUInt64(kernel), u = ToUInt64(user);

        if (_prevKernel == 0 && _prevUser == 0)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            return 0;
        }

        ulong dIdle = i - _prevIdle;
        ulong dKernel = k - _prevKernel;
        ulong dUser = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;

        ulong total = dKernel + dUser;              // Kernel enthaelt bereits Idle
        if (total == 0) return 0;

        return Math.Round(100.0 * (total - dIdle) / total, 1);
    }

    // ---------- Arbeitsspeicher ----------

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

    /// <summary>Speicherbelegung: (Auslastung in %, belegt in GB, gesamt in GB).</summary>
    public static (double LoadPercent, double UsedGb, double TotalGb) GetMemoryStatus()
    {
        var m = new MEMORYSTATUSEX();
        if (!GlobalMemoryStatusEx(m)) return (double.NaN, double.NaN, double.NaN);

        const double gb = 1024.0 * 1024 * 1024;
        double total = m.ullTotalPhys / gb;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / gb;
        return (m.dwMemoryLoad, Math.Round(used, 2), Math.Round(total, 2));
    }

    // ---------- Laufzeit ----------

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    /// <summary>Zeit seit dem letzten Windows-Start (ohne Schnellstart-Korrektur).</summary>
    public static TimeSpan GetUptime() => TimeSpan.FromMilliseconds(GetTickCount64());
}
