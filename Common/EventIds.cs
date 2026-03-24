namespace USBWatcher.Common
{
    internal static class EventIds
    {
        internal static class Install
        {
            public const int Started = 1000;
            public const int Completed = 1001;
            public const int ExistingInstallDetected = 1002;
            public const int WaitingForSync = 1003;
            public const int SyncTimeout = 1004;
            public const int StoppingAgent = 1005;
            public const int AgentTimeout = 1006;
            public const int CopyingPayload = 1007;
            public const int ShortcutCreated = 1008;
            public const int RunKeySet = 1009;
            public const int StartAgentInit = 1010;
            public const int StartAgentInitFallback = 1011;
            public const int UninstallStarted = 1012;
            public const int UninstallCompleted = 1013;
            public const int ConfigMerged = 1014;
            public const int ConfigReplaced = 1015;
            public const int ConfigUpdated = 1016;
            public const int InvalidUsage = 1017;
            public const int FatalError = 1018;
            public const int Warning = 1098;
            public const int Error = 1099;
        }

        internal static class Agent
        {
            public const int Started = 2000;
            public const int DeviceDetected = 2001;
            public const int DeviceUnlocked = 2002;
            public const int SyncLaunch = 2003;
            public const int ToastShown = 2004;
            public const int Warning = 2098;
            public const int Error = 2099;
        }

        internal static class Sync
        {
            public const int Started = 3000;
            public const int Completed = 3001;
            public const int ExtractStarted = 3002;
            public const int ExtractCompleted = 3003;
            public const int Warning = 3098;
            public const int Error = 3099;
        }
    }
}