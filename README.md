<p align="center">
  <img src="docs/logo.svg" width="108" height="108" alt="Home RMS Logo" />
</p>

<h1 align="center">Home RMS — Consent-Based LAN Remote Management System (.NET 9)</h1>

<p align="center">
  A modern, self-hosted <b>Remote Management System (RMS)</b> built in <b>.NET 9 / C#</b> for administering computers on your home LAN with <b>Mutual TLS (Zero-Prompt PKI)</b> authentication, <b>explicit on-device user consent</b>, and <b>full audit logging</b>.
</p>

![Architecture](docs/architecture.svg)

---

## Visual Tour & Screenshots

### 1. Controller Console — System Inventory & Hardware Dashboard
Displays saved home computers with live online/offline status probing, hardware summary cards (CPU, RAM, GPU & Display, Storage Volumes), real-time search filtering, installed applications (from the Windows Registry), and active processes sorted by memory usage.

![Controller — System Inventory](docs/screenshots/controller-inventory.png)

### 2. Controller Console — Live Remote Desktop & Input Control
Streams the remote desktop over gRPC/HTTP2 with configurable target FPS and JPEG quality, letterboxing-aware coordinate mapping, and live mouse/keyboard injection via Win32 `SendInput`.

![Controller — Remote Desktop](docs/screenshots/controller-desktop.png)

### 3. Controller Console — Interactive Remote PowerShell
Full bidirectional PowerShell session over Mutual TLS with one-click diagnostic quick-commands (`whoami & hostname`, `IP Configuration`, `Top CPU Processes`, `Disk Volumes`, `Uptime`) and command history (`Up`/`Down` arrows).

![Controller — Remote PowerShell](docs/screenshots/controller-shell.png)

### 4. Client Agent — Endpoint Monitor & Live Activity Audit Log
Runs in the signed-in user's interactive session with a system tray icon, status cards, active session indicator, one-click **Stop Session** revocation button, and a live timestamped audit log of every remote action.

![Agent — Endpoint Monitor](docs/screenshots/agent-status.png)

---

## Key Features & Architecture

```text
HomeRms.sln
├── src/HomeRms.Shared          # gRPC Protobuf contract (rms.proto) + Mutual-TLS PKI generator & loader
├── src/HomeRms.Agent           # Windows-only WinForms tray & endpoint monitor hosting Kestrel gRPC server over mTLS
├── src/HomeRms.Controller      # Windows dark-themed WPF console with multi-device sidebar & 3 modules
├── src/HomeRms.Controller.Mac  # macOS native Controller app (Avalonia UI, Apple Silicon arm64 & Intel x64)
├── tools/New-DevCerts.ps1      # One-time deployment PKI certificate generator (CA + Agent + Controller)
├── tools/Package-MacController.ps1 # Packages HomeRmsController.app bundles (.icns + Info.plist) for macOS
└── tests/HomeRms.LoopbackTest  # Automated end-to-end integration test suite (info, inventory, shell, consent, screen)
```

### 1. Zero-Prompt Mutual-TLS (mTLS) Authentication
Instead of typing passwords or hardcoding a plain pre-shared key into the binary, each deployment uses a dedicated 3-certificate PKI stored in `%ProgramData%\HomeRMS\certs`:

| Certificate | File | Role & EKU |
|---|---|---|
| **Root CA** | `homerms-ca.pfx` | Self-signed deployment Certificate Authority trusted by both sides |
| **Agent Server Cert** | `homerms-agent.pfx` | Server certificate (`serverAuth` EKU + `localhost` / LAN SANs) |
| **Controller Client Cert** | `homerms-controller.pfx` | Client certificate (`clientAuth` EKU) presented automatically by the Controller |

Both Kestrel (on the Agent) and `SocketsHttpHandler` (on the Controller) validate certificates against your deployment's custom `homerms-ca` root via `X509ChainTrustMode.CustomRootTrust`. Connections without a valid certificate signed by your CA are rejected at the TLS handshake layer.

### 2. Consent-First Security Model
Unlike covert surveillance tools, **Home RMS** is designed around transparency and user consent:
- **Interactive User Session:** Runs as a visible application in the signed-in user's desktop session with a system tray icon.
- **Explicit On-Screen Prompt:** Starting a Remote Desktop stream triggers a modal **Allow / Deny** dialog on the remote computer showing the operator's certificate identity (`homerms-controller`).
- **Persistent Red Viewing Banner:** Whenever a screen stream is active, an always-on-top red banner is displayed at the top of the remote screen with a one-click **Stop** button.
- **Time-Boxed Grants & Instant Revocation:** Consent grants expire automatically after 1 hour and can be revoked at any moment from the banner, the Agent window, or the system tray menu.
- **Full Audit Log:** Every connection, inventory query, PowerShell command, and screen session is recorded with timestamps in the Agent's `LIVE AUDIT & ACTIVITY LOG`.

