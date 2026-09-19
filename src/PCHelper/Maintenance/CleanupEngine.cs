using System.IO;

namespace PCHelper.Maintenance;

/// <summary>Ein Ordner (oder eine Datei), dessen Inhalt aufgeraeumt werden darf. <see cref="MinAge"/> null = alles.</summary>
public sealed record CleanTarget(string Path, TimeSpan? MinAge = null);

public readonly record struct CleanResult(long Freed, int Deleted, int Blocked)
{
    public static CleanResult operator +(CleanResult a, CleanResult b)
        => new(a.Freed + b.Freed, a.Deleted + b.Deleted, a.Blocked + b.Blocked);
}

/// <summary>
/// Misst und loescht den Inhalt von Ordnern. Bewusst konservativ: Verknuepfungen (Junctions, Symlinks) werden weder
/// betreten noch geloescht, was gerade in Benutzung ist bleibt liegen statt erzwungen zu werden, und der Ordner
/// selbst bleibt erhalten.
/// </summary>
public static class CleanupEngine
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Nur Verknuepfungen auslassen - versteckte und Systemdateien gehoeren zum Inhalt.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Groesse der Dateien, die <see cref="Clean"/> anfassen wuerde. Nicht lesbare Teile zaehlen nicht mit.</summary>
    public static long Measure(CleanTarget target)
    {
        long sum = 0;
        try
        {
            if (File.Exists(target.Path))
                return IsOld(new FileInfo(target.Path), target.MinAge) ? new FileInfo(target.Path).Length : 0;

            if (!Directory.Exists(target.Path)) return 0;

            foreach (var file in new DirectoryInfo(target.Path).EnumerateFiles("*", Recursive))
            {
                try { if (IsOld(file, target.MinAge)) sum += file.Length; }
                catch { /* Datei verschwunden oder gesperrt */ }
            }
        }
        catch { /* kein Zugriff auf den Ordner: zaehlt als nichts */ }
        return sum;
    }

    public static CleanResult Clean(CleanTarget target)
    {
        long freed = 0;
        int deleted = 0, blocked = 0;

        try
        {
            if (File.Exists(target.Path))
            {
                var single = new FileInfo(target.Path);
                if (IsOld(single, target.MinAge) && TryDelete(single, out var len)) { freed += len; deleted++; }
                else if (IsOld(single, target.MinAge)) blocked++;
                return new CleanResult(freed, deleted, blocked);
            }

            if (!Directory.Exists(target.Path)) return default;

            var root = new DirectoryInfo(target.Path);
            foreach (var file in root.EnumerateFiles("*", Recursive))
            {
                if (!IsOld(file, target.MinAge)) continue;
                if (TryDelete(file, out var length)) { freed += length; deleted++; }
                else blocked++;
            }

            // Leere Unterordner von unten nach oben wegraeumen. Der Ordner selbst bleibt.
            foreach (var dir in root.EnumerateDirectories("*", Recursive).OrderByDescending(d => d.FullName.Length))
            {
                try { if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete(); }
                catch { /* in Benutzung: bleibt liegen */ }
            }
        }
        catch { /* kein Zugriff: bleibt liegen */ }

        return new CleanResult(freed, deleted, blocked);
    }

    private static bool IsOld(FileInfo file, TimeSpan? minAge)
        => minAge is null || file.LastWriteTime < DateTime.Now - minAge.Value;

    private static bool TryDelete(FileInfo file, out long length)
    {
        length = 0;
        try
        {
            length = file.Length;
            if (file.IsReadOnly) file.IsReadOnly = false;
            file.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
