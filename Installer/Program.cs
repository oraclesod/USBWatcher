using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using USBWatcher.Common;

namespace USBWatcherInstall
{
    internal static class Program
    {
        private const string SharedEventSource = "USBWatcher";
        private const string ComponentName = "Install";

        private static readonly string InstallDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "USBWatcher");

        private static readonly string InstallLogPath =
            Path.Combine(InstallDir, "Install.log");

        private static readonly string CleanerExePath =
            Path.Combine(AppContext.BaseDirectory, "Cleaner.exe");

        static int Main(string[] args)
        {
            var logger = new Logger(SharedEventSource, InstallLogPath, ComponentName);
            var mode = (args.Length > 0 ? args[0] : "").Trim().ToLowerInvariant();

            if (mode != "install" &&
                mode != "cleaninstall" &&
                mode != "repair" &&
                mode != "uninstall" &&
                mode != "update")
            {
                Console.Error.WriteLine("Usage:");
                Console.Error.WriteLine("  Install.exe install");
                Console.Error.WriteLine("  Install.exe cleaninstall");
                Console.Error.WriteLine("  Install.exe repair");
                Console.Error.WriteLine("  Install.exe uninstall");
                Console.Error.WriteLine("  Install.exe update <jsonPath> <value>");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Examples:");
                Console.Error.WriteLine(@"  Install.exe cleaninstall");
                Console.Error.WriteLine(@"  Install.exe update Sync:Source \\server\share\folder");
                Console.Error.WriteLine(@"  Install.exe update Device:PnpDeviceIdContainsAny ""VEN_DL"",""PROD_SENTRY""");

                logger.Warn("Invalid command-line usage.", EventIds.Install.InvalidUsage);
                return 2;
            }

            var installer = new Installer();

            try
            {
                return mode switch
                {
                    "install" => installer.InstallOrRepair(repair: false),
                    "cleaninstall" => RunCleanInstall(installer, logger),
                    "repair" => installer.InstallOrRepair(repair: true),
                    "uninstall" => installer.Uninstall(),
                    "update" => RunUpdate(installer, args),
                    _ => 2
                };
            }
            catch (Exception ex)
            {
                logger.Error($"Fatal error: {ex}", EventIds.Install.FatalError);
                return 1;
            }
        }

        private static int RunCleanInstall(Installer installer, Logger logger)
        {
            try
            {
                logger.Info("Clean install requested. Starting Cleaner.exe first.", EventIds.Install.Started);

                if (!File.Exists(CleanerExePath))
                {
                    logger.Error($"Cleaner.exe not found at '{CleanerExePath}'.", EventIds.Install.FatalError);
                    Console.Error.WriteLine($"Cleaner.exe not found at '{CleanerExePath}'.");
                    return 1;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = CleanerExePath,
                    Arguments = "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(CleanerExePath) ?? AppContext.BaseDirectory
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    logger.Error("Failed to start Cleaner.exe.", EventIds.Install.FatalError);
                    Console.Error.WriteLine("Failed to start Cleaner.exe.");
                    return 1;
                }

                logger.Info("Waiting for Cleaner.exe to finish.", EventIds.Install.Started);
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    logger.Error($"Cleaner.exe exited with code {process.ExitCode}. Aborting clean install.", EventIds.Install.Error);
                    Console.Error.WriteLine($"Cleaner.exe exited with code {process.ExitCode}.");
                    return process.ExitCode;
                }

                logger.Info("Cleaner.exe completed successfully. Proceeding with install.", EventIds.Install.Completed);
                return installer.InstallOrRepair(repair: false);
            }
            catch (Exception ex)
            {
                logger.Error($"Clean install failed: {ex}", EventIds.Install.FatalError);
                Console.Error.WriteLine($"Clean install failed: {ex.Message}");
                return 1;
            }
        }

        private static int RunUpdate(Installer installer, string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: Install.exe update <jsonPath> <value>");
                return 2;
            }

            string jsonPath = args[1];
            string rawValue = string.Join(" ", args.Skip(2));

            return installer.UpdateConfigValue(jsonPath, rawValue);
        }
    }
}
