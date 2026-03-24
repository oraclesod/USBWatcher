using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace USBWatcherInstall
{
    internal static class ShortcutHelper
    {
        /// <summary>
        /// Creates an All-Users Start Menu shortcut and assigns an explicit AUMID.
        /// Must be run elevated to write under CommonPrograms.
        /// </summary>
        public static void CreateStartMenuShortcutWithAumid(
            string startMenuFolderName,
            string shortcutName,
            string targetPath,
            string arguments,
            string workingDirectory,
            string appUserModelId,
            string description)
        {
            if (string.IsNullOrWhiteSpace(startMenuFolderName))
                throw new ArgumentException("startMenuFolderName is required", nameof(startMenuFolderName));
            if (string.IsNullOrWhiteSpace(shortcutName))
                throw new ArgumentException("shortcutName is required", nameof(shortcutName));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("targetPath is required", nameof(targetPath));
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Target EXE not found", targetPath);
            if (string.IsNullOrWhiteSpace(appUserModelId))
                throw new ArgumentException("appUserModelId is required", nameof(appUserModelId));

            string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string folderPath = Path.Combine(programsPath, startMenuFolderName);
            Directory.CreateDirectory(folderPath);

            string shortcutPath = Path.Combine(folderPath, shortcutName + ".lnk");

            // Avoid permission/ACL weirdness on existing shortcuts
            TryDeleteFile(shortcutPath);

            // Create the shortcut via ShellLink COM
            var link = (IShellLinkW)new ShellLink();

            link.SetPath(targetPath);
            link.SetArguments(arguments ?? "");
            link.SetDescription(description ?? shortcutName);
            link.SetIconLocation(targetPath, 0);

            string wd = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : (Path.GetDirectoryName(targetPath) ?? programsPath);
            link.SetWorkingDirectory(wd);

            // Set AUMID via property store before saving
            var propStore = (IPropertyStore)link;
            var key = PropertyKey.AppUserModelId;
            using (var pv = new PropVariant(appUserModelId))
            {
                int hr = propStore.SetValue(ref key, pv);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                hr = propStore.Commit();
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }

            // Save to .lnk
            ((IPersistFile)link).Save(shortcutPath, true);
        }

        public static void RemoveStartMenuFolder(string startMenuFolderName)
        {
            string programsPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            string folderPath = Path.Combine(programsPath, startMenuFolderName);

            if (Directory.Exists(folderPath))
            {
                // Best-effort delete
                Directory.Delete(folderPath, recursive: true);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore; will fail later with a meaningful exception if we truly can't overwrite
            }
        }

        #region COM Interop

        // CLSID_ShellLink
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        // IShellLinkW (full v-table: required for SetPath/SetArguments/etc.)
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, out WIN32_FIND_DATAW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        // IPropertyStore for setting AUMID
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, [In] PropVariant pv);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public static PropertyKey AppUserModelId => new PropertyKey
            {
                // PKEY_AppUserModel_ID
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                pid = 5
            };
        }

        // Minimal PROPVARIANT for VT_LPWSTR
        [StructLayout(LayoutKind.Explicit)]
        private sealed class PropVariant : IDisposable
        {
            [FieldOffset(0)] private ushort vt;
            [FieldOffset(8)] private IntPtr ptr;

            public PropVariant(string value)
            {
                vt = 31; // VT_LPWSTR
                ptr = Marshal.StringToCoTaskMemUni(value);
            }

            public void Dispose()
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(ptr);
                    ptr = IntPtr.Zero;
                }
                vt = 0;
            }
        }

        #endregion
    }
}
