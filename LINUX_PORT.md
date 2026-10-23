# Ubuntu port — work in progress

Base: `cfea13e69e8a3f6855656a28a4ed04d5de2247fe`, branch `port/ubuntu-linux`.
The Windows client and its CI remain separate. This is not a claim of full parity.

## Revision linux0.2 — services removed by user choice

The Linux client no longer exposes the Services tab, page, dialogs or service
RPCs. It does not initialize managed services, run their autostart, inspect old
packages or check/download their updates. `services.js` is excluded from the
Linux package. Imported/saved service preferences and previously downloaded
packages are left untouched for portable-backup compatibility. Existing clients
must be fully closed before upgrading so any already-running owned service stops.
Windows retains its Services UI and runtime unchanged in behavior; the shared UI
defaults to enabling services unless the Linux host explicitly opts out.
`Linux/TelegramService.cs` and its standalone tests remain as inactive development
code, not an application feature. Earlier lifecycle evidence below is historical.

## Architecture / function matrix

| Windows implementation | Linux implementation / intended semantics |
|---|---|
| .NET 10 Core | Same Core project, same import/security/backup validators |
| WinForms + WebView2, Windows/ui | GTK 3 + WebKitGTK 4.1 / PyGObject, shared UI assets |
| chrome.webview | Main-frame-only WebKit bridge, private inherited pipes; no HTTP listener |
| NativeCore / Windows jobs | Owned process sessions, bounded waits, stdout/stderr drained |
| Xray + Wintun | Same pinned Xray; native /dev/net/tun FD, no sing-box |
| elevated GUI, WFP | Unprivileged GUI; polkit-authenticated fixed helper, dedicated core UID, own nft table |
| WinINet proxy | GNOME GSettings, per-key compare-and-restore journal |
| TunnelDns / PhysicalNetwork | resolved per-link DNS on owned TUN, policy routes; physical main table unchanged |
| DPAPI Store / DeviceIdentity | AES-GCM profile with a random key in Secret Service; no plaintext fallback |
| Task Scheduler / mutex | XDG autostart / single-instance GApplication D-Bus activation |
| NotifyIcon | Optional AppIndicator; normal visible window without a tray host |
| subscriptions, favorites, auto, routing | Core models and real Xray validation/probes |
| statistics, history, diagnostics | Core metrics and usage archive; OS-specific tests required |
| QR, clipboard, backups | ZXing Core + GTK clipboard/dialogs, same password-encrypted .kotbackup |
| .exe application rules | Not equivalent on Linux; reject rather than silently ignore; Linux app routing remains work |
| winws / WinDivert / BAT | Not installable on Linux. Linux zapret nfqws/tpws require separate strategies |
| TgWsProxy_windows.exe | Removed from the Linux client in linux0.2; dormant settings retained |
| signed Setup updates | Windows updater must not run on Linux; Linux signed update channel remains work |

## Shell decision and trust boundary

PyGObject GTK3 and WebKit2 4.1 are distribution-supported on Ubuntu 24.04;
26.04 remains a required test target, not an inferred success. This avoids relying
on a Windows-only WebView package. Python is only the OS shell; portable business
logic remains .NET. Resources are bundled, navigation and subframes are denied,
the bridge is injected only in the main frame at the exact local application URI.
Backend requests are JSON objects over inherited pipes, never shell command text.

## Network policy

The helper may only run the bundled Xray and perform fixed network operations.
It is installed root-owned and is entered through pkexec, with admin authentication.
The core drops to a dedicated system UID; only that UID bypasses the TUN policy
route. A passed TUN FD avoids giving the core CAP_NET_ADMIN. No GUI runs as root.
NetworkManager's physical profiles and the main default route are not edited.
DNS is assigned only to the newly owned link; deletion removes those settings.

Kill switch policy: manual disconnect and normal quit release it; core/GUI/helper
failure preserves the kernel table. Reconnect must replace rules atomically, never
delete then add. Explicit recovery removes only kot's table. Reboot persistence
is NOT IMPLEMENTED: the nft table is volatile, so early boot is not protected.
Do not describe this as a boot-persistent kill switch. UFW can additionally deny
traffic; kot does not change UFW or flush any foreign rules.

## Data migration

Use Windows → password-protected `.kotbackup` export → Linux import. DPAPI files
cannot be decrypted on Linux. Keep backups as secrets. XDG data/config/cache/runtime
directories are private (0700), secret files 0600, replaced atomically. If Secret
Service is unavailable/locked, report an error and leave old encrypted data intact.
No publisher private key is required for local unsigned .deb builds. Such builds
are not publisher-signed updates.

