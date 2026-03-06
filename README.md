
# DataLockerWatcher

DataLockerWatcher is a Windows solution designed to detect the insertion of approved DataLocker USB devices, guide the user through unlock and sync workflows, and securely synchronize files to/from an approved network location.

The solution consists of **three components**:

1. **Installer** – SYSTEM-level installer responsible for setup  
2. **Agent** – User logon–triggered background process  
3. **Sync** – On-demand worker process that performs file synchronization  

This document describes **installation, configuration, and operational flow**.

---

# 1. Components Overview

## Installer
- Runs elevated (SYSTEM / Administrator)
- Installs binaries to:

C:\Program Files\DataLockerWatcher

Creates:

- Event Log source
- Start Menu shortcut (with AUMID for toast notifications)
- HKLM Run key so Agent auto-starts at logon

If someone is already logged in, the installer launches the Agent immediately.

The installer also handles:
- upgrades
- uninstall cleanup

---

## Agent

Runs in the **interactive user session**.

Responsibilities:

- Starts automatically at logon
- Registers a **WMI watcher** for USB insertion events
- Detects approved DataLocker devices
- Displays toast notifications guiding the user
- Launches the **Sync worker** when required

---

## Sync

The Sync component performs the actual synchronization.

Responsibilities:

- Locates the unlocked DataLocker USB drive
- Validates access to the configured source
- Synchronizes files to the USB device
- Reports status via toast notifications and logs

Sync supports **two operating modes**:

| Mode | Description |
|-----|-------------|
| 1 | Direct folder sync from a network share |
| 2 | Encrypted ZIP extraction followed by sync |

---

# 2. System Requirements

- Windows 10 1809 (build 17763) or later
- .NET 8 runtime (self-contained binaries provided by default)
- User must have access to the configured network share
- DataLocker USB device must be unlocked before sync

---

# 3. Installation Instructions

## 3.1 Build Artifacts

After building in **Release**, the binaries will be produced:

Installer\bin\Release\net8.0-windows10.0.17763.0\win-x64\Installer.exe  
Agent\bin\Release\net8.0-windows10.0.17763.0\win-x64\Agent.exe  
Sync\bin\Release\net8.0-windows10.0.17763.0\win-x64\Sync.exe  

---

## 3.2 Run the Installer

1. Copy all items in the Build folder to the target machine (install folder)
2. Run installer **as Administrator**

Installer performs the following:

- Creates installation directory

C:\Program Files\DataLockerWatcher

- Copies Agent and Sync binaries
- Creates Event Log sources
- Creates Start Menu shortcut with **AUMID**
- Sets **HKLM Run** key for Agent auto-start

No reboot required.

---

# 4. Configuration

## 4.1 config.json

The configuration file must exist beside **Agent.exe** and **Sync.exe**:

C:\Program Files\DataLockerWatcher\config.json

Example:

```json
{
  "Device": {
    "PnpDeviceIdContainsAny": [
      "VID_1234",
      "DATALOCKER"
    ]
  },
  "Unlock": {
    "ExeName": "SafeConsole.exe",
    "ToastOnDetected": true
  },
  "Sync": {
    "Source": "\\fileserver\secure-share\Data",
    "TargetFolder": "Data",
    "Mode": 1,
    "Key": ""
  }
}
```
