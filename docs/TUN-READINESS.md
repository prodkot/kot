# Windows TUN readiness

When Xray starts its local HTTP listener before the Windows TUN inbound has
finished starting, the proxy can answer a health request while Wintun is still
creating an adapter. The client previously treated proxy HTTP and Windows DNS
success as enough to publish `connected`. A later Wintun failure then stopped
the core, leaving ordinary direct networking available when Kill Switch was off.

Connection readiness now requires:

1. The pinned Xray core's complete-startup event, retained outside the log ring.
2. An operational `kot-tun` interface with the configured IPv4 address.
3. A successful HTTP response through the authenticated local health proxy.
4. Windows DNS resolution and a separate successful HTTP response through TUN.

The TUN HTTP socket is bound to the tunnel source address and interface. Windows
must also select that interface for the destination's ordinary route. A loopback
response, a physical-interface route, or an unbound fallback cannot pass this
check. Periodic connection probes use the same TUN HTTP check.

Windows `IP_UNICAST_IF` takes a network-byte-order interface index;
`IPV6_UNICAST_IF` takes host byte order. See the Microsoft documentation for
[IPv4](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options)
and [IPv6](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ipv6-socket-options).

Adapter creation timeouts/setup failures receive a TUN-specific error. A working
proxy with failed TUN traffic is reported separately from failed Windows DNS.
Startup remains cancellable and bounded. Failed startup stops its core and
preserves the enabled Kill Switch.

## Limits and regression coverage

This fixes a confirmed false-positive connection state. It does not establish or
fix the underlying intermittent Windows PnP/Wintun creation failure. Similar
errors are reported [upstream in Xray](https://github.com/XTLS/Xray-core/issues/6799).
There is no driver replacement, network reset, deletion of another client's
adapter, automatic Kill Switch enablement or change to saved routing policy.
Kill Switch remains necessary if direct traffic must be blocked after failure.

Core tests reject partial-startup/log lookalikes. Native launcher tests hold a
live listener before the startup event and check that subsequent log eviction
cannot lose readiness. Windows integration covers real proxy plus TUN HTTP,
loopback-only health that must never emit `connected`, repeated production TUN
connections with Kill Switch off/on, cleanup after failure/cancellation, and
post-stop probes without direct fallback. Existing real-core crash and WFP leak
checks remain part of the development workflow.

Real Windows CI execution is required before distributing the candidate.

## Build dependency follow-up

The first CI attempt stopped on newly published [GO-2026-6859](https://pkg.go.dev/vuln/GO-2026-6859), an HTTP/1 server crash in the Go standard library. The pinned compiler and all CI build jobs now use Go 1.27.4, which contains the fix. The Xray source commit and protocol modules are unchanged. This is a separate dependency correction, not an explanation for the reported Windows adapter creation failure.
