# Linux test report

Latest routing/security audit and linux0.3 validation: [docs/LINUX_AUDIT.md](docs/LINUX_AUDIT.md).
The older results below describe the original preview.

Status: DEVELOPMENT PREVIEW — not a completed/full Linux port.

## linux0.2 change: remove Services in Linux only

The tab/page/dialogs and service JS module are absent from the Linux UI. The
backend rejects service RPCs, no longer initializes/starts/updates extra services
and does not send service models to the UI. Saved preferences and old downloaded
packages are preserved. Windows defaults still enable services.

Regression checks for this revision: native GTK tests on Wayland and X11
(XWayland), real backend with an imported backup containing autostart/update
flags and a corrupt old service manifest, existing Windows services browser
tests, portable tests and Windows cross-build. Results are stored separately in
`artifacts/linux/evidence/services-removed/`. Old Telegram lifecycle results below
describe linux0.1 development work, not an enabled linux0.2 feature.

Native Wayland/XWayland tests and actual backend removal tests: PASS. They verify
that service UI elements and `KotServices` are absent, stale service navigation
is ignored, every tested service RPC is rejected, old package files are unchanged,
and a portable export preserves the imported service autostart/update preferences.
The module `ui/services.js` is not in the linux0.2 package. Services are disabled
in application code, not merely hidden with CSS. No user profiles/packages are
deleted as part of this change.

Existing portable suite, actual pinned-Xray integration, Windows cross-build,
`Tests/services_ui.py` (Windows default still enabled) and `Tests/ui.py`: PASS
for this revision. System installation/upgrade on clean Ubuntu remains NOT_TESTED;
this change does not alter the broader VPN acceptance limitations below.

Host: Zorin OS 18.1, Ubuntu noble base, x86_64, GNOME Wayland.
No host root access; working network must not be modified by tests.

| Check | Result |
|---|---|
| Original `python3 Tests/repository.py` | PASS (552 source files, 0.8.0) |
| Original portable .NET suite | PASS; rerun after shared API changes |
| Pinned Linux Xray build | PASS, Go 1.27.2, verified source SHA-256 |
| Real Xray integration suite | PASS (VLESS/XHTTP, provider routing, DNS, metrics/auth) |
| Native GTK/WebKitGTK GUI from package staging directory | PASS on GdkWaylandDisplay and GdkX11Display (XWayland); real .NET backend/Core/Xray, theme, DOM, denied external navigation/forged token, second-instance activation |
| Installed native Linux GUI | NOT_TESTED (staging/extraction is not installation) |
| Clean Ubuntu 24.04 / 26.04, deb upgrade/remove | NOT_TESTED |
| New Linux backend + local VLESS server / real traffic / metrics | PASS |
| Native TUN FD + real VLESS TCP and UDP, IPv4 and IPv6, isolated netns | PASS |
| Helper policy routes / nft IPv4+IPv6 output drop counters / own-table cleanup | PASS in isolated netns |
| systemd-resolved / NetworkManager integration | NOT_TESTED; resolvectl is intentionally stubbed in namespace test to protect host D-Bus |
| Full polkit/helper dedicated-UID lifecycle | NOT_TESTED |
| GNOME proxy, actual GSettings apply/restore | PASS on isolated D-Bus/dconf with private XDG paths; host proxy unchanged |
| AES-GCM profile, wrong key, atomic previous profile, unavailable Secret Service | PASS |
| Owned process cancellation/output bound/parent SIGKILL tree cleanup | PASS |
| Official Linux Telegram lifecycle and secret-safe FIFO logging | PASS v1.11.2; actual DC/media/calls NOT_TESTED |
| Existing Chromium UI suite + site checks | PASS; one transient subscriptions assertion passed on rerun |
| Windows cross-build | PASS, original Windows CI retained |
| Local .deb / deterministic rebuild | PASS, byte-identical repeated local builds; hashes in `artifacts/linux/evidence/reproducibility.json` |
| Windows native runtime / WFP / services | NOT_TESTED (Linux host) |

## Commands executed

