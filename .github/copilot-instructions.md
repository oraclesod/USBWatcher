# Copilot instructions — DataLockerWatcher

Purpose: provide concise, actionable guidance so an AI coding agent can be productive in this repo.

1) Big picture
- Components: `Installer`, `Agent`, `Sync` projects under the solution root.
  - `Installer` (Installer/*): installs files to Program Files, creates Start Menu shortcut + AUMID, and sets HKLM Run to execute `Agent --init` at each user logon.
  - `Agent` (Agent/*): background process using WMI watchers to detect USB devices and removable volumes, shows Toasts, and launches `DataLockerWatcher-Sync.exe` when the removable is unlocked.
  - `Sync` (Sync/*): performs the actual file synchronization using `robocopy` from a network share (config.Sync.SourceFolder) into a folder on the unlocked drive.

2) Key integration & runtime details (must be respected by code changes)
- All three executables read `config.json` from `AppContext.BaseDirectory` at runtime — see `Sync/config.json` for the canonical example.
- Device matching: both `Agent` and `Sync` match disk PNP strings using substring checks against `Device.PnpDeviceIdContainsAny` (case-insensitive). Keep those implementations in sync when editing.
- Toast activation plumbing: Installer assigns an AUMID to the Agent shortcut (via `ShortcutHelper`) so toast button activations route back to the Agent process. Without the shortcut/AUMID, toasts may display but activation callbacks will not be delivered.
- Launch flow: Installer -> HKLM Run -> Agent `--init` -> Agent writes HKCU Run for the logged-in user -> Agent (background) launches `Sync` with `--hintDrive \"X:\"` when volume is mounted.

3) Important files to reference
- `Agent/Program.cs` — WMI watchers, toast handling, config loading.
- `Sync/Program.cs` — drive detection, robocopy invocation, config validation.
- `Installer/Installer.cs` — payload copying, HKLM Run, Start Menu shortcut + AUMID logic.
- `Sync/config.json` — canonical config example (PNP signature, unlocker EXE name, network source path).

4) Build / run / debug workflows
- Build: from repo root run `dotnet build DataLockerWatcher.sln` (works for CI or local developer builds).
- Run `Sync` manually for quick tests: use `dotnet run --project Sync -- --hintDrive \"E:\"` or run the built exe in `Sync/bin/.../DataLockerWatcher-Sync.exe --hintDrive \"E:\"`.
- Test Agent toast activation end-to-end: run the `Installer` (requires elevation) to create shortcut + HKLM Run; `Installer` will attempt to run Agent `--init` in the active session. Without installation, toasts show but activation callbacks require the AUMID shortcut.

5) Project-specific conventions & pitfalls
- Config duplication: `Agent` and `Sync` define near-identical `Config` types. If you change a config shape, update both projects and the `Sync/config.json` example.
- PNP matching uses simple substring matching — do not change to exact-match semantics without updating both projects and tests (there are no unit tests present).
- Cooldowns and UX: `Agent` enforces cooldowns (toast and sync launch). See `Agent/Program.cs` for `DetectToastCooldown` and `SyncLaunchCooldown` — changing timing affects user experience and possible duplicate launches.
- Logging: components write to Windows EventLog using sources `DataLockerWatcher-Agent`, `DataLockerWatcher-Sync`, `DataLockerWatcher-Install`. On failure they fallback to files under LocalAppData or ProgramFiles (see each project's `FallbackLogPath` / `Install.log`). Prefer preserving event source names.

6) External dependencies & environment assumptions
- Windows-only: the code uses WMI, Windows Registry, COM shortcuts and Windows toasts — expect Windows dev and CI agents for end-to-end tests.
- `robocopy` is required and assumed to live in `C:\Windows\System32` (used by `Sync`).
- Toast libraries: `Agent` uses `Microsoft.Toolkit.Uwp.Notifications` and `Sync` uses `CommunityToolkit.WinUI.Notifications`; changes to notification APIs must respect activation argument names (`action`, `drive`, `exe`).

7) Small examples / patterns to follow
- Toast unlock button: the Agent builds a toast with arguments `{ action=unlock, drive=E:, exe=unlocker.exe }`. The activation handler expects `action==unlock` and launches `drive\\exe` (see `Agent/Program.cs`).
- Sync invocation: Agent launches `DataLockerWatcher-Sync.exe --hintDrive \"{drive}\"` (keep argument name `--hintDrive`).
- Installer usage: `DataLockerWatcher-Install.exe install|repair|uninstall` (see `Installer/Program.cs`).

8) Quick checklist for making changes
- Update both `Agent` and `Sync` config types when changing `config.json` schema.
- Preserve EventLog source strings when renaming features to avoid losing historical logs.
- If modifying toast activation or AUMID logic, update `Installer/ShortcutHelper.cs` and test end-to-end via the `Installer` (elevation required).

If anything above is unclear or you want me to include quick runnable examples (exact run paths for bin outputs, or a minimal test harness), tell me which area to expand and I'll iterate.
