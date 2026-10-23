using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mosslight.Core.Services.Engine;

public sealed record CleanResult(long Bytes, int Files, IReadOnlyList<string> Failed);

public sealed record TrimResult(int Processes, long Bytes);

public static class Maintenance
{
    private static readonly string[] GameJunkDirs =
    {
        "logs",
        "crash-reports",
        "debug",
    };

    private static readonly string[] VersionJunkDirs =
    {
        "logs",
        "crash-reports",
        "crash_assistant",
    };

    public static IEnumerable<string> JunkFolders(string mcDir)
    {
        foreach (var name in GameJunkDirs)
        {
            yield return Path.Combine(mcDir, name);
        }

        var versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            yield break;
        }

        foreach (var versionDir in SafeDirs(versionsDir))
        {
            foreach (var name in VersionJunkDirs)
            {
                yield return Path.Combine(versionDir, name);
            }
        }
    }

    public static long MeasureGameJunk(string mcDir)
    {
        long total = 0;
        foreach (var dir in JunkFolders(mcDir))
        {
            total += FolderSize(dir);
        }

        foreach (var file in TopLevelDumps(mcDir))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    public static CleanResult CleanGameJunk(string mcDir)
    {
        long bytes = 0;
        var files = 0;
        var failed = new List<string>();

        foreach (var dir in JunkFolders(mcDir))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in SafeFiles(dir))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    bytes += length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file);
                }
            }

            foreach (var sub in SafeDirs(dir))
            {
                try
                {
                    Directory.Delete(sub, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(sub);
                }
            }
        }

        foreach (var file in TopLevelDumps(mcDir))
        {
            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                bytes += length;
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(file);
            }
        }

        return new CleanResult(bytes, files, failed);
    }

    private static IEnumerable<string> TopLevelDumps(string mcDir)
    {
        var result = new List<string>();
        try
        {
            result.AddRange(Directory.EnumerateFiles(mcDir, "hs_err_pid*.log", SearchOption.TopDirectoryOnly));
            result.AddRange(Directory.EnumerateFiles(mcDir, "replay_pid*.log", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static long FolderSize(string dir)
    {
        long total = 0;
        foreach (var file in SafeFiles(dir))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    public static TrimResult OptimizeMemory()
    {
        var count = 0;
        long freed = 0;
        var self = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id <= 4 || process.Id == self)
                {
                    continue;
                }
                if (process.HasExited)
                {
                    continue;
                }

                var before = process.WorkingSet64;
                if (!EmptyWorkingSet(process.Handle))
                {
                    continue;
                }

                process.Refresh();
                var after = process.WorkingSet64;
                count++;
                if (before > after)
                {
                    freed += before - after;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or NotSupportedException or UnauthorizedAccessException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return new TrimResult(count, freed);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
