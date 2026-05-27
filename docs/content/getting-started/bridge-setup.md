# Bridge Setup (Windows)

The bridge service is a PowerShell REST API that runs natively on the Windows machine with TwinCAT XAE installed. It is the only Windows-specific component of TcKit.

## Requirements

- Windows 10/11
- TwinCAT 3.1 Build 4026 installed
- PowerShell 5.1+
- **TcXaeMgmt PowerShell module** (see below)

## TcXaeMgmt PowerShell module

The bridge uses Beckhoff's official [TcXaeMgmt](https://www.powershellgallery.com/packages/TcXaeMgmt) module for ADS operations (runtime mode transitions and live PLC symbol reads). Signed by Beckhoff, no transitive dependencies, supports PowerShell 5.1+.

Three install paths, in order of operator-effort:

1. **PowerShell Gallery (recommended)** — one command, no admin needed:
   ```powershell
   Install-Module -Name TcXaeMgmt -Scope CurrentUser -Force
   ```
   Or just run `tckit doctor` — if the module is missing, the doctor prompts to install it for you.

2. **Already bundled with TwinCAT** — when the ADS API option was included in your TwinCAT installer, the module ships at `C:\TwinCAT\AdsApi\PowerShell\TcXaeMgmt`. Add `C:\TwinCAT\AdsApi\PowerShell\` to `$env:PSModulePath` to make it discoverable. Per [Beckhoff's manual-install docs](https://infosys.beckhoff.com/content/1033/tc3_ads_ps_tcxaemgmt/5531475467.html).

3. **Offline / firewalled environments** — download the [TE1000 archive](https://www.beckhoff.com/en-en/download/628465946) and unpack onto a path on `$env:PSModulePath`.

The bridge requires `>= 6.0` to cover both TwinCAT 4024 and 4026 sites. `tckit doctor` reports the installed version after the bridge is up.

## Starting the bridge

If you installed via the plugin or pip, run `tckit bridge install` once to copy the bundled bridge to `~/.tckit/bridge/`, then start it from there:

```powershell
~/.tckit/bridge/Start-Bridge.ps1
```

Contributors working from a repo checkout can run the source copy directly so edits to bridge code take effect without reinstalling:

```powershell
.\bridge\Start-Bridge.ps1
```

The bridge listens on `http://localhost:8765` by default.

To use a different port:

```powershell
~/.tckit/bridge/Start-Bridge.ps1 -Port 9000
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
