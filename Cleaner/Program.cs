using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

internal static class Program
{
    private const string ProductName = "DataLockerWatcher";
    private const string ProductDisplayName = "DataLocker Watcher";

    private static readonly string[] ProcessNamesToKill =
    {
        "DataLockerWatcher-Agent",
        "DataLockerWatcher-Sync",
        "DataLockerWatcher-Install",
        "USBWatcher-Agent",
        "USBWatcher-Sync",
        "USBWatcher-Install"
    };

    private static readonly string[] EventSourcesToDelete =
    {
        "DataLockerWatcher-Agent",
        "DataLockerWatcher-Sync",
        "DataLockerWatcher-Install",
        "USBWatcher-Agent",
        "USBWatcher-Sync",
        "USBWatcher-Install",
        "USBWatcher"

    };

    private static readonly string[] RegistryValueNamesToRemove =
    {
        "DataLockerWatcher-Agent",
        "DataLockerWatcher-Agent-Init",
        "USBWatcher-Agent",
        "USBWatcher-Agent-Init",
        "USBWatcher-Init",
        "USBWatcher"
        
    };

    private static readonly string[] RegistrySubKeysToDelete =
    {
        @"Software\DataLockerWatcher",
        @"Software\USBWatcher",
        @"Software\WOW6432Node\DataLockerWatcher",
        @"Software\WOW6432Node\USBWatcher"
    };

    private static readonly string[] ScheduledTasksToDelete =
    {
        @"DataLockerWatcher-Agent",
        @"DataLockerWatcher",
        @"USBWatcher-Agent",
        @"USBWatcher"
    };

    private static readonly List<string> Summary = new();
    private static readonly List<string> Errors = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log($"Starting {nameof(Cleaner)} cleanup...");
        Log($"Machine: {Environment.MachineName}");
        Log($"User: {Environment.UserDomainName}\\{Environment.UserName}");
        Log($"Elevated: {IsAdministrator()}");

        if (!IsAdministrator())
        {
            Error("This cleaner should be run elevated. Some items will not be removable without admin rights.");
        }

        try
        {
            KillKnownProcesses();
            DeleteScheduledTasks();
            RemoveSystemRegistryArtifacts();
            RemoveCurrentUserRegistryArtifacts();
            RemoveAllUsersRegistryArtifacts();
            DeleteEventSources();
            DeleteKnownSystemPaths();
            DeleteAllUsersFileArtifacts();
            DeleteCommonShortcuts();
        }
        catch (Exception ex)
        {
            Error($"Fatal error during cleanup: {ex}");
        }

        Console.WriteLine();
        Console.WriteLine("==== Cleanup Summary ====");
        foreach (var line in Summary)
            Console.WriteLine(line);

        if (Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("==== Errors / Warnings ====");
            foreach (var line in Errors)
                Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("Cleanup finished.");
        return Errors.Count == 0 ? 0 : 1;
    }

