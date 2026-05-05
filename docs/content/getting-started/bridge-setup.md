# Bridge Setup (Windows)

The bridge service is a PowerShell REST API that runs natively on the Windows machine with TwinCAT XAE installed. It is the only Windows-specific component of TcKit.

## Requirements

- Windows 10/11
- TwinCAT 3.1 Build 4026 installed
- PowerShell 5.1+

## Starting the bridge

Open PowerShell as Administrator in the TcKit directory:

```powershell
.\bridge\Start-Bridge.ps1
```

The bridge listens on `http://localhost:8765` by default.

To use a different port:

```powershell
.\bridge\Start-Bridge.ps1 -Port 9000
```

## Verify the bridge is running

```powershell
Invoke-RestMethod http://localhost:8765/health
# Expected: @{status=ok; version=0.1.0}
```

Or from another machine:

```bash
curl http://<windows-ip>:8765/health
```

## Firewall

If the Docker container is on a different machine (or a VM), allow inbound connections on port 8765 in Windows Firewall:

```powershell
New-NetFirewallRule -DisplayName "TcKit Bridge" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow
```

## XAE modes

| Mode | When to use |
|------|-------------|
| `attach` | XAE is already open — bridge attaches via `GetActiveObject("TcXaeShell.DTE.17.0")` |
| `headless` | CI only — bridge spawns an invisible XAE instance |

Set `XAE_MODE` in your `.env` file.

!!! warning "COM version"
    TwinCAT 4026 uses `TcXaeShell.DTE.17.0`. If you are on a different build, verify the COM version string manually before running any scripts.
