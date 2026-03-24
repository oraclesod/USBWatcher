using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using USBWatcher.Common;

namespace USBWatcherInstall
{
    internal sealed class Installer
    {
        private const string SharedEventSource = "USBWatcher";
        private const string ComponentName = "Install";

        private const string HklmRunValueName = "USBWatcher-Agent-Init";
        private const string HkcuRunValueName = "USBWatcher-Agent";

        private const string StartMenuFolderName = "USB Watcher";
        private const string AgentShortcutName = "USB Watcher - Agent";
        private const string AgentAumid = "USBWatcher.Agent";

        private static readonly string InstallDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBWatcher");

        private static readonly string InstallLogPath = Path.Combine(InstallDir, "Install.log");

        private readonly Logger _logger = new(SharedEventSource, InstallLogPath, ComponentName);

        private string SourceDir => AppContext.BaseDirectory;

        private const string AgentExeName = "USBWatcher-Agent.exe";
        private const string SyncExeName = "USBWatcher-Sync.exe";
        private const string InstallExeName = "Install.exe";
        private const string ConfigFileName = "config.json";
        private const string IconIcoFileName = "USBWatcher.ico";
        private const string IconPngFileName = "USBWatcher.png";

        private static readonly TimeSpan UpgradeSyncWaitTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan UpgradeSyncPollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AgentStopWaitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AgentStopPollInterval = TimeSpan.FromSeconds(1);

        public int InstallOrRepair(bool repair)
        {
            Directory.CreateDirectory(InstallDir);
            _logger.Info($"{(repair ? "Repair" : "Install")} started.", EventIds.Install.Started);

            EnsureEventLogSource();

            bool existingInstallDetected = IsInstalled();

            if (existingInstallDetected)
            {
                _logger.Info(
                    $"Existing installation detected. {(repair ? "Repair" : "Install")} will run against existing install.",
                    EventIds.Install.ExistingInstallDetected);

                if (!WaitForSyncToExit(UpgradeSyncWaitTimeout))
                {
                    _logger.Error(
                        $"Operation aborted because Sync did not exit within {UpgradeSyncWaitTimeout.TotalMinutes:0} minutes.",
                        EventIds.Install.SyncTimeout);
                    return 1;
                }

                if (!StopAgentProcesses())
                {
                    _logger.Error(
                        $"Operation aborted because Agent did not exit within {AgentStopWaitTimeout.TotalSeconds:0} seconds.",
                        EventIds.Install.AgentTimeout);
                    return 1;
                }
            }

            CopyPayload(InstallExeName, overwrite: true);
            CopyPayload(AgentExeName, overwrite: true);
            CopyPayload(SyncExeName, overwrite: true);

            if (repair || !existingInstallDetected)
            {
                CopyPayload(ConfigFileName, overwrite: true);
                _logger.Info(
                    existingInstallDetected
                        ? "Repair mode: replaced existing config.json with package config."
                        : "Install mode on new install: copied package config.json.",
                    EventIds.Install.ConfigReplaced);
            }
            else
            {
                MergeInstalledConfigWithPackageConfig();
                _logger.Info(
                    "Install mode on existing install: merged config schema and preserved existing values.",
                    EventIds.Install.ConfigMerged);
            }

            CopyPayloadIfExists(IconIcoFileName, overwrite: true);
            CopyPayloadIfExists(IconPngFileName, overwrite: true);

            var agentPath = Path.Combine(InstallDir, AgentExeName);
            var iconPath = Path.Combine(InstallDir, IconIcoFileName);

            try
            {
                ShortcutHelper.CreateStartMenuShortcutWithAumid(
                    startMenuFolderName: StartMenuFolderName,
                    shortcutName: AgentShortcutName,
                    targetPath: agentPath,
                    arguments: "",
                    workingDirectory: InstallDir,
                    iconPath: File.Exists(iconPath) ? iconPath : "",
                    appUserModelId: AgentAumid
                );

                _logger.Info("Created Start Menu shortcut with AUMID for Agent.", EventIds.Install.ShortcutCreated);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed creating Start Menu shortcut/AUMID: {ex}", EventIds.Install.Warning);
            }

            SetHklmRunInit(agentPath);
            _logger.Info("Set HKLM Run init value.", EventIds.Install.RunKeySet);

            var session = SessionHelper.TryGetActiveSession();
            if (session != null)
            {
                _logger.Info($"Attempting to start Agent init in active session {session.SessionId}...", EventIds.Install.StartAgentInit);

                bool ok = SessionHelper.TryRunAsActiveUser(agentPath, "--init", hidden: true, out var err);
                if (ok)
                {
                    _logger.Info($"Started Agent init in active session {session.SessionId}.", EventIds.Install.StartAgentInit);
                }
                else
                {
                    _logger.Warn(
                        $"Failed to start Agent init in active session {session.SessionId}. {err}",
                        EventIds.Install.Warning);

                    if (TryStartAgentFallback(agentPath, out var fallbackErr))
                    {
                        _logger.Info("Fallback start of Agent succeeded (Process.Start).", EventIds.Install.StartAgentInitFallback);
                    }
                    else
                    {
                        _logger.Warn($"Fallback start of Agent failed: {fallbackErr}", EventIds.Install.Warning);
                        _logger.Info("Agent will start at next user logon via HKLM Run.", EventIds.Install.StartAgentInitFallback);
                    }
                }
            }
            else
            {
                _logger.Warn("No active session detected; init will run at next user logon.", EventIds.Install.Warning);
            }

            _logger.Info($"{(repair ? "Repair" : "Install")} complete.", EventIds.Install.Completed);
            return 0;
        }

