using System.IO;
using System.Text;

namespace PCHelper.Core;

/// <summary>
/// Sehr einfaches, robustes Logging in eine rotierende Textdatei.
/// Darf unter keinen Umstaenden eine Exception nach aussen werfen.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(
                    AppInfo.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging darf den Programmablauf nie stoeren.
        }
    }

    private static void RotateIfNeeded()
    {
        var fi = new FileInfo(AppInfo.LogFile);
        if (!fi.Exists || fi.Length < MaxBytes) return;

        var old = AppInfo.LogFile + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(AppInfo.LogFile, old);
    }
}