### 3. Core Modules
- **System Inventory (`InventoryCollector.cs`):**
  - Reads `HKLM` (64-bit and `WOW6432Node`) and `HKCU` uninstall registry hives, deduplicating and sorting installed applications.
  - Enumerates all running processes and their working-set RAM consumption.
  - Queries WMI (`Win32_Processor`, `Win32_ComputerSystem`, `Win32_VideoController`) and `DriveInfo` for CPU model, logical cores, physical RAM, GPU adapter, and fixed disk capacity/free space.
- **Remote PowerShell (`RemoteManagementService.Shell`):**
  - Spawns an isolated, hidden `powershell.exe` process connected to a gRPC bidirectional stream (`AsyncDuplexStreamingCall`), streaming `stdout` and `stderr` in real time.
- **Remote Desktop & Input (`ScreenCapturer.cs` & `InputInjector.cs`):**
  - Captures the primary display at physical DPI resolution using Win32 GDI `BitBlt` (`SRCCOPY | CAPTUREBLT`), overlays the live cursor position via `GetCursorInfo` + `DrawIcon`, and encodes frames as JPEG.
  - Maps mouse movement, clicks (`Left`, `Right`, `Middle`), scroll wheel deltas, and keyboard virtual-key codes from the Controller's letterboxed viewport to normalized `0..65535` absolute coordinates injected via Win32 `SendInput`.

---

## Building from Source

Requires the **.NET 9 SDK** on Windows 10/11.

```powershell
# Compile the entire solution in Release mode
dotnet build HomeRms.sln -c Release

# Publish standalone single-file Windows Release executables into ./dist
dotnet publish src/HomeRms.Agent/HomeRms.Agent.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
dotnet publish src/HomeRms.Controller/HomeRms.Controller.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# Package self-contained macOS Controller .app bundles (Apple Silicon arm64 & Intel x64) into ./dist
./tools/Package-MacController.ps1
```

---

## Deployment & Usage Guide

### Step 1: Generate Deployment Certificates (Once per Home Network)
Run the included PowerShell script to generate `homerms-ca.pfx`, `homerms-agent.pfx`, and `homerms-controller.pfx` in `%ProgramData%\HomeRMS\certs`:

```powershell
./tools/New-DevCerts.ps1 -Password 'choose-a-strong-pass'
```

Copy `%ProgramData%\HomeRMS\certs` to each target PC on your home network and set `HOMERMS_CERT_PASSWORD` to match. *(For local single-machine testing, both the Agent and Controller automatically generate dev certificates on first launch if none exist.)*

### Step 2: Run the Agent on Target PCs
```powershell
$env:HOMERMS_CERT_PASSWORD = 'choose-a-strong-pass'
./dist/HomeRmsAgent.exe --port 7443
```
To allow incoming connections from your LAN, open TCP port `7443` for the `Private` network profile:
```powershell
New-NetFirewallRule -DisplayName "Home RMS Agent (mTLS)" -Direction Inbound -Protocol TCP -LocalPort 7443 -Profile Private -Action Allow
```

### Step 3: Launch the Controller Console (Windows or macOS)
**On Windows:**
```powershell
./dist/HomeRmsController.exe
```

**On macOS (Apple Silicon `arm64` or Intel `x64`):**
1. Extract `HomeRmsController-macOS-osx-arm64.zip` (or `osx-x64.zip`).
2. Double-click `Launch-HomeRMS-Mac.command` (or run `chmod +x HomeRmsController.app/Contents/MacOS/HomeRmsController && xattr -cr HomeRmsController.app && open HomeRmsController.app`).
3. Add your Windows computers in the **Add / Edit Endpoint** card on the bottom-left (or click **↻ Check Status** to auto-discover Windows Agents on your LAN).

---

## Automated Loopback Test Suite

`tests/HomeRms.LoopbackTest` boots the Agent's Kestrel + gRPC server in-process on port `7444` and executes an automated 5-stage verification over Mutual TLS:

1. **`GetAgentInfo`** — verifies mTLS handshake, machine name, user identity, and physical display dimensions.
2. **`GetInventory`** — verifies registry application enumeration, process list, CPU, RAM, GPU, and disk volumes.
3. **`Shell`** — opens a bidirectional PowerShell stream and verifies command execution round-trip.
4. **Consent Gate (Negative Test)** — verifies `StreamScreen` is rejected with `StatusCode.PermissionDenied` when no consent grant exists.
5. **Live Screen Stream (Positive Test)** — attaches an approved consent handler and verifies live JPEG frame delivery.

Run the test suite with:

```powershell
dotnet run --project tests/HomeRms.LoopbackTest -c Release
```

Example output:
```text
[test] host started on 7444

[test] GetAgentInfo OK -> DOR-WORKSTATION / Dorp / 3840x2160
[test] GetInventory OK -> 126 apps, 306 processes, CPU='Intel(R) Core(TM) i7-14700KF', RAM=63GB, disks=3
[test] Shell OK -> received marker
[test] Consent gate OK -> screen denied without a grant
[test] StreamScreen OK -> frame #1 (3840x2160, 235475 bytes JPEG)

==== LOOPBACK TEST PASSED ====
```

---

## License

For personal/home administration on machines you own or administer with explicit user consent.
