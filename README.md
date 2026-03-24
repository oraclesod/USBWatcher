# USBWatcher

USBWatcher is a Windows solution designed to detect approved encrypted USB devices, guide the user through unlock and sync workflows, and securely synchronize files to and from an approved network location.

The solution consists of **three components**:

1. **Installer** – SYSTEM-level installer responsible for setup, upgrade, repair, update, and uninstall  
2. **Agent** – User logon-triggered background process  
3. **Sync** – On-demand worker process that performs file synchronization  

---

# 1. Components Overview

## Installer
- Runs elevated as **SYSTEM** or **Administrator**
- Installs binaries to:

`C:\Program Files\USBWatcher`

Creates and maintains:
- Windows Event Log source: `USBWatcher`
- Start Menu shortcut with **AUMID** for toast activation
- **HKLM Run** entry to initialize the Agent at every user logon
- Local install copy of the installer for future repair or uninstall

Handles:
- install (including upgrade logic)
- repair
- update (config-only changes)
- uninstall

---

## Agent
Runs in the **interactive user session**

Responsibilities:
- Starts automatically at logon
- Monitors USB insertion via WMI
- Detects approved devices
- Displays toast notifications
- Launches Sync when required

---

## Sync
Performs the actual synchronization

Responsibilities:
- Detects unlocked USB device
- Validates source access
- Copies or extracts files
- Reports status via toast + logs

Modes:

| Mode | Description |
|------|------------|
| 1 | Direct folder sync |
| 2 | Encrypted ZIP extract + sync |

---

# 2. Installer Command-Line Usage

```text
Install.exe install
Install.exe repair
Install.exe update <jsonPath> <value>
Install.exe uninstall
```

---

## 2.1 install (includes upgrade)

- If no install exists → full install
- If install exists → upgrade

### Upgrade behavior:
1. Wait up to **10 minutes** for Sync to exit
2. If still running → exit code 1
3. Kill all Agent processes
4. Merge `config.json`:
   - add new keys from installer config
   - remove deprecated keys
   - preserve existing values
5. Replace binaries
6. Restart Agent

---

## 2.2 repair

- If install missing → full install
- If install exists:
  - overwrite ALL files
  - replace config.json completely (no merge)

---

## 2.3 update (config only)

Example:

```text
Install.exe update Sync:Source "\\server\share"
Install.exe update Device:PnpDeviceIdContainsAny "VID_1234","VID_5678"
```

Behavior:
- Updates existing config keys only
- Does NOT allow adding new keys
- Supports arrays via comma-separated values

---

## 2.4 uninstall

Removes:
- installed files
- Start Menu shortcut
- HKLM + HKCU Run entries

Leaves logs for troubleshooting.

---

# 3. Installed File Layout

```text
C:\Program Files\USBWatcher\
    Install.exe
    USBWatcher-Agent.exe
    USBWatcher-Sync.exe
    config.json
    USBWatcher.ico
    USBWatcher.png
    Install.log
```

---

# 4. Configuration (config.json)

```json
{
  "Device": {
    "PnpDeviceIdContainsAny": ["VID_1234"]
  },
  "Unlock": {
    "ExeName": "SafeConsole.exe",
    "ToastOnDetected": true
  },
  "Sync": {
    "Source": "\\server\share",
    "TargetFolder": "Data",
    "Mode": 1,
    "Key": ""
  }
}
```

---

# 5. Logging

## Windows Event Log
- Log Name: Application
- Source: `USBWatcher`

Event ID ranges:
- 1000–1999 → Installer
- 2000–2999 → Agent
- 3000–3999 → Sync

---

## File Logs

| Component | Path |
|----------|------|
| Install | Program Files\USBWatcher\Install.log |
| Agent | %LOCALAPPDATA%\USBWatcher\Agent.log |
| Sync | %LOCALAPPDATA%\USBWatcher\Sync.log |

---

# 6. Common Project Structure

Shared code is stored in:

```
/USBWatcher.Common
    Logger.cs
    EventIds.cs
```

Referenced by:
- Agent
- Sync
- Installer

---

# 7. Operational Flow

## Logon
1. User logs in
2. HKLM Run triggers Agent --init
3. Agent initializes and monitors USB

## Device Workflow
1. USB inserted
2. Agent detects device
3. User unlocks
4. Sync executes
5. Files copied/extracted
6. Status reported

---

# 8. Exit Codes

| Code | Meaning |
|------|--------|
| 0 | Success |
| 1 | Failure |
| 2 | Invalid usage |

---

# 9. Deployment Notes

For Intune:
- Run as SYSTEM
- Use:
  - install → install/upgrade
  - uninstall → removal
- Detection via:
  - file presence
  - registry (Run key)

---

This document reflects the current USBWatcher architecture, including upgrade-safe config handling, centralized logging, and shared component design.
