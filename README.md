# USB Watcher

USB Watcher is a Windows solution designed to detect approved encrypted
USB devices, guide the user through unlock and sync workflows, and
securely synchronize files from an approved network location.

The solution consists of **three components**:

1.  **Installer** -- SYSTEM-level installer responsible for setup,
    upgrade, repair, update, and uninstall
2.  **Agent** -- User logon-triggered background process
3.  **Sync** -- On-demand worker process that performs file
    synchronization

------------------------------------------------------------------------

# 1. Components Overview

## Installer

Runs elevated as **SYSTEM** or **Administrator**

Installs binaries to:

`C:\Program Files\USBWatcher`

Creates and maintains: - Windows Event Log source: `USBWatcher` - Start
Menu shortcut with **AUMID** for toast activation - Shortcut icon
sourced from the **Agent EXE (embedded icon)** - **HKLM Run** entry to
initialize the Agent at every user logon - Local install copy of the
installer for future repair or uninstall

Handles: - install (including upgrade logic) - repair - update
(config-only changes) - uninstall

------------------------------------------------------------------------

## Agent

Runs in the **interactive user session**

Responsibilities: - Starts automatically at logon - Monitors USB
insertion via WMI - Detects approved devices - Displays toast
notifications - Launches Sync when required - Extracts toast image (PNG)
to:

`%TEMP%\USBWatcher\`

-   Reuses extracted image for all notifications

------------------------------------------------------------------------

## Sync

Performs the actual synchronization

Responsibilities: - Detects unlocked USB device - Validates source
access - Copies or extracts files - Uses shared toast image from
`%TEMP%\USBWatcher\` - Reports status via toast + logs

Modes:

  Mode   Description
  ------ ------------------------------
  1      Direct folder sync
  2      Encrypted ZIP extract + sync

------------------------------------------------------------------------

# 2. Installer Command-Line Usage

Install.exe install
Install.exe repair
Install.exe update `<jsonPath>` `<value>`
Install.exe uninstall

------------------------------------------------------------------------

## 2.1 install (includes upgrade)

-   If no install exists → full install
-   If install exists → upgrade

### Configuration source priority:

1.  External config.json (same folder as installer)
2.  Embedded config (fallback)

### Upgrade behavior:

1.  Wait up to 10 minutes for Sync to exit\
2.  If still running → exit code 1\
3.  Kill all Agent processes\
4.  Merge config.json:
    -   add new keys from template
    -   remove deprecated keys
    -   preserve ALL existing values
5.  Replace binaries\
6.  Restart Agent

------------------------------------------------------------------------

## 2.2 repair

-   If install missing → full install
-   If install exists:
    -   overwrite ALL files
    -   replace config.json completely (no merge)

------------------------------------------------------------------------

## 2.3 update (config only)

Install.exe update Sync:Source "\\server\\share"
Install.exe update Device:PnpDeviceIdContainsAny "VID_1234","VID_5678"

Behavior: - Updates existing config keys only - Does NOT allow adding
new keys - Supports arrays via comma-separated values

------------------------------------------------------------------------

## 2.4 uninstall

Removes: - installed files - Start Menu shortcut - HKLM + HKCU Run
entries

Leaves logs for troubleshooting.

------------------------------------------------------------------------

# 3. Installed File Layout

`C:\Program Files\USBWatcher\`
Install.exe
USBWatcher-Agent.exe
USBWatcher-Sync.exe
config.json
Install.log

------------------------------------------------------------------------

# 4. Configuration (config.json)

{ "Device": { "PnpDeviceIdContainsAny": \["VID_1234"\] }, "Unlock": {
"ExeName": "SafeConsole.exe", "ToastOnDetected": true }, "Sync": {
"Source": "\\server\\share", "TargetFolder": "Data", "Mode": 1, "Key":
"" } }

------------------------------------------------------------------------

# 5. Logging

## Windows Event Log

-   Log Name: Application
-   Source: USBWatcher

Event ID ranges: - 1000--1999 → Installer - 2000--2999 → Agent -
3000--3999 → Sync

------------------------------------------------------------------------

## File Logs

  Component   Path
  ----------- ------------------------------------------------------
  Install     `Program Files\USBWatcher\Install.log`
  Agent       `%LOCALAPPDATA%\USBWatcher\Agent.log`
  Sync        `%LOCALAPPDATA%\USBWatcher\Sync.log`

------------------------------------------------------------------------

# 6. Common Project Structure

USBWatcher.Common Logger.cs EventIds.cs

------------------------------------------------------------------------

# 7. Operational Flow

## Logon

1.  User logs in
2.  HKLM Run triggers Agent --init
3.  Agent initializes and monitors USB

## Device Workflow

1.  USB inserted
2.  Agent detects device
3.  Toast prompts user to unlock
4.  User launches unlock tool
5.  Agent detects unlocked volume
6.  Sync executes
7.  Files copied/extracted
8.  Status reported

------------------------------------------------------------------------

# 8. Exit Codes

  Code   Meaning
  ------ ---------------
  0      Success
  1      Failure
  2      Invalid usage

------------------------------------------------------------------------

# 9. Deployment Notes

For Intune: 
- Run as SYSTEM
- install → install/upgrade
- uninstall → removal

Detection:
- File presence
- Registry Run key