        public int UpdateConfigValue(string jsonPath, string rawValue)
        {
            Directory.CreateDirectory(InstallDir);
            _logger.Info($"Update started. Path='{jsonPath}' Value='{rawValue}'", EventIds.Install.Started);

            EnsureEventLogSource();

            var installedConfigPath = Path.Combine(InstallDir, ConfigFileName);
            if (!File.Exists(installedConfigPath))
            {
                _logger.Error($"Update failed: installed config not found at '{installedConfigPath}'.", EventIds.Install.Error);
                return 1;
            }

            JsonNode? root;
            try
            {
                root = LoadJsonFromFile(installedConfigPath);
            }
            catch (Exception ex)
            {
                _logger.Error($"Update failed: unable to read installed config. {ex}", EventIds.Install.Error);
                return 1;
            }

            if (root is not JsonObject rootObject)
            {
                _logger.Error("Update failed: installed config root is not a JSON object.", EventIds.Install.Error);
                return 1;
            }

            if (!TryUpdateExistingPath(rootObject, jsonPath, rawValue, out string? updateError))
            {
                _logger.Error($"Update failed: {updateError}", EventIds.Install.Error);
                return 1;
            }

            SaveJsonToFile(installedConfigPath, rootObject);
            _logger.Info($"Updated config path '{jsonPath}'.", EventIds.Install.ConfigUpdated);
            return 0;
        }

        public int Uninstall()
        {
            Directory.CreateDirectory(InstallDir);
            _logger.Info("Uninstall started.", EventIds.Install.UninstallStarted);

            EnsureEventLogSource();

            RemoveHklmRunInit();

            try
            {
                ShortcutHelper.RemoveStartMenuFolder(StartMenuFolderName);
                _logger.Info($"Removed Start Menu folder: {StartMenuFolderName}", EventIds.Install.UninstallStarted);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed removing Start Menu folder: {ex}", EventIds.Install.Warning);
            }

            StopAgentProcessesBestEffort();

            var session = SessionHelper.TryGetActiveSession();
            if (session != null)
            {
                SessionHelper.TryRunAsActiveUser(
                    Path.Combine(Environment.SystemDirectory, "reg.exe"),
                    $@"DELETE ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""{HkcuRunValueName}"" /f",
                    hidden: true,
                    out _);
            }

            SafeDelete(Path.Combine(InstallDir, InstallExeName));
            SafeDelete(Path.Combine(InstallDir, AgentExeName));
            SafeDelete(Path.Combine(InstallDir, SyncExeName));
            SafeDelete(Path.Combine(InstallDir, ConfigFileName));
            SafeDelete(Path.Combine(InstallDir, IconIcoFileName));
            SafeDelete(Path.Combine(InstallDir, IconPngFileName));

            TryDeleteDirIfEmpty(InstallDir);

            _logger.Info("Uninstall complete.", EventIds.Install.UninstallCompleted);
            return 0;
        }

        private bool IsInstalled()
        {
            return File.Exists(Path.Combine(InstallDir, AgentExeName)) ||
                   File.Exists(Path.Combine(InstallDir, SyncExeName));
        }

