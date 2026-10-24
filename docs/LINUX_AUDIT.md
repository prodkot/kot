# Linux routing and security audit, 2026-10-24

Baseline: `port/ubuntu-linux`, commit `0f6e4ad`, Linux preview 0.8.0~linux0.2.
Updated package: **0.8.0~linux0.3**. This is still an incomplete preview.

## Changes, in verification order

1. Removed development-machine `/tmp/opencode` assumptions from tests. Each
   test now uses its own standard temporary directory.
2. Removed the main-table policy lookup that accepted every more-specific
   physical route before TUN. Only the dedicated core UID bypasses TUN;
   LAN/direct/proxy/block decisions stay in Xray. Recovery still removes the
   legacy rule. Regression coverage includes connected and explicit routes in
   both address families, plus real TCP/UDP over TUN in isolated CI.
3. A core crash, lost control pipe or invalid stop request is no longer reported
   as successful disconnection. The helper retains the firewall guard, exits
   unsuccessfully, and the client keeps its recovery marker. Failed cleanup
   disposes owned processes and returns the client to an actionable state.
   Connections without protection cannot silently clear a retained guard.
   Core startup closes the parent-death race and sets `NoNewPrivs` after UID drop.
4. Allowed only the two packaged `ext:geoip.dat:` / `ext:geosite.dat:` names;
   arbitrary external files remain forbidden. Previously valid routing entries
   using those assets were rejected by the helper.
5. Validate the entire GNOME proxy recovery receipt before changing a setting.
   A corrupt key can no longer cause partial restoration before rejection.
6. Rebuilt the unchanged, SHA-256-pinned Xray source with **Go 1.27.3**. The old
   Go 1.27.2 build had reachable `crypto/x509` vulnerability
   [GO-2026-6809](https://pkg.go.dev/vuln/GO-2026-6809). Updated all build workflows
   to the same compiler. An OpenPGP advisory is module-only, with no affected
   package imported or function called by this Xray target.
7. Fixed the existing Ubuntu CI namespace failure: use `sudo unshare -nm`
   instead of relying on unprivileged user namespaces. Added an installed-helper
   test in a private network namespace and private `/run`: dedicated UID/GID,
   empty supplemental groups, zero effective capabilities, NoNewPrivs, explicit
   disconnect, EOF, core SIGKILL, supervisor SIGKILL and explicit recovery.
   The test never changes host network state or host `/run`.

## Verified locally

| Check | Result |
| --- | --- |
| Original release download | SHA-256 matches release checksum |
| Shared .NET regression suite | 782 PASS assertions |
| Real pinned Xray integration after Go update | 67 PASS assertions |
| Helper validation, routing and stop protocol | 8 tests passed |
| Linux managed storage/proxy/retained-guard tests | 11 PASS assertions |
| Unavailable Secret Service | Fails closed, no key on stdout or plaintext fallback |
| Linux backend and tests | Release build passed |
| Xray source vulnerability scan with Go 1.27.3 | No reachable or imported-package vulnerabilities |
| Transitive Linux NuGet vulnerability scan | No advisories reported |
| Self-contained amd64 deb | Built successfully; no Windows executables included |

The Xray integration suite exercises real VLESS/XHTTP requests, routing/DNS,
health-check isolation, authenticated metrics, concurrent IPv4/IPv6 attempts,
failed alternatives, cancellation and empty-AAAA handling. This is not a claim
that every protocol or provider works.

## Native validation and limits

The Linux workflow on this branch runs Ubuntu 24.04 and 26.04, including native
TUN/nftables, installed-helper crash tests, GTK/WebKitGTK, Secret Service, GNOME
proxy restoration and package install/upgrade/remove. **Check the actual run
result before treating these native checks as passed.** The local audit sandbox
cannot create a network/user namespace or run the desktop as a regular UID;
production root rejection was not weakened to bypass that restriction.

Interactive polkit authentication, real systemd-resolved/NetworkManager DNS
selection, coexistence with UFW, clean-machine installation and real-desktop
Wayland/X11 sessions still need acceptance testing. Namespace tests intentionally
stub resolvectl, so they do not prove absence of system DNS leaks. In particular,
more-specific DNS routing domains on other links need a real resolved test.

The kill switch remains volatile across OS reboot, and early boot traffic is
unprotected. System proxy mode covers applications honoring GNOME HTTP/HTTPS
settings, not arbitrary UDP. Windows executable routing is rejected on Linux.
The Linux Services tab remains disabled. Local deb files are not signed updates.
Passing these checks is not a guarantee that the software has no security flaws.
