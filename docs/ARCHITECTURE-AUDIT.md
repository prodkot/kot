# Architecture foundation audit, 2026-11-19

## Baseline and scope

Base: `a92bd99159ca56becb5fd583b794a760e5671c39`, the head of [PR #51](https://github.com/prodkot/kot/pull/51), not the older main branch. Work is isolated on `refactor/architecture-foundation`; the eventual PR targets `ui/p51-polish`. Dependency PRs #52-54 remain independent. No merge, dependency upgrade, release or profile migration is part of this change.

## Findings and migration order

| Area | Finding and consequence | Decision |
| --- | --- | --- |
| Core | Configuration, Xray translation, routing, subscription parsing and profile persistence already have separate owners. Their size reflects protocol validation, not necessarily excess responsibility. | Keep these APIs and schemas. Document dependencies; do not reimplement policy in JavaScript. |
| Windows shell | MainWindow mixes window creation, WebView origin/lifetime checks, RPC dispatch, profile commands and log export. Existing partials already share one UI-thread owner. | Separate bridge and profile commands into named partials, preserving dispatch, gates and generation checks. |
| Managed services | Package installation/rollback, process launch, strategy testing and operation scheduling share one long file. Cancellation and VPN preparation deliberately share state. | Separate cohesive implementation areas into partials; retain a single lifecycle owner. Independent asynchronous service objects would require a separate race/lifetime design. |
| Web UI | HTML embeds two large historical style blocks. app.js mixes transport, rendering, journal, statistics, routing and startup. Existing about/services/client-tools/dialog factories provide a usable pattern. | Extract canonical styles/tokens, then explicit dependency-injected UI modules; keep classic scripts compatible with WebView and file fixtures. |
| Design | Theme variables exist, but typography, control geometry, radii and motion are repeated. Late P5.1 overrides encode approved behavior. | Derive tokens from current values. Preserve cascade order, selectors and responsive behavior; validate pixels rather than assume equivalence. |
| Tests | Strong focused core/browser/native suites exist. Browser fixtures exercise DOM and RPC shape but cannot prove WFP/TUN or WebView lifetime. | Record baseline, compare both themes/scales, run existing native CI and real TUN development workflow separately. |
| Site | The demo reuses app source but build.py has a hand-maintained script copy list. New modules can silently be omitted. | Copy canonical UI assets, retain the isolated mock bridge and restrictive demo CSP, verify demo parity and no native operation path. Landing branding stays independent. |
| Release / installer | Signed package manifests, pinned upstream source, installer maintenance and recovery are already separated and security-sensitive. | No production behavior edits. Exercise payload completeness and installation because UI asset layout changes. |
| Docs / agents | Feature/build docs exist, but no root agent guide and no current end-to-end ownership map. | Add concise engineering rules, architecture and UI contracts linked to existing feature docs. |

## Risk controls

1. Preserve UI IDs, RPC action names, payloads, timeouts, native source checks and cancellation exceptions.
2. Preserve profile normalization, DPAPI envelope, atomic replacement and recovery copies.
3. Keep TUN, DNS, WFP, system proxy, Xray configuration and signed-update validation untouched.
4. Keep CSS specificity/order; tokens replace identical values only. Small-window exceptions remain explicit.
5. Compare source ownership and command contracts after extraction; run behavior suites after each stage.

## Remaining debt to track independently

- P1: Convert the native/JS RPC surface into a reviewed schema with generated or checked payload types. This patch documents the existing contract rather than introducing a protocol migration.
- P1: Expand cancellation/fault-injection coverage around competing update, service rollback and reconnect operations before changing their ownership model.
- P1: Reproduce reported machine-specific Wintun startup failures on affected hardware. Passing a hosted Windows TUN test does not establish the cause on the user's machine.
- P2: Retire historical CSS overrides component by component with visual baselines. Broad cascade simplification is riskier than centralizing approved parameters.
- P2: Extract remaining subscription/server/update UI controllers when their behavior next changes, using the same explicit dependencies. Avoid a second mutable global state store.
- P2: Continue installer interrupted-recovery diagnostics on Windows; do not infer reliability solely from a successful clean install.
- P2: Share browser fixture builders so fixture snapshots cannot drift independently from the documented host projection.

Execution results and limitations are recorded in the PR/report after validation, not inferred from this plan.