```sh
python3 Tests/repository.py
dotnet run --project Tests/Kot.Tests.csproj -c Release
python3 Release/xray-core.py artifacts/core --target linux
python3 Tests/xray_integration.py --core artifacts/core/xray
dotnet run --project Tests/Kot.Tests.csproj -c Release --no-build -- geoip-core artifacts/core/xray
dotnet build Windows/Kot.Windows.csproj -c Release
dotnet run --project LinuxTests/Kot.LinuxTests.csproj -c Release -- artifacts/core/xray --telegram
python3 LinuxTests/helper.py
python3 LinuxTests/security.py
python3 LinuxTests/process_tree.py
python3 LinuxTests/gnome.py
KOT_HOST_NETNS=$(readlink /proc/self/ns/net) unshare -Urnm python3 LinuxTests/network_namespace.py
python3 Linux/build.py
python3 LinuxTests/gui.py <deb-staging>/usr/lib/kot artifacts/linux/evidence
```

The actual host did not have git/.NET/Go/venv initially. git was extracted from an
Ubuntu .deb under `/tmp/opencode/git-local`; .NET 10.0.401 and Go 1.27.2 were
installed under `/tmp/opencode`; no system toolchain replacement or sudo install
was performed. A local Playwright 1.63.0 environment used its Ubuntu 24.04
Chromium fallback. `Tests/ui.py`, `flags_ui.py`, `subscriptions_ui.py`,
`routing_ui.py`, `statistics_ui.py`, `client_tools_ui.py`, `services_ui.py`,
`journal_ui.py`, `ui_controls.py`, `about_ui.py`, `ui_polish.py`,
`ui_resilience.py`, `site/check.py` and `site/demo_browser_check.py` passed.
Test-generated tracked screenshots were restored to the original repository
bytes, not included as application changes.

## Failures found and fixed during implementation

* Initial local VLESS fixture timed out: the pinned Xray intentionally blocks
  private destinations on VLESS server freedom by default. Fixed only the local
  fixture's `finalRules` allow-list, consistent with the original integration
  suite; no production TLS/security checks were disabled.
* Real dconf test initially used different XDG roots for daemon and clients.
  Fixed the isolation harness; no host GNOME settings were changed.
* Telegram upstream logs contained the generated secret. Added the FIFO/redaction
  boundary; real lifecycle/secret tests then passed. Old failed-fixture logs were
  scrubbed; test secrets are not published in artifacts.
* Normal disconnect initially missed the final statistics-generation event.
  Added explicit recording state and reconnect regression tests.
* Locked NuGet publish caught a RID mismatch after plain dotnet runs. Linux RID
  is now declared consistently in the project, rather than bypassing locked mode.
* Second-instance test initially blocked the primary GTK loop with a synchronous
  subprocess/D-Bus call. Moved the test caller to a worker; real activation passed
  without creating/replacing the backend, on both toolkit backends.
* A later repeated GitHub Telegram download hit its timeout. The earlier verified
  v1.11.2 package was reused with exact size/SHA-256 validation for the final offline
  lifecycle regression (including actual listener closure); no TLS/hash checks or
  timeouts were disabled. This does not assert that GitHub is always reachable.

## Acceptance boundaries

No claim of tested VPN protection on the desktop is made from compilation,
mocked HTML or netns fixtures alone. Namespace tests run in a fresh user+mount+
network namespace and assert it differs from the host. They use real `/dev/net/tun`,
Xray, iproute2 and nftables. They do NOT call host `resolvectl`, UFW or NetworkManager,
and they cannot exercise a dedicated second UID with the single-UID userns map.
No host firewall/default route/DNS experiments were performed.

Local install/upgrade/removal and Ubuntu 26.04 remain **NOT_TESTED**. The new
timeout-bounded Linux CI includes those checks and native GUI evidence collection,
but it has not been pushed/executed here. Windows runtime/WinForms/WFP tests remain
**NOT_TESTED** locally; only the cross-build, shared suite and unchanged Windows CI
are available evidence. See `LINUX_PORT.md` for the concrete unfinished functions.

Artifacts and sanitized evidence: `artifacts/linux/`; see `docs/LINUX.md` for
installation, recovery and removal commands. The .deb is a local unsigned preview,
not a verified publisher-signed update.

Ubuntu 26.04 archive metadata was checked over HTTPS: GTK3 3.24.52, WebKitGTK
4.1 typelib 2.52.0, Secret Service typelib 0.21.7, libicu78, libssl3t64 and pkexec
are present in resolute main. This supports the dependency choice, **not** a claim
of having installed or run the application on 26.04. A standalone Xorg login
session (as opposed to X11 clients under XWayland) is also **NOT_TESTED**.
