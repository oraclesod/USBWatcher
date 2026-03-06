using System;

namespace DataLockerWatcherInstall
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            var mode = (args.Length > 0 ? args[0] : "").Trim().ToLowerInvariant();
            if (mode != "install" && mode != "repair" && mode != "uninstall")
            {
                Console.Error.WriteLine("Usage: DataLockerWatcher-Install.exe <install|repair|uninstall>");
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
                    _ => 2
                };
            }
            catch (Exception ex)
            {
                installer.LogInstallEvent($"Fatal error: {ex}", System.Diagnostics.EventLogEntryType.Error);
                return 1;
            }
        }
    }
}
