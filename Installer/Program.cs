using System;
using System.Linq;
using USBWatcher.Common;

namespace USBWatcherInstall
{
    internal static class Program
    {
        private const string SharedEventSource = "USBWatcher";
        private const string ComponentName = "Install";

        private static readonly string InstallDir =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "USBWatcher");

        private static readonly string InstallLogPath =
            System.IO.Path.Combine(InstallDir, "Install.log");

        static int Main(string[] args)
        {
            var logger = new Logger(SharedEventSource, InstallLogPath, ComponentName);
            var mode = (args.Length > 0 ? args[0] : "").Trim().ToLowerInvariant();

            if (mode != "install" && mode != "repair" && mode != "uninstall" && mode != "update")
            {
                Console.Error.WriteLine("Usage:");
                Console.Error.WriteLine("  Install.exe install");
                Console.Error.WriteLine("  Install.exe repair");
                Console.Error.WriteLine("  Install.exe uninstall");
                Console.Error.WriteLine("  Install.exe update <jsonPath> <value>");
                Console.Error.WriteLine("");
                Console.Error.WriteLine("Examples:");
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