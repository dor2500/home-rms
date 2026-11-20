# Security notes

Known security properties of Home RMS, what was hardened on the `fix/ui-redesign-polish` branch
(after v2.1.0), and the issues that are documented but not fixed yet, with their mitigations.

## Fixed on this branch

| Area | Before | Now |
|---|---|---|
| Central Hub relay (`http://<pc>:7480`) | Plain HTTP on all interfaces. API, relay and reverse proxy answered without authentication unless console 2FA was on; `/proxy/` was never gated. Anyone on the LAN could run commands on paired agents through it. | Sign-in is **required by default**: the per-install hub access key (Server & PKI > *Hub relay access key*, DPAPI-protected, can be regenerated) or a controller TOTP / backup code. Sign-in can only be turned off explicitly (with a warning). |
| Hub CSRF | Any web page could POST to `/api/relay/.../shell` on localhost. | State-changing requests need the `X-HomeRms-Request: 1` header (a cross-site form post cannot set it). Session cookies are 256-bit, `HttpOnly`, `SameSite=Strict`. |
| Hub open relay / proxy | `/api/relay/{host}:{port}` and `/proxy/{host}` accepted any host and lent the controller's mTLS identity / disabled TLS validation for it; the proxy followed redirects. | Only saved endpoints can be relayed to; the proxy only targets a saved endpoint's web UI URL, does not follow redirects, and tolerates invalid TLS certificates only for those hosts. |
| Hub page XSS | Endpoint names and alert text were inserted into HTML unescaped, and each endpoint was inlined as JSON into an `onclick` attribute. | All values are HTML-escaped; endpoints are selected by index. |
| WinRM credentials | The password sat in a single-quoted here-string (`''` is not an escape there): passwords with `'` never worked and a line starting with `'@` could inject code. The script body was pasted inside `{ }` of a *local* script block, so an unbalanced brace ran the rest on the controller. | User, password, host and body travel as Base64 and are decoded in PowerShell; the body becomes a script block via `[ScriptBlock]::Create`. All generated scripts are parsed with PowerShell's parser in `tests/HomeRms.ControllerChecks`. |
| Pin store | An unreadable `trusted-controllers.json` read as "nothing pinned", which reopened first-come pairing. | Fails closed (every controller refused, logged by the agent, resettable from the agent tray menu). Writes are atomic. |
| File manager protection | `C:\PROGRA~1`, `C:\Windows ` (trailing space/dot) bypassed delete/rename protection; `C:\Users` was not protected when the agent runs as LocalSystem. | Short names expanded, trailing dots/spaces ignored, `C:\Users` derived from `%PUBLIC%`, whole user profiles protected. |
| Lock screen | The detached "Screen 2" window kept streaming next to the lock screen; dialogs stayed usable; the translucent scrim left remote screens readable; a pending 2FA pairing prompt hung forever. | All closed / hidden / cancelled on lock. |
| Expired agent certificates | Accepted silently (`IgnoreNotTimeValid`). | Still accepted (home PCs with wrong clocks), but a one-time warning per endpoint is logged in the live console. |

### Behaviour change: hub relay sign-in

Phones or browsers that used the hub page without signing in now see a sign-in box. Enter the
hub access key from **Server & PKI** in the controller (or a controller 2FA / backup code); the
session is remembered for the configured "remember device" period. Scripts calling the hub API
must sign in via `POST /api/auth/verify` (JSON `{"code": "<key>"}`) with the header
`X-HomeRms-Request: 1`, keep the `hrms_session` cookie, and send the header on every POST.
To restore the old behaviour (not recommended) untick *Require sign-in for the hub relay*.
Settings live in `%AppData%\HomeRMS\hub-relay.json` (`AllowLan: false` binds to localhost only).

## Known issues (documented, not fixed)

### Hub relay is plain HTTP
Sign-in protects access, but the access key, session cookie, shell commands and screen images
cross the LAN unencrypted. **Mitigation:** use the hub only on a trusted home network (or set
`AllowLan` to `false` and reach it through an SSH tunnel / VPN). A TLS listener needs a
certificate the phone trusts, which the current PKI cannot provide without user setup.

### Embedded CA private key with a default password
`HomeRms.Shared/Certs/homerms-ca.pfx` (including its private key) ships inside every binary, and
`RmsPaths.CertPassword` defaults to `homerms-dev`. Anyone with a copy of Home RMS can mint
certificates that chain to this CA. **What still protects you:** agents pin the controller's own
unique certificate (SHA-256 fingerprint) after the first pairing, and that pairing requires
on-device consent or the agent's TOTP code; once pinned, a CA-signed certificate alone is
rejected at the TLS layer. The controller, however, trusts any agent certificate signed by the CA,
so on a hostile LAN an attacker could impersonate an agent (DNS / ARP spoofing) and see the
commands and files you send to it. **Mitigation:** pair agents on a trusted network, set
`HOMERMS_CERT_PASSWORD`, and generate a private CA (`DevCertificates.EnsureDeploymentCertificates`
into an empty cert directory) for installations that leave the home LAN. A proper fix (per-install
CA generated at first run, agent certificate pinning on the controller) changes the deployment
model and was left for a release that can migrate existing installs.

### Updater checksums come from the same release
Agent and controller updaters verify `checksums.sha256`, but that file is downloaded from the same
GitHub release as the binary, so it only protects against corrupted downloads, not against a
compromised release or GitHub account. The WinRM "install agent remotely" script downloads the
latest release without any check. **Mitigation:** keep the GitHub account protected by 2FA; a real
fix is signing the release (Authenticode or a minisign/ed25519 signature verified against a key
embedded in the binaries).

### Agent install over WinRM skips certificate checks
`New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck` is used for WinRM so that
self-signed WinRM HTTPS listeners work. Over plain WinRM (HTTP 5985) Kerberos / NTLM still
authenticates and encrypts the session, but use WinRM only inside the trusted LAN.

### Pairing brute force is rate limited per IP only
`PairWith2Fa` locks a peer address out after 5 failures in 5 minutes. An attacker controlling many
addresses could still try more codes. The 6-digit TOTP rotates every 30 s, so success needs a lot of
addresses; pair agents promptly after installing them.
