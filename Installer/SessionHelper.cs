using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DataLockerWatcherInstall
{
    internal sealed class ActiveSession
    {
        public uint SessionId { get; init; }
    }

    internal static class SessionHelper
    {
        /// <summary>
        /// Attempts to find the best interactive user session.
        /// Prefers WTSActive, then WTSConnected, then falls back to active console session.
        /// </summary>
        public static ActiveSession? TryGetActiveSession()
        {
            // 1) Prefer enumerated sessions (handles RDP / multi-session cases)
            if (TryGetBestInteractiveSessionId(out uint bestSessionId))
                return new ActiveSession { SessionId = bestSessionId };

            // 2) Fallback: console session
            uint sid = WTSGetActiveConsoleSessionId();
            if (sid == 0xFFFFFFFF) return null;
            return new ActiveSession { SessionId = sid };
        }

        /// <summary>
        /// Best-effort: launches a process into the best interactive session.
        /// Returns false if not possible (commonly when not running as SYSTEM, or no user logged on).
        /// </summary>
        public static bool TryRunAsActiveUser(string fileName, string arguments, bool hidden, out string? error)
        {
            error = null;

            var session = TryGetActiveSession();
            if (session == null)
            {
                error = "No interactive user session found (no user logged on?).";
                return false;
            }

            return TryRunInSession(session.SessionId, fileName, arguments, hidden, out error);
        }

        private static bool TryRunInSession(uint sessionId, string fileName, string arguments, bool hidden, out string? error)
        {
            error = null;

            if (!File.Exists(fileName))
            {
                error = $"File not found: {fileName}";
                return false;
            }

            // 1) Get an impersonation token for the user in that session
            if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            {
                error = $"WTSQueryUserToken({sessionId}) failed: {GetLastErrorMessage()}";
                return false;
            }

            try
            {
                // 2) Duplicate into a PRIMARY token
                var desiredAccess =
                    TOKEN_DUPLICATE |
                    TOKEN_ASSIGN_PRIMARY |
                    TOKEN_QUERY |
                    TOKEN_ADJUST_DEFAULT |
                    TOKEN_ADJUST_SESSIONID;

                if (!DuplicateTokenEx(
                        userToken,
                        desiredAccess,
                        IntPtr.Zero,
                        SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                        TOKEN_TYPE.TokenPrimary,
                        out IntPtr primaryToken))
                {
                    error = $"DuplicateTokenEx failed: {GetLastErrorMessage()}";
                    return false;
                }

                try
                {
                    // 2b) Best-effort: force session id onto the duplicated token (helps in edge cases)
                    // If this fails we continue; CreateProcessAsUser may still succeed.
                    uint sid = sessionId;
                    _ = SetTokenInformation(
                        primaryToken,
                        TOKEN_INFORMATION_CLASS.TokenSessionId,
                        ref sid,
                        (uint)Marshal.SizeOf<uint>());

                    // 3) Build environment block (best effort)
                    IntPtr env = IntPtr.Zero;
                    bool envOk = CreateEnvironmentBlock(out env, primaryToken, false);
                    if (!envOk)
                        env = IntPtr.Zero;

                    try
                    {
                        var si = new STARTUPINFO();
                        si.cb = Marshal.SizeOf(si);
                        si.lpDesktop = @"winsta0\default";

                        if (hidden)
                        {
                            si.dwFlags = STARTF_USESHOWWINDOW;
                            si.wShowWindow = SW_HIDE;
                        }

                        // NOTE: We do NOT want to zero flags just because env is null.
                        uint creationFlags = 0;
                        if (env != IntPtr.Zero)
                            creationFlags |= CREATE_UNICODE_ENVIRONMENT;

                        // Build command line
                        string cmdLine = $"\"{fileName}\"".Trim();
                        if (!string.IsNullOrWhiteSpace(arguments))
                            cmdLine += " " + arguments.Trim();

                        string? workDir = null;
                        try
                        {
                            workDir = Path.GetDirectoryName(fileName);
                        }
                        catch
                        {
                            // ignore; workDir can be null
                        }

                        bool ok = CreateProcessAsUser(
                            primaryToken,
                            null,
                            cmdLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            false,
                            creationFlags,
                            env,
                            workDir,
                            ref si,
                            out PROCESS_INFORMATION pi);

                        if (!ok)
                        {
                            // Helpful hint for the common privilege/identity issue
                            var msg = GetLastErrorMessage();
                            error = $"CreateProcessAsUser failed: {msg}";
                            if (msg.Contains("1314") || msg.Contains("privilege", StringComparison.OrdinalIgnoreCase))
                                error += " (Hint: this typically requires running as SYSTEM and having SeIncreaseQuotaPrivilege/SeAssignPrimaryTokenPrivilege.)";
                            return false;
                        }

                        // Close process/thread handles to avoid leaks
                        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

                        return true;
                    }
                    finally
                    {
                        if (env != IntPtr.Zero)
                            DestroyEnvironmentBlock(env);
                    }
                }
                finally
                {
                    CloseHandle(primaryToken);
                }
            }
            finally
            {
                CloseHandle(userToken);
            }
        }

        private static bool TryGetBestInteractiveSessionId(out uint sessionId)
        {
            sessionId = 0;

            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr ppSessionInfo, out int count) || ppSessionInfo == IntPtr.Zero || count <= 0)
                return false;

            try
            {
                int dataSize = Marshal.SizeOf<WTS_SESSION_INFO>();
                uint? active = null;
                uint? connected = null;

                for (int i = 0; i < count; i++)
                {
                    IntPtr itemPtr = IntPtr.Add(ppSessionInfo, i * dataSize);
                    var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(itemPtr);

                    // Ignore session 0
                    if (si.SessionID == 0) continue;

                    // Prefer active
                    if (si.State == WTS_CONNECTSTATE_CLASS.WTSActive)
                    {
                        active = (uint)si.SessionID;
                        break;
                    }

                    // Keep first connected as fallback
                    if (connected == null && si.State == WTS_CONNECTSTATE_CLASS.WTSConnected)
                        connected = (uint)si.SessionID;
                }

                if (active.HasValue)
                {
                    sessionId = active.Value;
                    return true;
                }

                if (connected.HasValue)
                {
                    sessionId = connected.Value;
                    return true;
                }

                return false;
            }
            finally
            {
                WTSFreeMemory(ppSessionInfo);
            }
        }

        private static string GetLastErrorMessage()
        {
            int err = Marshal.GetLastWin32Error();
            return $"({err}) {new Win32Exception(err).Message}";
        }

        #region Native interop

        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_HIDE = 0;

        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        private const uint TOKEN_ADJUST_SESSIONID = 0x0100;

        private enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation
        }

        private enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        private enum TOKEN_INFORMATION_CLASS
        {
            TokenUser = 1,
            TokenGroups,
            TokenPrivileges,
            TokenOwner,
            TokenPrimaryGroup,
            TokenDefaultDacl,
            TokenSource,
            TokenType,
            TokenImpersonationLevel,
            TokenStatistics,
            TokenRestrictedSids,
            TokenSessionId = 12
        }

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionID;

            [MarshalAs(UnmanagedType.LPStr)]
            public string pWinStationName;

            public WTS_CONNECTSTATE_CLASS State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr Token);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
            TOKEN_TYPE TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(
            IntPtr TokenHandle,
            TOKEN_INFORMATION_CLASS TokenInformationClass,
            ref uint TokenInformation,
            uint TokenInformationLength);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}