Implementation and actual evidence are tracked in `LINUX_TEST_REPORT.md`.

## Actual feature state and remaining work

This branch produces `artifacts/linux/kot_0.8.0~linux0.2_amd64.deb`, an **incomplete
development preview**, not a finished Ubuntu VPN product. The installer and
privileged end-to-end path have not been exercised on clean target systems.

Implemented: shared Russian pages/themes/icons; native main-frame bridge;
subscription/local link/Clash/full Xray import with real native validation;
selection/favorites/manual latency/initial Auto; connection cancellation and
30-second proxy-only health/reconnect; GNOME proxy journal; aggregate metrics
and persistent usage; routing/domain/IP/GeoIP settings; encrypted portable
backup; QR encode/decode and GTK clipboard/dialog adapters; XDG autostart;
GApplication single instance; optional AppIndicator with a taskbar fallback.
Background refresh currently covers the active subscription, remote routing
profiles and GeoIP. Background scheduling/network-change behavior is not yet
validated in a desktop soak test. Periodic whole-list ping and re-selection of
Auto are NOT IMPLEMENTED; changing those scheduling options returns an explicit
error instead of pretending to apply them.

### Linux process routing

Upstream DOES have `common/net/find_process_linux.go`: it looks up sockets in
`/proc/net/{tcp,udp,tcp6,udp6}`, then scans `/proc/<pid>/fd` and `exe`.
`app/router/condition.go` supports executable names/paths and strips `.exe`.
That is not a safe equivalent of Windows rules here: the isolated `kot-core` UID
cannot read another user's process FDs under Ubuntu's proc/ptrace restrictions.
Do not grant CAP_SYS_PTRACE merely to make this lookup work. Both manual .exe
rules and provider process rules are explicitly rejected on Linux. A tested
cgroup/launcher-based Linux policy remains TODO, including UDP/IPv6 semantics.

### Additional services — historical linux0.1 implementation, removed in linux0.2

* Telegram: official Linux ELF, same upstream settings, verified download size
  and SHA-256, manual update/restart/rollback, autostart and update checks.
  v1.11.2 was actually installed/launched/stopped in an isolated data directory.
  Its loopback port was verified. Real Telegram account/media/call/DC behavior
  is **NOT_TESTED**. Upstream can still show its own tray icon.
* Upstream writes an MTProto link to `proxy.log`. A regular file would leak the
  secret, as an initial test demonstrated. kot supplies a 0600 FIFO and consumes
  it into a bounded, redacted memory journal instead. The runtime config still
  needs a plaintext secret for upstream: private 0700 directory/0600 config,
  deleted on stop and next startup. After power loss it can remain on disk until
  cleanup. Never claim that file is encrypted while the service runs.
* zapret: Flowseal's BAT/WinDivert package is Windows-only. `bol-van/zapret`
  documents Linux nfqws (NFQUEUE) and tpws; it is now EOL in favor of
  `bol-van/zapret2`. A separately pinned Linux package, restricted queue/helper
  policy and Linux-native strategies/testing are **NOT IMPLEMENTED**. The UI
  explicitly reports this, rather than downloading/running Windows binaries.

### Remaining product/security acceptance work

* Clean Ubuntu 24.04 and 26.04 installation, upgrade, reinstall, removal and
  installed-app GUI → polkit → dedicated UID → TUN → resolved verification.
* DNS leakage/system resolver behavior, NetworkManager changes, sleep/resume,
  disappearing interfaces, UFW coexistence, power failure and long-running use.
* Boot-persistent kill switch, startup races, packet-level incoming/forwarded
  traffic coverage. Current guard covers local input/output, not router forwarding.
* Linux application routing, zapret, periodic Auto/ping, all-subscription
  background refresh and detailed per-application statistics attribution.
* Native recovery UI for damaged profiles (data is preserved and startup fails
  explicitly today), automated window recreation retry limits, full native QR,
  clipboard, backup-dialog and tray-host acceptance tests.
* Publisher-signed Linux update channel and updater transaction/rollback.
  Windows `ReleasePackage` and Setup validation remain Windows-specific by design.

Linux TUN deliberately rejects system/localhost and hostname-bootstrap DNS
servers in full provider configs, preventing resolver recursion after takeover.
IP-based DNS remains supported; such provider configs can instead use proxy mode.
The helper uses only table 47811, rule priorities 10780–10782, its recorded
interface index, and the ownership-labelled `inet kot_guard` table. It refuses
occupied/foreign resources. Do not manually reuse those names while kot is active.
