# Home RMS — consent-based remote administration

A small, self-hosted remote-management system for **machines you own or administer with the user's knowledge** on your home LAN. It has two parts written in .NET 9 / C#:

- **Agent** — a lightweight tray application that runs in the signed-in user's session. It hosts a gRPC endpoint over mutual TLS and exposes inventory, a remote shell, remote-desktop streaming, and input injection.
- **Controller** — a WPF dashboard on your admin PC that connects to an agent and drives those three modules.

![architecture](docs/architecture.svg)

> ### Why this is consent-based, not covert
> This project deliberately does **not** implement a hidden/silent surveillance agent. Monitoring a computer without the user's knowledge is stalkerware and is illegal in most places, even on your own network. Instead:
> - The agent runs as the **normal interactive user**, not as a hidden SYSTEM service.
> - A **tray icon is always visible**, and a full **activity log** records every connection, inventory pull, and shell command.
> - Remote desktop / input require an **explicit on-machine “Allow”**, and while anyone is viewing, a **red “your screen is being viewed” banner** stays on screen with a one-click **Stop**.
> - There is **no firewall evasion and no reverse tunnel** — the agent simply listens on the LAN on a port you open.
> - Grants are **time-boxed** (1 hour) and can be revoked instantly from the tray.
>
> Use it on your own machines, or on a family member's machine **with them present and aware**.

---

## Architecture

```
HomeRms.sln
├── src/HomeRms.Shared       # gRPC contract (rms.proto) + mutual-TLS PKI helpers
├── src/HomeRms.Agent        # tray app: Kestrel+gRPC host, consent, capture, input, inventory, shell
├── src/HomeRms.Controller   # WPF dashboard: inventory / shell / remote-desktop tabs
├── tools/New-DevCerts.ps1   # one-time certificate generation for a deployment
└── tests/HomeRms.LoopbackTest  # automated end-to-end smoke test
```

### Secure authentication (mutual TLS, not an embedded PSK)

Instead of baking a pre-shared key into the agent build, each deployment has its own tiny PKI:

| Certificate | Holder | Purpose |
|---|---|---|
| `homerms-ca` | root of trust | signs the two leaf certs; shared (public part) by both sides |
| `homerms-agent` | agent | server cert (serverAuth EKU) |
| `homerms-controller` | controller | client cert (clientAuth EKU) |

The agent **requires** a client certificate and accepts the connection only if it chains to your CA; the controller validates the agent's certificate the same way. The controller's certificate common name (`homerms-controller`) is surfaced to the user in the consent prompt as the operator identity. Nothing authenticates with a password, and no secret travels in the clear.

### Transport

gRPC over HTTP/2 + TLS. Both ends use `SocketsHttpHandler` / Kestrel so ALPN negotiates HTTP/2 cleanly. Default port **7443** (override with `--port` on the agent).

---

## Build

Requires the **.NET 9 SDK** on Windows.

```bash
dotnet build HomeRms.sln -c Release
```

Three projects build: `HomeRms.Shared`, `HomeRms.Agent` (→ `HomeRmsAgent.exe`), `HomeRms.Controller` (→ `HomeRmsController.exe`).

## Set up certificates (once per deployment)

```powershell
# Pick a strong password; the same value must be set as HOMERMS_CERT_PASSWORD
# on every machine (agent + controller).
./tools/New-DevCerts.ps1 -Password 'choose-a-strong-pass'
```

This writes `homerms-ca.pfx`, `homerms-agent.pfx`, and `homerms-controller.pfx` to
`%ProgramData%\HomeRMS\certs`. Copy that folder to each agent machine and to the controller.

> For quick local testing you can skip this step — the agent auto-generates the certs on first run using the default dev password.

## Run

**On each target machine (with the user's knowledge):**

```powershell
$env:HOMERMS_CERT_PASSWORD = 'choose-a-strong-pass'
HomeRmsAgent.exe            # a tray icon appears; double-click it for the activity log
```

Open inbound TCP **7443** on that machine's firewall for your LAN subnet. To start the agent automatically at logon, place a shortcut in `shell:startup` (a visible, user-controllable autostart — not a hidden service).

**On your admin PC:**

```powershell
$env:HOMERMS_CERT_PASSWORD = 'choose-a-strong-pass'
HomeRmsController.exe
```

Enter the agent's hostname or LAN IP + port, click **Connect**, then use the tabs:

- **System Inventory** — installed apps (registry), running processes, hardware specs.
- **Remote Shell** — interactive PowerShell; each command is echoed into the agent's visible log.
- **Remote Desktop** — click **Start viewing** (the user is prompted on the target). Tick **Send mouse / keyboard** to control.

---

## Automated loopback test

`tests/HomeRms.LoopbackTest` starts the agent's gRPC host in-process and connects a mutual-TLS client to verify the full pipe:

```bash
dotnet run --project tests/HomeRms.LoopbackTest
```

It asserts: agent info, inventory (apps/processes/hardware), a round-tripped shell command, **and** that screen streaming is denied without a consent grant. Exit code `0` = pass. Example run:

```
[test] GetAgentInfo OK -> DOR-WORKSTATION / Dorp / 1920x1080
[test] GetInventory OK -> 126 apps, 302 processes, CPU='Intel(R) Core(TM) i7-14700KF', RAM=63GB, disks=3
[test] Shell OK -> received marker
[test] Consent gate OK -> screen denied without a grant
==== LOOPBACK TEST PASSED ====
```

---

## Scope & roadmap

Implemented: mutual-TLS transport, inventory, remote shell, primary-screen streaming, input injection, consent + visible banner + activity log.

Not yet implemented (natural next steps):
- Multi-agent dashboard with LAN discovery (today the controller connects to one agent at a time).
- Multi-monitor capture and selection.
- Signed MSI installers for agent and controller.
- H.264 frame encoding for lower bandwidth (currently JPEG frames).

## License

For personal/home use on machines you own or administer with consent. Do not use this software to monitor anyone without their knowledge.
