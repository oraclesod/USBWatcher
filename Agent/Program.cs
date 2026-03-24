using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Toolkit.Uwp.Notifications;
using USBWatcher.Common;

namespace USBWatcherAgent
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
            public string SourceFolder { get; set; } = "";
            public string TargetFolder { get; set; } = "";
        }
    }

    internal static class Program
    {
        private const string CommonEventSource = "USBWatcher";
        private const string ComponentName = "Agent";
        private const string HkcuRunValueName = "USBWatcher";
        private const string SingleInstanceMutexName = @"Local\USBWatcher-Agent";

        private static readonly string FallbackLogPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USBWatcher",
                "Agent.log");

        private static readonly Logger Logger = new(CommonEventSource, FallbackLogPath, ComponentName);

        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        private static readonly string AgentExePath = Path.Combine(AppContext.BaseDirectory, "USBWatcher-Agent.exe");
        private static readonly string SyncExePath = Path.Combine(AppContext.BaseDirectory, "USBWatcher-Sync.exe");
        private static readonly string ToastLogoPngPath = Path.Combine(AppContext.BaseDirectory, "USBWatcher.png");

        private static Config _cfg = new();

        private static DateTime _lastDetectedToastUtc = DateTime.MinValue;
        private static DateTime _lastSyncLaunchUtc = DateTime.MinValue;

        private static readonly TimeSpan DetectToastCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SyncLaunchCooldown = TimeSpan.FromSeconds(15);

        private static ManagementEventWatcher? _diskInsertWatcher;
        private static ManagementEventWatcher? _volCreateWatcher;
        private static ManagementEventWatcher? _volModifyWatcher;

        private static TrayAppContext? _tray;
        private static UiInvokerForm? _uiInvoker;
        private static System.Windows.Forms.Timer? _stateTimer;

        private static readonly object _syncLock = new();
        private static Process? _syncProcess;

        private static readonly object _stateLock = new();
        private static bool _deviceDetected;
        private static bool _deviceUnlocked;
        private static bool _lastSyncFailed;

        private static Mutex? _singleInstanceMutex;

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [STAThread]
        static void Main()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackLogPath)!);
            }
            catch
            {
                // Best effort only
            }

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && string.Equals(args[1], "--init", StringComparison.OrdinalIgnoreCase))
            {
                RunInit();
                return;
            }

            bool createdNew = false;

            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out createdNew);
                if (!createdNew)
                {
                    LogInfo("Another instance of USBWatcher-Agent is already running in this session. Exiting.");
                    return;
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to create single-instance mutex: {ex}");
                return;
            }

            try
            {
                try
                {
                    _cfg = LoadConfigOrThrow(ConfigPath);
                }
                catch (Exception ex)
                {
                    LogError($"Config load failed: {ex.Message}");
                    _cfg = new Config();
                }

                LogInfo("Agent starting (normal mode).");

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _tray = new TrayAppContext();
                _uiInvoker = new UiInvokerForm();

                _uiInvoker.CreateControl();
                _ = _uiInvoker.Handle;

                ToastNotificationManagerCompat.OnActivated += toastArgs =>
                {
                    try
                    {
                        var targs = ToastArguments.Parse(toastArgs.Argument);
                        if (!targs.TryGetValue("action", out var action))
                            return;

                        if (!string.Equals(action, "unlock", StringComparison.OrdinalIgnoreCase))
                            return;

                        string? drive = targs.Contains("drive") ? targs["drive"] : null;
                        string? exe = targs.Contains("exe") ? targs["exe"] : null;

                        if (string.IsNullOrWhiteSpace(drive) || string.IsNullOrWhiteSpace(exe))
                            return;

                        string exePath = Path.Combine(drive + "\\", exe);
                        LogInfo($"Toast unlock requested. Launching: {exePath}");

                        if (File.Exists(exePath))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                UseShellExecute = true
                            });
                        }
                        else
                        {
                            LogError($"Unlock EXE not found at: {exePath}");
                            TryToast("USB Watcher", "Unlocker was not found on the CD drive. Reinsert the device and try again.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Toast activation handler error: {ex}");
                    }
                };

                StartWatchers();
                StartStatePolling();

                RefreshDeviceStateAndUi_PollOnly();

                Application.Run(_tray);
            }
            catch (Exception ex)
            {
                LogError($"Agent fatal error: {ex}");
            }
            finally
            {
                StopStatePolling();
                StopWatchers();

                try { _uiInvoker?.Close(); } catch { }

                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { _singleInstanceMutex?.Dispose(); } catch { }
                _singleInstanceMutex = null;
            }
        }

        private sealed class UiInvokerForm : Form
        {
            public UiInvokerForm()
            {
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                Opacity = 0;
                Width = 1;
                Height = 1;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-2000, -2000);
                Load += (_, _) => Hide();
            }
        }

        private static void UiInvoke(Action action)
        {
            try
            {
                if (_uiInvoker == null || _uiInvoker.IsDisposed)
                    return;

                if (!_uiInvoker.IsHandleCreated)
                {
                    _uiInvoker.CreateControl();
                    _ = _uiInvoker.Handle;
                }

                if (_uiInvoker.InvokeRequired)
                    _uiInvoker.BeginInvoke(action);
                else
                    action();
            }
            catch
            {
            }
        }

        private static bool IsCurrentSessionActiveForSync()
        {
            try
            {
                int currentSessionId = Process.GetCurrentProcess().SessionId;
                uint activeSessionId = WTSGetActiveConsoleSessionId();

                bool isActive = activeSessionId != 0xFFFFFFFF && currentSessionId == (int)activeSessionId;

                LogInfo($"Active-session check: currentSessionId={currentSessionId}, activeConsoleSessionId={activeSessionId}, isActive={isActive}");
                return isActive;
            }
            catch (Exception ex)
            {
                LogError($"IsCurrentSessionActiveForSync error: {ex}");
                return false;
            }
        }

        private static void SetDeviceState(bool detected, bool unlocked, string reason)
        {
            lock (_stateLock)
            {
                _deviceDetected = detected;
                _deviceUnlocked = unlocked;

                if (!detected || !unlocked)
                    _lastSyncFailed = false;
            }

            LogInfo($"STATE: detected={detected}, unlocked={unlocked} ({reason})");
            PostUiStateUpdate();
        }

        private static void SetSyncFailed(bool failed, string reason)
        {
            lock (_stateLock)
            {
                _lastSyncFailed = failed;
            }

            LogInfo($"STATE: lastSyncFailed={failed} ({reason})");
            PostUiStateUpdate();
        }

        private static bool IsSyncRunning()
        {
            lock (_syncLock)
            {
                return _syncProcess != null && !_syncProcess.HasExited;
            }
        }

        private static void PostUiStateUpdate()
        {
            if (_tray == null)
                return;

            bool detected;
            bool unlocked;
            bool failed;

            lock (_stateLock)
            {
                detected = _deviceDetected;
                unlocked = _deviceUnlocked;
                failed = _lastSyncFailed;
            }

            bool syncing = IsSyncRunning();
            bool syncExePresent = File.Exists(SyncExePath);

            UiInvoke(() =>
            {
                _tray.UpdateState(
                    deviceDetected: detected,
                    deviceUnlocked: unlocked,
                    syncRunning: syncing,
                    lastSyncFailed: failed,
                    syncExePresent: syncExePresent);
            });
        }

        private static void StartStatePolling()
        {
            _stateTimer = new System.Windows.Forms.Timer
            {
                Interval = 2000
            };

            _stateTimer.Tick += (_, _) =>
            {
                try
                {
                    RefreshDeviceStateAndUi_PollOnly();
                }
                catch (Exception ex)
                {
                    LogError($"State poll error: {ex.Message}");
                }
            };

            _stateTimer.Start();
        }

        private static void StopStatePolling()
        {
            try
            {
                if (_stateTimer != null)
                {
                    _stateTimer.Stop();
                    _stateTimer.Dispose();
                    _stateTimer = null;
                }
            }
            catch
            {
            }
        }

        private static void RefreshDeviceStateAndUi_PollOnly()
        {
            bool detected = IsMatchingUsbDiskPresent();
            bool unlocked = detected && IsMatchingUnlockedVolumePresent();

            lock (_stateLock)
            {
                if (!detected)
                {
                    _deviceDetected = false;
                    _deviceUnlocked = false;
                    _lastSyncFailed = false;
                }
                else
                {
                    _deviceDetected = true;
                    _deviceUnlocked = unlocked;
                }
            }

            PostUiStateUpdate();
        }

        private static void StartWatchers()
        {
            string qDiskInsert =
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_DiskDrive'";

            _diskInsertWatcher = new ManagementEventWatcher(new WqlEventQuery(qDiskInsert));
            _diskInsertWatcher.EventArrived += (_, e) => OnUsbDiskInserted(e);
            _diskInsertWatcher.Start();
            LogInfo("Disk insertion watcher started.");

            string qVolCreate =
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_Volume'";

            _volCreateWatcher = new ManagementEventWatcher(new WqlEventQuery(qVolCreate));
            _volCreateWatcher.EventArrived += (_, e) => OnVolumeEvent(e, "Create");
            _volCreateWatcher.Start();
            LogInfo("Volume creation watcher started.");

            string qVolModify =
                "SELECT * FROM __InstanceModificationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_Volume'";

            _volModifyWatcher = new ManagementEventWatcher(new WqlEventQuery(qVolModify));
            _volModifyWatcher.EventArrived += (_, e) => OnVolumeEvent(e, "Modify");
            _volModifyWatcher.Start();
            LogInfo("Volume modification watcher started.");
        }

        private static void StopWatchers()
        {
            try { _diskInsertWatcher?.Stop(); } catch { }
            try { _volCreateWatcher?.Stop(); } catch { }
            try { _volModifyWatcher?.Stop(); } catch { }

            try { _diskInsertWatcher?.Dispose(); } catch { }
            try { _volCreateWatcher?.Dispose(); } catch { }
            try { _volModifyWatcher?.Dispose(); } catch { }

            _diskInsertWatcher = null;
            _volCreateWatcher = null;
            _volModifyWatcher = null;
        }

        private static void OnUsbDiskInserted(EventArrivedEventArgs e)
        {
            try
            {
                var disk = e.NewEvent?["TargetInstance"] as ManagementBaseObject;
                if (disk == null)
                    return;

                string pnp = (disk["PNPDeviceID"] as string) ?? "";
                string model = (disk["Model"] as string) ?? "";
                string deviceId = (disk["DeviceID"] as string) ?? "";

                if (!PnpMatches(pnp, _cfg.Device?.PnpDeviceIdContainsAny))
                    return;

                LogInfo($"USBWatcher device detected (locked phase likely). Model='{model}', DeviceID='{deviceId}', PNP='{pnp}'");

                SetDeviceState(detected: true, unlocked: false, reason: "disk-insert");

                if (!_cfg.Unlock.ToastOnDetected)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastDetectedToastUtc) < DetectToastCooldown)
                    return;

                _lastDetectedToastUtc = now;

                string? exeName = string.IsNullOrWhiteSpace(_cfg.Unlock.ExeName) ? null : _cfg.Unlock.ExeName.Trim();
                if (exeName == null)
                {
                    TryToast("Encrypted USB device detected", "Please unlock the drive to begin sync.");
                    return;
                }

                string? cdfsDrive = FindCdfsDriveContainingExe(exeName);
                if (cdfsDrive == null)
                {
                    TryToast("Encrypted USB device detected", "Please unlock the drive to begin sync.");
                    return;
                }

                var builder = new ToastContentBuilder()
                    .AddText("Encrypted USB device detected")
                    .AddText("Click Unlock to open the unlock utility. Sync will start after unlock.");

                TryAddToastLogo(builder);

                builder
                    .AddButton(new ToastButton()
                        .SetContent("Unlock")
                        .AddArgument("action", "unlock")
                        .AddArgument("drive", cdfsDrive)
                        .AddArgument("exe", exeName))
                    .AddButton(new ToastButton()
                        .SetContent("Dismiss")
                        .AddArgument("action", "dismiss"))
                    .Show();
            }
            catch (Exception ex)
            {
                LogError($"OnUsbDiskInserted error: {ex}");
            }
        }

        private static void OnVolumeEvent(EventArrivedEventArgs e, string kind)
        {
            try
            {
                var vol = e.NewEvent?["TargetInstance"] as ManagementBaseObject;
                if (vol == null)
                    return;

                string? driveLetter = vol["DriveLetter"] as string;
                string? fileSystem = vol["FileSystem"] as string;

                if (string.IsNullOrWhiteSpace(driveLetter) || string.IsNullOrWhiteSpace(fileSystem))
                    return;

                if (string.Equals(fileSystem, "CDFS", StringComparison.OrdinalIgnoreCase))
                {
                    LogInfo($"{kind}: Ignoring CDFS volume event at {driveLetter}.");
                    return;
                }

                string? pnp = TryGetPnpForDriveLetter(driveLetter);
                if (string.IsNullOrWhiteSpace(pnp) || !PnpMatches(pnp, _cfg.Device?.PnpDeviceIdContainsAny))
                    return;

                SetDeviceState(detected: true, unlocked: true, reason: $"{kind}-volume({driveLetter},{fileSystem})");

                if (!IsCurrentSessionActiveForSync())
                {
                    LogInfo($"{kind}: Unlocked volume detected at {driveLetter}, but current session is not active. Skipping sync launch.");
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastSyncLaunchUtc) < SyncLaunchCooldown)
                    return;

                _lastSyncLaunchUtc = now;

                LogInfo($"{kind}: USBWatcher unlocked volume mounted at {driveLetter} ({fileSystem}). Launching Sync.");

                LaunchSyncProcess_TrustedUnlockedEvent(hintDrive: driveLetter, reason: $"{kind}: auto-sync");
            }
            catch (Exception ex)
            {
                LogError($"OnVolumeEvent error: {ex}");
            }
        }

        private static string? TryGetUnlockedDriveLetterForDevice()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DriveLetter, FileSystem FROM Win32_Volume");
                foreach (ManagementObject vol in searcher.Get())
                {
                    string? dl = vol["DriveLetter"] as string;
                    string? fs = vol["FileSystem"] as string;

                    if (string.IsNullOrWhiteSpace(dl) || string.IsNullOrWhiteSpace(fs))
                        continue;

                    if (string.Equals(fs, "CDFS", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? pnp = TryGetPnpForDriveLetter(dl);
                    if (string.IsNullOrWhiteSpace(pnp))
                        continue;

                    if (PnpMatches(pnp, _cfg.Device?.PnpDeviceIdContainsAny))
                        return dl;
                }
            }
            catch (Exception ex)
            {
                LogError($"TryGetUnlockedDriveLetterForDevice error: {ex.Message}");
            }

            return null;
        }

        private static void LaunchSyncProcess_TrustedUnlockedEvent(string? hintDrive, string reason)
        {
            try
            {
                PostUiStateUpdate();

                lock (_syncLock)
                {
                    if (!File.Exists(SyncExePath))
                    {
                        LogError($"{reason}: Sync exe missing: {SyncExePath}");
                        SetSyncFailed(true, "sync-exe-missing");
                        return;
                    }

                    if (_syncProcess != null && !_syncProcess.HasExited)
                    {
                        LogInfo($"{reason}: Sync already running (PID {_syncProcess.Id}).");
                        PostUiStateUpdate();
                        return;
                    }

                    var args = string.IsNullOrWhiteSpace(hintDrive) ? "" : $"--hintDrive \"{hintDrive}\"";
                    LogInfo($"{reason}: Starting Sync: {SyncExePath} {args}".Trim());

                    var psi = new ProcessStartInfo
                    {
                        FileName = SyncExePath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var p = new Process
                    {
                        StartInfo = psi,
                        EnableRaisingEvents = true
                    };

                    p.Exited += (_, _) =>
                    {
                        try
                        {
                            int code = p.ExitCode;
                            if (code == 0)
                            {
                                LogInfo($"Sync completed successfully (exit {code}).");
                                SetSyncFailed(false, "sync-success");
                            }
                            else
                            {
                                LogError($"Sync exited with code {code}.");
                                SetSyncFailed(true, $"sync-exit-{code}");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError($"Sync exit handler error: {ex}");
                            SetSyncFailed(true, "sync-exit-handler-error");
                        }
                        finally
                        {
                            lock (_syncLock)
                            {
                                try { p.Dispose(); } catch { }
                                if (ReferenceEquals(_syncProcess, p))
                                    _syncProcess = null;
                            }

                            PostUiStateUpdate();
                        }
                    };

                    if (!p.Start())
                    {
                        LogError($"{reason}: Sync process failed to start (Start() returned false).");
                        SetSyncFailed(true, "sync-start-false");
                        return;
                    }

                    _syncProcess = p;
                    PostUiStateUpdate();
                }
            }
            catch (Exception ex)
            {
                LogError($"LaunchSyncProcess error: {ex}");
                SetSyncFailed(true, "sync-launch-exception");
            }
        }

        private static bool TryLaunchUnlockFromTray(out string? error)
        {
            error = null;

            try
            {
                string? exeName = string.IsNullOrWhiteSpace(_cfg.Unlock.ExeName) ? null : _cfg.Unlock.ExeName.Trim();
                if (string.IsNullOrWhiteSpace(exeName))
                {
                    error = "Unlock.ExeName is not configured in config.json.";
                    return false;
                }

                string? cdfsDrive = FindCdfsDriveContainingExe(exeName);
                if (cdfsDrive == null)
                {
                    error = "Could not locate the unlock utility on the device CD drive.";
                    return false;
                }

                string exePath = Path.Combine(cdfsDrive + "\\", exeName);

                if (!File.Exists(exePath))
                {
                    error = $"Unlock utility not found: {exePath}";
                    return false;
                }

                LogInfo($"Tray unlock requested. Launching: {exePath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool IsMatchingUsbDiskPresent()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject disk in searcher.Get())
                {
                    string pnp = (disk["PNPDeviceID"] as string) ?? "";
                    if (PnpMatches(pnp, _cfg.Device?.PnpDeviceIdContainsAny))
                        return true;
                }
            }
            catch (Exception ex)
            {
                LogError($"IsMatchingUsbDiskPresent error: {ex.Message}");
            }

            return false;
        }

        private static bool IsMatchingUnlockedVolumePresent()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DriveLetter, FileSystem FROM Win32_Volume");
                foreach (ManagementObject vol in searcher.Get())
                {
                    string? dl = vol["DriveLetter"] as string;
                    string? fs = vol["FileSystem"] as string;

                    if (string.IsNullOrWhiteSpace(dl) || string.IsNullOrWhiteSpace(fs))
                        continue;

                    if (string.Equals(fs, "CDFS", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? pnp = TryGetPnpForDriveLetter(dl);
                    if (string.IsNullOrWhiteSpace(pnp))
                        continue;

                    if (PnpMatches(pnp, _cfg.Device?.PnpDeviceIdContainsAny))
                        return true;
                }
            }
            catch (Exception ex)
            {
                LogError($"IsMatchingUnlockedVolumePresent error: {ex.Message}");
            }

            return false;
        }

        private static string? FindCdfsDriveContainingExe(string exeName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DriveLetter, FileSystem FROM Win32_Volume");
                foreach (ManagementObject vol in searcher.Get())
                {
                    string? dl = vol["DriveLetter"] as string;
                    string? fs = vol["FileSystem"] as string;

                    if (string.IsNullOrWhiteSpace(dl))
                        continue;

                    if (!string.Equals(fs, "CDFS", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string candidate = Path.Combine(dl + "\\", exeName);
                    if (File.Exists(candidate))
                        return dl;
                }
            }
            catch (Exception ex)
            {
                LogError($"FindCdfsDriveContainingExe error: {ex.Message}");
            }

            return null;
        }

        private static string? TryGetPnpForDriveLetter(string driveLetter)
        {
            try
            {
                string qPart =
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{EscapeWmi(driveLetter)}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";

                using var partSearcher = new ManagementObjectSearcher(qPart);
                foreach (ManagementObject part in partSearcher.Get())
                {
                    string partId = (part["DeviceID"] as string) ?? "";
                    if (string.IsNullOrWhiteSpace(partId))
                        continue;

                    string qDisk =
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmi(partId)}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";

                    using var diskSearcher = new ManagementObjectSearcher(qDisk);
                    foreach (ManagementObject disk in diskSearcher.Get())
                    {
                        string pnp = (disk["PNPDeviceID"] as string) ?? "";
                        if (!string.IsNullOrWhiteSpace(pnp))
                            return pnp;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"TryGetPnpForDriveLetter error: {ex.Message}");
            }

            return null;
        }

        private static bool PnpMatches(string pnp, string[]? containsAny)
        {
            if (string.IsNullOrWhiteSpace(pnp))
                return false;

            if (containsAny == null || containsAny.Length == 0)
                return false;

            foreach (var needle in containsAny)
            {
                if (string.IsNullOrWhiteSpace(needle))
                    continue;

                if (pnp.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static Config LoadConfigOrThrow(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("config.json not found", path);

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<Config>(
                          json,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidOperationException("Failed to deserialize config.json.");

            if (cfg.Device?.PnpDeviceIdContainsAny == null || cfg.Device.PnpDeviceIdContainsAny.Length == 0)
                throw new InvalidOperationException("config.json missing Device.PnpDeviceIdContainsAny.");

            cfg.Unlock ??= new Config.UnlockConfig();
            cfg.Sync ??= new Config.SyncConfig();

            return cfg;
        }

        private static void RunInit()
        {
            try
            {
                LogInfo("Init mode starting.");

                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                               ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

                string desiredValue = $"\"{AgentExePath}\"";
                string? existingValue = runKey.GetValue(HkcuRunValueName) as string;

                if (!string.Equals(existingValue, desiredValue, StringComparison.OrdinalIgnoreCase))
                {
                    runKey.SetValue(HkcuRunValueName, desiredValue, RegistryValueKind.String);
                    LogInfo("Init: HKCU Run created/updated for this user.");
                }
                else
                {
                    LogInfo("Init: HKCU Run already correct.");
                }

                if (IsAgentAlreadyRunning())
                {
                    LogInfo("Init: Agent already running in this session. Not launching another instance.");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = AgentExePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                LogInfo("Init: Launched agent normal mode. Exiting init.");
            }
            catch (Exception ex)
            {
                LogError($"Init failed: {ex}");
            }
        }

        private static bool IsAgentAlreadyRunning()
        {
            try
            {
                using var mutex = Mutex.OpenExisting(SingleInstanceMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                LogError($"IsAgentAlreadyRunning check failed: {ex.Message}");
                return false;
            }
        }

        private static void TryAddToastLogo(ToastContentBuilder builder)
        {
            try
            {
                if (!File.Exists(ToastLogoPngPath))
                    return;

                builder.AddAppLogoOverride(new Uri(ToastLogoPngPath), ToastGenericAppLogoCrop.Circle);
            }
            catch (Exception ex)
            {
                LogError($"Toast logo load failed: {ex.Message}");
            }
        }

        private static void TryToast(string title, string body)
        {
            try
            {
                var builder = new ToastContentBuilder()
                    .AddText(title)
                    .AddText(body);

                TryAddToastLogo(builder);
                builder.Show();
            }
            catch (Exception ex)
            {
                LogError($"Toast failed: {ex.Message}");
            }
        }

        private static string EscapeWmi(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private static void LogInfo(string msg, int eventId = EventIds.Agent.Started) => Logger.Info(msg, eventId);
        private static void LogWarn(string msg, int eventId = EventIds.Agent.Warning) => Logger.Warn(msg, eventId);
        private static void LogError(string msg, int eventId = EventIds.Agent.Error) => Logger.Error(msg, eventId);

        private sealed class TrayAppContext : ApplicationContext
        {
            private readonly NotifyIcon _notifyIcon;

            private readonly Icon _iconDisconnected;
            private readonly Icon _iconLocked;
            private readonly Icon _iconOk;
            private readonly Icon _iconSync;
            private readonly Icon _iconError;

            private readonly ToolStripMenuItem _unlockItem;
            private readonly ToolStripMenuItem _syncItem;

            private bool _deviceDetected;
            private bool _deviceUnlocked;
            private bool _syncRunning;
            private bool _lastSyncFailed;
            private bool _syncExePresent;

            public TrayAppContext()
            {
                _iconDisconnected = LoadEmbeddedIconOrFallback("USBWatcher-disconnected.ico", SystemIcons.Application);
                _iconLocked = LoadEmbeddedIconOrFallback("USBWatcher-locked.ico", SystemIcons.Shield);
                _iconOk = LoadEmbeddedIconOrFallback("USBWatcher-ok.ico", SystemIcons.Application);
                _iconSync = LoadEmbeddedIconOrFallback("USBWatcher-sync.ico", _iconOk);
                _iconError = LoadEmbeddedIconOrFallback("USBWatcher-error.ico", SystemIcons.Error);

                var menu = new ContextMenuStrip();

                _unlockItem = new ToolStripMenuItem("Unlock");
                _unlockItem.Click += (_, _) =>
                {
                    if (!_unlockItem.Enabled)
                        return;

                    if (!TryLaunchUnlockFromTray(out var err))
                    {
                        LogError($"Unlock failed: {err}");
                        TryToast("USB Watcher", err ?? "Unlock failed.");
                    }
                };
                menu.Items.Add(_unlockItem);

                _syncItem = new ToolStripMenuItem("Sync");
                _syncItem.Click += (_, _) =>
                {
                    if (!_syncItem.Enabled)
                        return;

                    if (!IsCurrentSessionActiveForSync())
                    {
                        LogInfo("Tray: manual sync requested, but current session is not active. Ignoring.");
                        TryToast("USB Watcher", "Sync can only be started from the active user session.");
                        return;
                    }

                    var hint = TryGetUnlockedDriveLetterForDevice();
                    if (string.IsNullOrWhiteSpace(hint))
                    {
                        LogError("Tray: manual sync requested but could not resolve unlocked drive letter; launching sync without hint.");
                    }

                    LaunchSyncProcess_TrustedUnlockedEvent(hint, "Tray: manual sync");
                };
                menu.Items.Add(_syncItem);

                menu.Items.Add(new ToolStripSeparator());

                var exitItem = new ToolStripMenuItem("Exit");
                exitItem.Click += (_, _) => ExitThread();
                menu.Items.Add(exitItem);

                _notifyIcon = new NotifyIcon
                {
                    Icon = _iconDisconnected,
                    Visible = true,
                    Text = "USB Watcher",
                    ContextMenuStrip = menu
                };

                menu.Opening += (_, _) => RefreshMenuState();
                RefreshMenuState();
                UpdateIcon();
            }

            public void UpdateState(bool deviceDetected, bool deviceUnlocked, bool syncRunning, bool lastSyncFailed, bool syncExePresent)
            {
                _deviceDetected = deviceDetected;
                _deviceUnlocked = deviceUnlocked;
                _syncRunning = syncRunning;
                _lastSyncFailed = lastSyncFailed;
                _syncExePresent = syncExePresent;

                UpdateIcon();
                RefreshMenuState();
            }

            private bool CanSync() => _deviceDetected && _deviceUnlocked && _syncExePresent && !_syncRunning;

            private void UpdateIcon()
            {
                Icon icon =
                    !_deviceDetected ? _iconDisconnected :
                    !_deviceUnlocked ? _iconLocked :
                    _syncRunning ? _iconSync :
                    _lastSyncFailed ? _iconError :
                    _iconOk;

                _notifyIcon.Icon = icon;
                _notifyIcon.Text =
                    !_deviceDetected ? "USB Watcher - Not detected" :
                    !_deviceUnlocked ? "USB Watcher - Locked" :
                    _syncRunning ? "USB Watcher - Syncing" :
                    _lastSyncFailed ? "USB Watcher - Sync failed" :
                    "USB Watcher - Ready";
            }

            private void RefreshMenuState()
            {
                _unlockItem.Enabled = _deviceDetected && !_deviceUnlocked;
                _syncItem.Enabled = CanSync();
            }

            protected override void ExitThreadCore()
            {
                try
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
                catch
                {
                }

                base.ExitThreadCore();
            }

            private static Icon LoadEmbeddedIconOrFallback(string resourceEndsWith, Icon fallback)
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var name = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith(resourceEndsWith, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrWhiteSpace(name))
                        return fallback;

                    using var s = asm.GetManifestResourceStream(name);
                    if (s == null)
                        return fallback;

                    return new Icon(s);
                }
                catch
                {
                    return fallback;
                }
            }
        }
    }
}