    private static void KillKnownProcesses()
    {
        Log("Stopping running DataLocker/USBWatcher processes...");

        foreach (string processName in ProcessNamesToKill.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        Log($"Killing process: {process.ProcessName} (PID {process.Id})");
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10000);
                    }
                    catch (Exception ex)
                    {
                        Error($"Failed to kill process {processName} PID {process.Id}: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Error($"Failed enumerating processes for {processName}: {ex.Message}");
            }
        }
    }

    private static void DeleteScheduledTasks()
    {
        Log("Removing legacy scheduled tasks if present...");

        foreach (string taskName in ScheduledTasksToDelete)
        {
            RunProcess(
                "schtasks.exe",
                $"/Delete /TN \"{taskName}\" /F",
                $"Delete scheduled task {taskName}",
                allowFailure: true);

            RunProcess(
                "schtasks.exe",
                $"/Delete /TN \"\\{taskName}\" /F",
                $"Delete scheduled task \\{taskName}",
                allowFailure: true);
        }
    }

    private static void RemoveSystemRegistryArtifacts()
    {
        Log("Removing HKLM registry artifacts...");

        foreach (string subKey in RegistrySubKeysToDelete)
        {
            DeleteRegistryTree(Registry.LocalMachine, subKey);
        }

        RemoveRunValues(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run");
        RemoveRunValues(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");

        // Also try 32-bit view explicitly.
        RemoveRunValuesFromView(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run");
        RemoveRunValuesFromView(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run");
        RemoveRunValuesFromView(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        RemoveRunValuesFromView(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
    }

    private static void RemoveCurrentUserRegistryArtifacts()
    {
        Log("Removing HKCU registry artifacts...");

        foreach (string subKey in RegistrySubKeysToDelete)
        {
            DeleteRegistryTree(Registry.CurrentUser, subKey);
        }

        RemoveRunValues(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
        RemoveRunValues(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
    }

    private static void RemoveAllUsersRegistryArtifacts()
    {
        Log("Removing registry artifacts for all user profiles...");

        foreach (var profile in GetUserProfiles())
        {
            string sid = profile.Sid;
            string profilePath = profile.ProfilePath;
            string hiveName = sid;
            bool loadedByUs = false;

            try
            {
                if (!IsHiveLoaded(sid))
                {
                    string ntUserDat = Path.Combine(profilePath, "NTUSER.DAT");
                    if (File.Exists(ntUserDat))
                    {
                        string tempHive = $"DLW_{sid.Replace("-", "_")}";
                        if (LoadUserHive(tempHive, ntUserDat))
                        {
                            hiveName = tempHive;
                            loadedByUs = true;
                        }
                        else
                        {
                            Error($"Could not load hive for SID {sid} ({profilePath}).");
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                using RegistryKey usersRoot = Registry.Users;
                using RegistryKey? userHive = usersRoot.OpenSubKey(hiveName, writable: true);
                if (userHive == null)
                {
                    Error($"Failed to open HKU\\{hiveName}");
                    continue;
                }

                foreach (string subKey in RegistrySubKeysToDelete)
                {
                    DeleteRegistryTree(userHive, subKey);
                }

                RemoveRunValues(userHive, @"Software\Microsoft\Windows\CurrentVersion\Run");
                RemoveRunValues(userHive, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            }
            catch (Exception ex)
            {
                Error($"Failed cleaning registry for SID {sid}: {ex.Message}");
            }
            finally
            {
                if (loadedByUs)
                {
                    UnloadUserHive(hiveName);
                }
            }
        }
    }

    private static void DeleteEventSources()
    {
        Log("Removing event log sources...");

        foreach (string source in EventSourcesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (EventLog.SourceExists(source))
                {
                    Log($"Deleting event source: {source}");
                    EventLog.DeleteEventSource(source);
                }
                else
                {
                    Log($"Event source not present: {source}");
                }
            }
            catch (Exception ex)
            {
                Error($"Failed to delete event source {source}: {ex.Message}");
            }
        }
    }

    private static void DeleteKnownSystemPaths()
    {
        Log("Removing system-wide folders/files...");

        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DataLockerWatcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "DataLockerWatcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DataLockerWatcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "USBWatcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "USB Watcher"),

            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "DataLocker Watcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "USBWatcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "USB Watcher"),

            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "DataLocker Watcher - Agent.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "DataLockerWatcher-Agent.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "USBWatcher-Agent.lnk")
        };

        foreach (var path in paths)
        {
            DeletePath(path);
        }
    }

    private static void DeleteAllUsersFileArtifacts()
    {
        Log("Removing per-user file and log artifacts...");

        foreach (var profile in GetUserProfiles())
        {
            try
            {
                string userRoot = profile.ProfilePath;
                if (!Directory.Exists(userRoot))
                    continue;

                var paths = new[]
                {
                    Path.Combine(userRoot, @"AppData\Local\DataLockerWatcher"),
                    Path.Combine(userRoot, @"AppData\Local\DataLockerWatcher-Agent"),
                    Path.Combine(userRoot, @"AppData\Local\USBWatcher"),
                    Path.Combine(userRoot, @"AppData\Local\USBWatcher-Agent"),

                    Path.Combine(userRoot, @"AppData\Roaming\DataLockerWatcher"),
                    Path.Combine(userRoot, @"AppData\Roaming\USBWatcher"),

                    Path.Combine(userRoot, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\DataLocker Watcher"),
                    Path.Combine(userRoot, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\USBWatcher"),

                    Path.Combine(userRoot, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\DataLocker Watcher - Agent.lnk"),
                    Path.Combine(userRoot, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\DataLockerWatcher-Agent.lnk"),
                    Path.Combine(userRoot, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\USBWatcher-Agent.lnk"),

                    Path.Combine(userRoot, @"Desktop\DataLocker Watcher - Agent.lnk"),
                    Path.Combine(userRoot, @"Desktop\DataLockerWatcher-Agent.lnk"),
                    Path.Combine(userRoot, @"Desktop\USBWatcher-Agent.lnk")
                };

                foreach (string path in paths)
                {
                    DeletePath(path);
                }

                DeleteMatchingFiles(Path.Combine(userRoot, @"AppData\Local"), "DataLockerWatcher*.log");
                DeleteMatchingFiles(Path.Combine(userRoot, @"AppData\Local"), "USBWatcher*.log");
                DeleteMatchingFiles(Path.Combine(userRoot, @"AppData\Roaming"), "DataLockerWatcher*.log");
                DeleteMatchingFiles(Path.Combine(userRoot, @"AppData\Roaming"), "USBWatcher*.log");
            }
            catch (Exception ex)
            {
                Error($"Failed cleaning files for profile {profile.ProfilePath}: {ex.Message}");
            }
        }
    }

    private static void DeleteCommonShortcuts()
    {
        Log("Removing additional common shortcuts...");

        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DataLocker Watcher - Agent.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DataLockerWatcher-Agent.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "USBWatcher-Agent.lnk")
        };

        foreach (var path in paths)
        {
            DeletePath(path);
        }
    }

    private static void DeleteMatchingFiles(string root, string pattern)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            foreach (string file in EnumerateFilesSafe(root, pattern))
            {
                DeletePath(file);
            }
        }
        catch (Exception ex)
        {
            Error($"Failed searching for pattern '{pattern}' under '{root}': {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> directories = Array.Empty<string>();

            try
            {
                files = Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
            }

            foreach (var file in files)
                yield return file;

            try
            {
                directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var dir in directories)
            {
                try
                {
                    var attrs = File.GetAttributes(dir);

                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                        continue;

                    pending.Push(dir);
                }
                catch
                {
                }
            }
        }
    }
    private static void DeletePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (File.Exists(path))
            {
                MakeWritable(path);
                File.Delete(path);
                Log($"Deleted file: {path}");
                return;
            }

            if (Directory.Exists(path))
            {
                ResetAttributesRecursive(path);
                Directory.Delete(path, recursive: true);
                Log($"Deleted directory: {path}");
                return;
            }
        }
        catch (Exception ex)
        {
            Error($"Immediate delete failed for '{path}': {ex.Message}");

            try
            {
                if (File.Exists(path))
                {
                    if (MoveFileEx(path, null, MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT))
                    {
                        Log($"Scheduled file for deletion on reboot: {path}");
                    }
                    else
                    {
                        Error($"Failed to schedule file for delete on reboot: {path} (Win32 {Marshal.GetLastWin32Error()})");
                    }
                }
                else if (Directory.Exists(path))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                                                   .OrderByDescending(x => x.Length))
                    {
                        try
                        {
                            MakeWritable(child);
                            MoveFileEx(child, null, MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT);
                        }
                        catch
                        {
                            // best effort
                        }
                    }

                    if (MoveFileEx(path, null, MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT))
                    {
                        Log($"Scheduled directory for deletion on reboot: {path}");
                    }
                    else
                    {
                        Error($"Failed to schedule directory for delete on reboot: {path} (Win32 {Marshal.GetLastWin32Error()})");
                    }
                }
            }
            catch (Exception ex2)
            {
                Error($"Failed delayed delete handling for '{path}': {ex2.Message}");
            }
        }
    }

    private static void ResetAttributesRecursive(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            MakeWritable(entry);
        }

        MakeWritable(directory);
    }

    private static void MakeWritable(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void DeleteRegistryTree(RegistryKey root, string subKeyPath)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: true);
            if (key == null)
            {
                Log($"Registry key not present: {GetKeyDisplay(root)}\\{subKeyPath}");
                return;
            }

            root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            Log($"Deleted registry key: {GetKeyDisplay(root)}\\{subKeyPath}");
        }
        catch (Exception ex)
        {
            Error($"Failed deleting registry key {GetKeyDisplay(root)}\\{subKeyPath}: {ex.Message}");
        }
    }

    private static void RemoveRunValues(RegistryKey root, string subKeyPath)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: true);
            if (key == null)
                return;

            foreach (string valueName in RegistryValueNamesToRemove)
            {
                try
                {
                    if (key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                    {
                        key.DeleteValue(valueName, throwOnMissingValue: false);
                        Log($"Deleted registry value: {GetKeyDisplay(root)}\\{subKeyPath}\\{valueName}");
                    }
                }
                catch (Exception ex)
                {
                    Error($"Failed deleting registry value {GetKeyDisplay(root)}\\{subKeyPath}\\{valueName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Error($"Failed opening registry key {GetKeyDisplay(root)}\\{subKeyPath}: {ex.Message}");
        }
    }

    private static void RemoveRunValuesFromView(RegistryHive hive, RegistryView view, string subKeyPath)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            RemoveRunValues(baseKey, subKeyPath);
        }
        catch (Exception ex)
        {
            Error($"Failed removing run values in {hive}/{view} {subKeyPath}: {ex.Message}");
        }
    }

    private static bool IsHiveLoaded(string sid)
    {
        try
        {
            using RegistryKey users = Registry.Users;
            return users.GetSubKeyNames().Contains(sid, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LoadUserHive(string hiveName, string ntUserDatPath)
    {
        int exit = RunProcess(
            "reg.exe",
            $"load \"HKU\\{hiveName}\" \"{ntUserDatPath}\"",
            $"Load hive HKU\\{hiveName}",
            allowFailure: true);

        return exit == 0;
    }

    private static void UnloadUserHive(string hiveName)
    {
        RunProcess(
            "reg.exe",
            $"unload \"HKU\\{hiveName}\"",
            $"Unload hive HKU\\{hiveName}",
            allowFailure: true);
    }

    private static IEnumerable<UserProfileInfo> GetUserProfiles()
    {
        var results = new List<UserProfileInfo>();

        try
        {
            using RegistryKey? profileList = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList",
                writable: false);

            if (profileList == null)
                return results;

            foreach (string sid in profileList.GetSubKeyNames())
            {
                if (!LooksLikeUserSid(sid))
                    continue;

                try
                {
                    using RegistryKey? sidKey = profileList.OpenSubKey(sid);
                    string? path = sidKey?.GetValue("ProfileImagePath") as string;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    path = Environment.ExpandEnvironmentVariables(path);

                    if (path.Contains(@"\systemprofile", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains(@"\LocalService", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains(@"\NetworkService", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    results.Add(new UserProfileInfo(sid, path));
                }
                catch (Exception ex)
                {
                    Error($"Failed reading profile for SID {sid}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Error($"Failed enumerating user profiles: {ex.Message}");
        }

        return results.DistinctBy(x => x.Sid);
    }

    private static bool LooksLikeUserSid(string sid)
    {
        return sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);
    }

    private static int RunProcess(string fileName, string arguments, string description, bool allowFailure)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Log($"{description}: {fileName} {arguments}");

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                Log(stdout.Trim());

            if (!string.IsNullOrWhiteSpace(stderr))
                Log(stderr.Trim());

            if (process.ExitCode != 0 && !allowFailure)
            {
                Error($"{description} failed with exit code {process.ExitCode}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            if (!allowFailure)
                Error($"{description} failed: {ex.Message}");
            else
                Log($"{description} skipped/failed: {ex.Message}");

            return -1;
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string GetKeyDisplay(RegistryKey key)
    {
        if (ReferenceEquals(key, Registry.LocalMachine)) return "HKLM";
        if (ReferenceEquals(key, Registry.CurrentUser)) return "HKCU";
        if (ReferenceEquals(key, Registry.Users)) return "HKU";

        return key.Name;
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Summary.Add(line);
        Console.WriteLine(line);
    }

    private static void Error(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING: {message}";
        Errors.Add(line);
        Console.WriteLine(line);
    }

    private sealed record UserProfileInfo(string Sid, string ProfilePath);

    private static class Cleaner
    {
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        MOVEFILE_REPLACE_EXISTING = 0x1,
        MOVEFILE_COPY_ALLOWED = 0x2,
        MOVEFILE_DELAY_UNTIL_REBOOT = 0x4,
        MOVEFILE_WRITE_THROUGH = 0x8,
        MOVEFILE_CREATE_HARDLINK = 0x10,
        MOVEFILE_FAIL_IF_NOT_TRACKABLE = 0x20
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, MoveFileFlags dwFlags);
}

internal static class LinqCompatExtensions
{
    public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var item in source)
        {
            if (seen.Add(keySelector(item)))
                yield return item;
        }
    }
}