using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using CommunityToolkit.WinUI.Notifications;
using SharpCompress.Common;
using SharpCompress.Readers;
using USBWatcher.Common;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace USBWatcherSync
{
    internal sealed class Config
    {
        public DeviceConfig Device { get; set; } = new();
        public UnlockConfig Unlock { get; set; } = new();
        public SyncConfig Sync { get; set; } = new();

        internal sealed class DeviceConfig
        {
            public string[] PnpDeviceIdContainsAny { get; set; } = Array.Empty<string>();
        }

        internal sealed class UnlockConfig
        {
            public string? ExeName { get; set; }
            public bool ToastOnDetected { get; set; } = true;
        }

        internal sealed class SyncConfig
        {
            public string Source { get; set; } = "";
            public string TargetFolder { get; set; } = "";
            public int Mode { get; set; } = 1;   // 1 = folder -> robocopy, 2 = encrypted zip -> extract -> robocopy
            public string Key { get; set; } = "";
        }
    }

    internal static class Program
    {
        private const string SharedEventSource = "USBWatcher";
        private const string ComponentName = "Sync";

        // Reuse Agent’s AUMID because the Installer registers a Start Menu shortcut for Agent.
        // Toasts for unpackaged Win32 require the AUMID to exist as a shortcut property.
        private const string ToastAumid = "USBWatcher.Agent";

        private static readonly string FallbackLogPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USBWatcher",
                "Sync.log");

        private static readonly Logger Logger = new(SharedEventSource, FallbackLogPath, ComponentName);

        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "config.json");

        // Exit codes (agent interprets 0=success, non-zero=failure)
        private const int EXIT_SUCCESS = 0;
        private const int EXIT_GENERAL_FAILURE = 1;
        private const int EXIT_DRIVE_NOT_FOUND = 2;
        private const int EXIT_SHARE_ACCESS_FAILED = 3;
        private const int EXIT_CONFIG_ERROR = 4;

        static int Main(string[] args)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackLogPath)!);

            try
            {
                var cfg = LoadConfigOrThrow(ConfigPath);

                string? hintDrive = TryGetArg(args, "--hintDrive"); // "E:"
                LogInfo($"Sync start. user={Environment.UserName} hintDrive={hintDrive ?? "<none>"} mode={cfg.Sync.Mode}");

                string? driveRoot = FindUSBDriveRoot(cfg.Device.PnpDeviceIdContainsAny, hintDrive);
                if (driveRoot == null)
                {
                    ToastWarn("USBWatcher Sync", "Could not detect your unlocked USB drive. Please reconnect and unlock, then try again.");
                    LogError("No unlocked USB drive found. Exiting.");
                    return EXIT_DRIVE_NOT_FOUND;
                }

                if (!CanAccessSyncSource(cfg.Sync, out var accessErr))
                {
                    ToastWarn("USBWatcher Sync", "Cannot access the USBWatcher sync source. Check VPN/connection or contact IT.");
                    LogError($"Source access failed: {accessErr}");
                    return EXIT_SHARE_ACCESS_FAILED;
                }

                string targetFolder = Path.Combine(driveRoot, cfg.Sync.TargetFolder);
                Directory.CreateDirectory(targetFolder);

                ToastInfo("USBWatcher Sync", "File sync has started. Please do not remove your USB drive.");
                LogInfo($"Resolved target folder: '{targetFolder}'");

                int rc = cfg.Sync.Mode switch
                {
                    1 => RunMode1Sync(cfg, targetFolder),
                    2 => RunMode2Sync(cfg, targetFolder),
                    _ => throw new InvalidOperationException($"Unsupported Sync.Mode '{cfg.Sync.Mode}'.")
                };

                if (rc >= 0 && rc <= 7)
                {
                    ToastInfo("USBWatcher Sync", "Sync completed successfully.");
                    LogInfo("Sync completed successfully.");
                    return EXIT_SUCCESS;
                }

                ToastError("USBWatcher Sync", $"Sync failed (robocopy={rc}). Please try again or contact IT.");
                LogError($"Sync failed. Robocopy exit code={rc}.");
                return EXIT_GENERAL_FAILURE;
            }
            catch (FileNotFoundException ex) when (string.Equals(Path.GetFileName(ex.FileName), "config.json", StringComparison.OrdinalIgnoreCase))
            {
                LogError($"Config not found: {ex.FileName}");
                return EXIT_CONFIG_ERROR;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("Invalid config.json", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.StartsWith("Unsupported Sync.Mode", StringComparison.OrdinalIgnoreCase))
            {
                LogError(ex.Message);
                return EXIT_CONFIG_ERROR;
            }
            catch (Exception ex)
            {
                ToastError("USBWatcher Sync", "Sync failed due to an unexpected error. Please contact IT.");
                LogError($"Unhandled exception: {ex}");
                return EXIT_GENERAL_FAILURE;
            }
        }

        private static int RunMode1Sync(Config cfg, string targetFolder)
        {
            string sourceFolder = cfg.Sync.Source;

            LogInfo($"Mode 1 selected. Syncing folder '{sourceFolder}' -> '{targetFolder}'");

            int rc = RunRobocopy(sourceFolder, targetFolder, null, out var rcLogPath);
            LogInfo($"Mode 1 robocopy exit code={rc}. Log={rcLogPath}");

            return rc;
        }

        private static int RunMode2Sync(Config cfg, string targetFolder)
        {
            string zipPath = cfg.Sync.Source;
            string tempExtractFolder = Path.Combine(targetFolder, ".dlw-temp-extract");

            LogInfo($"Mode 2 selected. ZIP source='{zipPath}' tempExtractFolder='{tempExtractFolder}' target='{targetFolder}'");

            EnsureTempFolderIsSafe(targetFolder, tempExtractFolder);

            try
            {
                RecreateEmptyDirectory(tempExtractFolder);

                ToastInfo("USBWatcher Sync", "Encrypted package detected. Extracting files before sync.");
                LogInfo($"Extracting ZIP '{zipPath}' to '{tempExtractFolder}'");

                ExtractEncryptedZip(zipPath, cfg.Sync.Key, tempExtractFolder);

                int rc = RunRobocopy(tempExtractFolder, targetFolder, tempExtractFolder, out var rcLogPath);
                LogInfo($"Mode 2 robocopy exit code={rc}. Log={rcLogPath}");

                return rc;
            }
            finally
            {
                try
                {
                    SafeDeleteDirectory(tempExtractFolder);
                    LogInfo($"Removed temp extract folder '{tempExtractFolder}'");
                }
                catch (Exception ex)
                {
                    LogWarn($"Failed to remove temp extract folder '{tempExtractFolder}': {ex}");
                }
            }
        }

        private static Config LoadConfigOrThrow(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("config.json not found", path);

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<Config>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (cfg == null)
                throw new InvalidOperationException("Invalid config.json: unable to deserialize configuration.");

            if (cfg.Device?.PnpDeviceIdContainsAny == null || cfg.Device.PnpDeviceIdContainsAny.Length == 0)
                throw new InvalidOperationException("Invalid config.json: Device.PnpDeviceIdContainsAny is required.");

            if (cfg.Sync == null)
                throw new InvalidOperationException("Invalid config.json: Sync section is required.");

            if (string.IsNullOrWhiteSpace(cfg.Sync.Source))
                throw new InvalidOperationException("Invalid config.json: Sync.Source is required.");

            if (string.IsNullOrWhiteSpace(cfg.Sync.TargetFolder))
                throw new InvalidOperationException("Invalid config.json: Sync.TargetFolder is required.");

            if (cfg.Sync.Mode != 1 && cfg.Sync.Mode != 2)
                throw new InvalidOperationException("Invalid config.json: Sync.Mode must be 1 or 2.");

            if (cfg.Sync.Mode == 2 && string.IsNullOrWhiteSpace(cfg.Sync.Key))
                throw new InvalidOperationException("Invalid config.json: Sync.Key is required when Sync.Mode = 2.");

            return cfg;
        }

        private static bool CanAccessSyncSource(Config.SyncConfig sync, out string? error)
        {
            error = null;

            try
            {
                switch (sync.Mode)
                {
                    case 1:
                        if (!Directory.Exists(sync.Source))
                            throw new DirectoryNotFoundException($"Source folder not found: {sync.Source}");

                        using (var e = Directory.EnumerateFileSystemEntries(sync.Source).GetEnumerator())
                        {
                            _ = e.MoveNext();
                        }
                        return true;

                    case 2:
                        if (!File.Exists(sync.Source))
                            throw new FileNotFoundException("Source ZIP file not found.", sync.Source);

                        using (var fs = new FileStream(sync.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            return true;
                        }

                    default:
                        error = $"Unsupported mode: {sync.Mode}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ExtractEncryptedZip(string zipPath, string password, string extractFolder)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("ZIP source file not found.", zipPath);

            Directory.CreateDirectory(extractFolder);

            var readerOptions = new ReaderOptions
            {
                Password = password
            };

            using var stream = File.OpenRead(zipPath);
            using var reader = ReaderFactory.OpenReader(stream, readerOptions);

            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory)
                {
                    reader.WriteEntryToDirectory(
                        extractFolder,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            if (!Directory.EnumerateFileSystemEntries(extractFolder).Any())
                throw new InvalidOperationException("ZIP extraction produced no files.");
        }

        private static int RunRobocopy(string source, string target, string? excludeDir, out string logPath)
        {
            string robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            logPath = Path.Combine(Path.GetTempPath(), "USBWatcher-Robocopy.log");

            string args = $"\"{source}\" \"{target}\" /MIR /Z /R:1 /W:5 /NP /LOG:\"{logPath}\"";

            if (!string.IsNullOrWhiteSpace(excludeDir))
            {
                string excludeName = Path.GetFileName(excludeDir.TrimEnd('\\'));
                args += $" /XD \"{excludeName}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = robocopy,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }

        private static void RecreateEmptyDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                LogInfo($"Temp folder already exists. Removing stale folder '{path}'");
                SafeDeleteDirectory(path);
            }

            Directory.CreateDirectory(path);

            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new IOException($"Temp folder is not empty after recreation: {path}");

            LogInfo($"Created clean temp folder '{path}'");
        }

        private static void EnsureTempFolderIsSafe(string targetFolder, string tempExtractFolder)
        {
            string normalizedTarget = NormalizePath(targetFolder);
            string normalizedTemp = NormalizePath(tempExtractFolder);

            if (string.Equals(normalizedTarget, normalizedTemp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Temp extract folder resolves to the same path as target folder: '{targetFolder}'");
            }

            if (!normalizedTemp.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedTemp, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Temp extract folder must be inside the target folder. target='{targetFolder}' temp='{tempExtractFolder}'");
            }
        }

        private static string NormalizePath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string? FindUSBDriveRoot(string[] pnpContainsAny, string? hintDrive)
        {
            if (!string.IsNullOrWhiteSpace(hintDrive))
            {
                string dl = hintDrive.Trim().TrimEnd('\\');
                if (dl.Length == 2 && dl[1] == ':' && Directory.Exists(dl + "\\"))
                {
                    string? pnp = TryGetPnpForDriveLetter(dl);
                    if (!string.IsNullOrWhiteSpace(pnp) && PnpMatches(pnp, pnpContainsAny))
                        return dl + "\\";
                }
            }

            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, InterfaceType FROM Win32_DiskDrive WHERE InterfaceType='USB'");

            foreach (ManagementObject disk in searcher.Get())
            {
                string deviceId = (disk["DeviceID"] as string) ?? "";
                string pnp = (disk["PNPDeviceID"] as string) ?? "";

                if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(pnp))
                    continue;

                if (!PnpMatches(pnp, pnpContainsAny))
                    continue;

                string qPart =
                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{EscapeWmi(deviceId)}'}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition";

                using var partSearcher = new ManagementObjectSearcher(qPart);

                foreach (ManagementObject part in partSearcher.Get())
                {
                    string partId = (part["DeviceID"] as string) ?? "";
                    if (string.IsNullOrWhiteSpace(partId))
                        continue;

                    string qLd =
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmi(partId)}'}} " +
                        "WHERE AssocClass=Win32_LogicalDiskToPartition";

                    using var ldSearcher = new ManagementObjectSearcher(qLd);

                    foreach (ManagementObject ld in ldSearcher.Get())
                    {
                        string? dl = ld["DeviceID"] as string; // "E:"
                        if (!string.IsNullOrWhiteSpace(dl) && Directory.Exists(dl + "\\"))
                            return dl + "\\";
                    }
                }
            }

            return null;
        }

        private static string? TryGetPnpForDriveLetter(string driveLetter)
        {
            try
            {
                string qPart =
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{EscapeWmi(driveLetter)}'}} " +
                    "WHERE AssocClass=Win32_LogicalDiskToPartition";

                using var partSearcher = new ManagementObjectSearcher(qPart);

                foreach (ManagementObject part in partSearcher.Get())
                {
                    string partId = (part["DeviceID"] as string) ?? "";
                    if (string.IsNullOrWhiteSpace(partId))
                        continue;

                    string qDisk =
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmi(partId)}'}} " +
                        "WHERE AssocClass=Win32_DiskDriveToDiskPartition";

                    using var diskSearcher = new ManagementObjectSearcher(qDisk);

                    foreach (ManagementObject disk in diskSearcher.Get())
                    {
                        string pnp = (disk["PNPDeviceID"] as string) ?? "";
                        if (!string.IsNullOrWhiteSpace(pnp))
                            return pnp;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool PnpMatches(string pnp, string[] containsAny)
        {
            foreach (var needle in containsAny)
            {
                if (string.IsNullOrWhiteSpace(needle))
                    continue;

                if (pnp.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            var di = new DirectoryInfo(path);

            foreach (var dir in di.GetDirectories("*", SearchOption.AllDirectories))
            {
                dir.Attributes = FileAttributes.Normal;
            }

            foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
            {
                file.IsReadOnly = false;
                file.Attributes = FileAttributes.Normal;
            }

            di.Attributes = FileAttributes.Normal;
            Directory.Delete(path, true);
        }

        private static string? TryGetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static string EscapeWmi(string s) =>
            s.Replace("\\", "\\\\").Replace("'", "\\'");

        private static void ToastInfo(string title, string body) => TryShowToast(title, body);
        private static void ToastWarn(string title, string body) => TryShowToast(title, body);
        private static void ToastError(string title, string body) => TryShowToast(title, body);

        private static void TryShowToast(string title, string body)
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "USBWatcher.png");
                Uri? iconUri = File.Exists(iconPath) ? new Uri(iconPath) : null;

                var builder = new ToastContentBuilder()
                    .AddText(title)
                    .AddText(body);

                if (iconUri != null)
                {
                    builder.AddAppLogoOverride(iconUri, ToastGenericAppLogoCrop.Circle);
                }

                string xml = builder
                    .GetToastContent()
                    .GetContent();

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var toast = new ToastNotification(doc);
                ToastNotificationManager.CreateToastNotifier(ToastAumid).Show(toast);
            }
            catch (Exception ex)
            {
                LogWarn($"Toast failed: {ex.Message}");
            }
        }

        private static void LogInfo(string msg) => Logger.Info(msg, EventIds.Sync.Started);
        private static void LogWarn(string msg) => Logger.Warn(msg, EventIds.Sync.Warning);
        private static void LogError(string msg) => Logger.Error(msg, EventIds.Sync.Error);
    }
}