        private bool WaitForSyncToExit(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            bool firstLog = true;
            var processName = Path.GetFileNameWithoutExtension(SyncExeName);

            while (sw.Elapsed < timeout)
            {
                Process[] syncProcesses = Array.Empty<Process>();

                try
                {
                    syncProcesses = Process.GetProcessesByName(processName);
                    if (syncProcesses.Length == 0)
                    {
                        _logger.Info("No running Sync process detected.", EventIds.Install.WaitingForSync);
                        return true;
                    }

                    if (firstLog)
                    {
                        var details = string.Join(", ", syncProcesses.Select(p =>
                        {
                            try
                            {
                                return $"PID={p.Id}, Name={p.ProcessName}, Session={p.SessionId}";
                            }
                            catch
                            {
                                return "PID=<unknown>";
                            }
                        }));

                        _logger.Info(
                            $"Sync is currently running ({syncProcesses.Length} instance(s)); waiting up to {timeout.TotalMinutes:0} minutes for it to finish. Found: {details}",
                            EventIds.Install.WaitingForSync);
                        firstLog = false;
                    }
                    else
                    {
                        _logger.Info(
                            $"Sync still running ({syncProcesses.Length} instance(s)); waited {Math.Floor(sw.Elapsed.TotalSeconds):0}s so far...",
                            EventIds.Install.WaitingForSync);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Error while checking for Sync processes: {ex}", EventIds.Install.Warning);
                }
                finally
                {
                    foreach (var p in syncProcesses)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                Thread.Sleep(UpgradeSyncPollInterval);
            }

            _logger.Warn(
                $"Timed out waiting for Sync to finish after {timeout.TotalMinutes:0} minutes.",
                EventIds.Install.SyncTimeout);

            return false;
        }

        private bool StopAgentProcesses()
        {
            var sw = Stopwatch.StartNew();
            var processName = Path.GetFileNameWithoutExtension(AgentExeName);
            bool firstPass = true;

            while (sw.Elapsed < AgentStopWaitTimeout)
            {
                Process[] agents = Array.Empty<Process>();

                try
                {
                    agents = Process.GetProcessesByName(processName);

                    if (agents.Length == 0)
                    {
                        _logger.Info("No Agent processes remain.", EventIds.Install.StoppingAgent);
                        return true;
                    }

                    var details = string.Join(", ", agents.Select(p =>
                    {
                        try
                        {
                            return $"PID={p.Id}, Name={p.ProcessName}, Session={p.SessionId}";
                        }
                        catch
                        {
                            return "PID=<unknown>";
                        }
                    }));

                    _logger.Info(
                        firstPass
                            ? $"Stopping Agent process(es) before operation. Found: {details}"
                            : $"Agent process(es) still present after {Math.Floor(sw.Elapsed.TotalSeconds):0}s. Found: {details}",
                        EventIds.Install.StoppingAgent);

                    foreach (var p in agents)
                    {
                        try
                        {
                            p.Kill(entireProcessTree: false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(
                                $"Failed to kill Agent PID={SafePid(p)} Session={SafeSessionId(p)}: {ex.Message}",
                                EventIds.Install.Warning);
                        }
                    }

                    firstPass = false;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Error while stopping Agent processes: {ex}", EventIds.Install.Warning);
                }
                finally
                {
                    foreach (var p in agents)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                Thread.Sleep(AgentStopPollInterval);
            }

            try
            {
                var remaining = Process.GetProcessesByName(processName);
                var details = string.Join(", ", remaining.Select(p =>
                {
                    try
                    {
                        return $"PID={p.Id}, Name={p.ProcessName}, Session={p.SessionId}";
                    }
                    catch
                    {
                        return "PID=<unknown>";
                    }
                }));

                _logger.Error(
                    $"Agent still appears to be running after waiting {AgentStopWaitTimeout.TotalSeconds:0} seconds. Remaining: {details}",
                    EventIds.Install.AgentTimeout);

                foreach (var p in remaining)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }

            return false;
        }

        private void StopAgentProcessesBestEffort()
        {
            try
            {
                var processName = Path.GetFileNameWithoutExtension(AgentExeName);
                var agents = Process.GetProcessesByName(processName);

                foreach (var p in agents)
                {
                    try
                    {
                        p.Kill(entireProcessTree: false);
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                _logger.Info("Best-effort stop of Agent process(es) completed.", EventIds.Install.StoppingAgent);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Best-effort stop of Agent process(es) failed: {ex}", EventIds.Install.Warning);
            }
        }

        private void MergeInstalledConfigWithPackageConfig()
        {
            var installedConfigPath = Path.Combine(InstallDir, ConfigFileName);
            var packageConfigPath = GetPayloadSourcePath(ConfigFileName);

            if (!File.Exists(packageConfigPath))
                throw new FileNotFoundException($"Package config not found: {packageConfigPath}");

            JsonNode? packageRoot = LoadJsonFromFile(packageConfigPath);

            if (!File.Exists(installedConfigPath))
            {
                _logger.Info("Existing install had no config.json. Copying package config.", EventIds.Install.ConfigMerged);
                File.Copy(packageConfigPath, installedConfigPath, overwrite: true);
                return;
            }

            JsonNode? installedRoot = LoadJsonFromFile(installedConfigPath);

            if (installedRoot is not JsonObject installedObject)
                throw new InvalidOperationException("Installed config root is not a JSON object.");

            if (packageRoot is not JsonObject packageObject)
                throw new InvalidOperationException("Package config root is not a JSON object.");

            SyncObjectSchema(installedObject, packageObject);
            SaveJsonToFile(installedConfigPath, installedObject);
        }

        private void SyncObjectSchema(JsonObject target, JsonObject template)
        {
            var targetKeys = target.Select(kvp => kvp.Key).ToList();
            foreach (var key in targetKeys)
            {
                if (!template.ContainsKey(key))
                    target.Remove(key);
            }

            foreach (var kvp in template)
            {
                var key = kvp.Key;
                var templateValue = kvp.Value;

                if (!target.ContainsKey(key))
                {
                    target[key] = templateValue?.DeepClone();
                    continue;
                }

                var existingValue = target[key];

                if (existingValue is JsonObject existingObj && templateValue is JsonObject templateObj)
                {
                    SyncObjectSchema(existingObj, templateObj);
                }
            }
        }

        private bool TryUpdateExistingPath(JsonObject root, string path, string rawValue, out string? error)
        {
            error = null;

            var segments = path.Split(
                new[] { ':', '.' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                error = "JSON path is empty.";
                return false;
            }

            JsonObject current = root;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];

                if (!current.TryGetPropertyValue(segment, out JsonNode? child) || child is not JsonObject childObject)
                {
                    error = $"Path segment '{segment}' does not exist as an object.";
                    return false;
                }

                current = childObject;
            }

            var finalSegment = segments[^1];

            if (!current.TryGetPropertyValue(finalSegment, out JsonNode? existingValue) || existingValue is null)
            {
                error = $"Path '{path}' does not exist.";
                return false;
            }

            if (!TryCreateReplacementNode(existingValue, rawValue, out JsonNode? replacement, out error))
                return false;

            current[finalSegment] = replacement;
            return true;
        }

        private bool TryCreateReplacementNode(JsonNode existingValue, string rawValue, out JsonNode? replacement, out string? error)
        {
            replacement = null;
            error = null;

            if (existingValue is JsonArray existingArray)
            {
                var items = ParseArrayArgument(rawValue);
                var newArray = new JsonArray();

                Type? arrayElementType = GetArrayElementType(existingArray);

                foreach (var item in items)
                {
                    if (!TryCreateTypedJsonValue(item, arrayElementType, out JsonNode? itemNode, out error))
                        return false;

                    newArray.Add(itemNode);
                }

                replacement = newArray;
                return true;
            }

            if (existingValue is JsonObject)
            {
                error = "Updating entire object nodes is not supported. Update a leaf property instead.";
                return false;
            }

            Type? scalarType = GetJsonScalarType(existingValue);
            return TryCreateTypedJsonValue(rawValue, scalarType, out replacement, out error);
        }

        private Type? GetArrayElementType(JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                    continue;

                var t = GetJsonScalarType(item);
                if (t != null)
                    return t;
            }

            return typeof(string);
        }

        private Type? GetJsonScalarType(JsonNode node)
        {
            if (node is not JsonValue value)
                return null;

            if (value.TryGetValue<string>(out _)) return typeof(string);
            if (value.TryGetValue<bool>(out _)) return typeof(bool);
            if (value.TryGetValue<int>(out _)) return typeof(int);
            if (value.TryGetValue<long>(out _)) return typeof(long);
            if (value.TryGetValue<double>(out _)) return typeof(double);
            if (value.TryGetValue<decimal>(out _)) return typeof(decimal);

            return typeof(string);
        }

        private bool TryCreateTypedJsonValue(string rawValue, Type? targetType, out JsonNode? node, out string? error)
        {
            node = null;
            error = null;

            rawValue = TrimMatchingQuotes(rawValue);

            try
            {
                if (targetType == typeof(bool))
                {
                    if (!bool.TryParse(rawValue, out bool b))
                    {
                        error = $"'{rawValue}' is not a valid boolean.";
                        return false;
                    }

                    node = JsonValue.Create(b);
                    return true;
                }

                if (targetType == typeof(int))
                {
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    {
                        error = $"'{rawValue}' is not a valid integer.";
                        return false;
                    }

                    node = JsonValue.Create(i);
                    return true;
                }

                if (targetType == typeof(long))
                {
                    if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    {
                        error = $"'{rawValue}' is not a valid long integer.";
                        return false;
                    }

                    node = JsonValue.Create(l);
                    return true;
                }

                if (targetType == typeof(double))
                {
                    if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d))
                    {
                        error = $"'{rawValue}' is not a valid number.";
                        return false;
                    }

                    node = JsonValue.Create(d);
                    return true;
                }

                if (targetType == typeof(decimal))
                {
                    if (!decimal.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal m))
                    {
                        error = $"'{rawValue}' is not a valid decimal.";
                        return false;
                    }

                    node = JsonValue.Create(m);
                    return true;
                }

                node = JsonValue.Create(rawValue);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private string[] ParseArrayArgument(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return Array.Empty<string>();

            return rawValue
                .Split(',', StringSplitOptions.TrimEntries)
                .Select(TrimMatchingQuotes)
                .Where(x => x != null)
                .Select(x => x!)
                .ToArray();
        }

        private static string TrimMatchingQuotes(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            value = value.Trim();

            if (value.Length >= 2)
            {
                if ((value[0] == '"' && value[^1] == '"') ||
                    (value[0] == '\'' && value[^1] == '\''))
                {
                    return value.Substring(1, value.Length - 2);
                }
            }

            return value;
        }

        private JsonNode? LoadJsonFromFile(string path)
        {
            var json = File.ReadAllText(path);
            return JsonNode.Parse(json);
        }

        private void SaveJsonToFile(string path, JsonNode node)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            File.WriteAllText(path, node.ToJsonString(options));
        }

        private bool TryStartAgentFallback(string agentPath, out string? error)
        {
            error = null;

            try
            {
                if (!File.Exists(agentPath))
                {
                    error = $"Agent exe not found: {agentPath}";
                    return false;
                }

                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = agentPath,
                    Arguments = "--init",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(agentPath) ?? InstallDir
                });

                if (p == null)
                {
                    error = "Process.Start returned null.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void SetHklmRunInit(string agentPath)
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key.SetValue(HklmRunValueName, $"\"{agentPath}\" --init", RegistryValueKind.String);
        }

        private void RemoveHklmRunInit()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue(HklmRunValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed removing HKLM Run '{HklmRunValueName}': {ex.Message}", EventIds.Install.Warning);
            }
        }

        private void CopyPayload(string fileName, bool overwrite)
        {
            var src = GetPayloadSourcePath(fileName);
            var dst = Path.Combine(InstallDir, fileName);

            if (!File.Exists(src))
                throw new FileNotFoundException($"Required payload not found: {src}");

            _logger.Info($"Copying payload '{fileName}' from '{src}' to '{dst}'.", EventIds.Install.CopyingPayload);
            File.Copy(src, dst, overwrite);
            _logger.Info($"Copied payload '{fileName}'.", EventIds.Install.CopyingPayload);
        }

        private void CopyPayloadIfExists(string fileName, bool overwrite)
        {
            var src = GetPayloadSourcePath(fileName, allowMissing: true);
            if (!File.Exists(src))
            {
                _logger.Info($"Optional payload '{fileName}' not found at '{src}', skipping.", EventIds.Install.CopyingPayload);
                return;
            }

            var dst = Path.Combine(InstallDir, fileName);
            _logger.Info($"Copying optional payload '{fileName}' from '{src}' to '{dst}'.", EventIds.Install.CopyingPayload);
            File.Copy(src, dst, overwrite);
            _logger.Info($"Copied optional payload '{fileName}'.", EventIds.Install.CopyingPayload);
        }

        private string GetPayloadSourcePath(string fileName, bool allowMissing = false)
        {
            if (string.Equals(fileName, InstallExeName, StringComparison.OrdinalIgnoreCase))
            {
                var exePath = Environment.ProcessPath;
                exePath ??= Process.GetCurrentProcess().MainModule?.FileName;

                if (string.IsNullOrWhiteSpace(exePath))
                    throw new FileNotFoundException("Unable to resolve running installer executable path.");

                return exePath;
            }

            var candidate = Path.Combine(SourceDir, fileName);

            if (!allowMissing)
                return candidate;

            return candidate;
        }

        private void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to delete {path}: {ex.Message}", EventIds.Install.Warning);
            }
        }

        private void TryDeleteDirIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }

        private void EnsureEventLogSource()
        {
            try
            {
                if (!EventLog.SourceExists(SharedEventSource))
                    EventLog.CreateEventSource(SharedEventSource, "Application");
            }
            catch
            {
                // Fallback file logging will be used.
            }
        }

        private static int SafePid(Process p)
        {
            try { return p.Id; } catch { return -1; }
        }

        private static int SafeSessionId(Process p)
        {
            try { return p.SessionId; } catch { return -1; }
        }
    }
}