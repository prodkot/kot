# Validation by change boundary

Use [BUILD.md](BUILD.md) for prerequisites and commands, [release development](../Release/DEVELOPMENT.md) for installer/recovery details and the checked-in workflows for their exact pinned toolchains. This matrix explains which evidence is required; it is not a claim that every environment supports every check.

For a local Dev build, use the rows for the specific change. Run the applicable checks available locally, including native checks when the local Windows environment supports them. Record missing evidence and its effect on what the tester can conclude. Do not start Actions or publish a build to obtain Dev evidence. Every Beta must pass the full Windows checks in Actions, including WebView2, TUN and the installer, even for a change confined to one component.

| Change | Required evidence |
| --- | --- |
| Every PR, including docs | `python Tests/repository.py`; inspect diff, base and uncommitted work |
| Build identity, release refs, upstream source | `Tests/build_identity.py`, `Tests/release_ref.py`, `Tests/xray_source.py` and applicable compiled identity/upstream checks |
| Portable rules, profiles, package validation | `dotnet run --project Tests/Kot.Tests.csproj -c Release`; old-profile/failure/rollback coverage for schema or transaction changes |
| UI or bridge contract | `Tests/architecture_ui.py`, relevant existing browser suites, both-theme screenshots, demo checks; native dispatcher changes also require Windows checks |
| Shared CSS / global UI composition | Complete browser matrix below, small windows, scale, long labels, keyboard/focus and reduced motion |
| Windows window/bridge/lifecycle | NativeTests and WindowTests on Windows, real WebView2 startup/recovery and owned-process cleanup |
| Xray translation/routing | Actual pinned Xray configuration/traffic integration, not only JSON assertions |
| TUN, WFP, DNS, proxy or connection lifecycle | Real Windows TUN traffic, cancel/exit/failure cleanup and restoration evidence |
| Managed service lifecycle/packages | Real Windows `--services-test`, cancellation, integrity, install rollback and stop ownership |
| Payload layout, installation or updating | Build and install the signed Setup, verify installed startup, update, recovery after interruption and uninstall that preserves user data |

After building the Release configuration of `Tests/Kot.Tests.csproj`, run `python Tests/subscription_cycle.py --core PATH_TO_PINNED_XRAY` from the repository root. Two loopback Xray exits serve different HTTP responses. The fixture exercises refresh with a running client, reloads the profile in fresh processes and checks the next real handshake and route. Native WindowTests cover the Windows UI and TUN separately.

For IP/REALITY shortId reconciliation, also run `python Tests/reality_cycle.py --core PATH_TO_PINNED_XRAY` with Playwright/Chromium and OpenSSL available. Optional `--dotnet` and `--tests` select the local runtime and portable test assembly. It uses synthetic credentials, loopback REALITY endpoints with independent shortIds and distinguishable Germany/Sweden responses. Link and native JSON cases cover A/B/C, B/C, A/C, missing and ambiguous targets, refresh with a live tunnel, disconnect/reconnect and fresh-process profile reload. A real browser checks selection, connected labels and both themes using a test bridge adapter. This is not Windows WebView2, DPAPI, TUN or the real provider. `--expect-missing` is only for reproducing an older core's failure, never candidate acceptance. Fixtures keep configurations in subprocess pipes and temporary test profiles, suppress child logs and remove temporary profiles on exit. Never use production subscription data in them.

For the server JSON dialog, `python Tests/node_json_ui.py` exercises the production DOM in Chromium: full data only after opening, two-axis scrolling, complete copy through the fixture bridge, stale read/copy responses, loading/unavailable/error states, per-frame geometry, 360-1280px widths, 100/125/150% text scale, both themes and motion settings, keyboard focus and cleanup. Fixtures contain synthetic values only. Native clipboard, unchanged persistence, snapshot/log isolation and the existing active tunnel are covered by `WindowTests/SubscriptionRefreshChecks.cs` on Windows; a browser adapter does not prove those native boundaries.

## Browser and demo matrix

For shared modal lifecycle changes, run `python Tests/dialog_lifecycle_ui.py` and `python Tests/dialog_lifecycle_ui.py --group stress --cycles 1000`. The Chromium fixture checks actual open/closing/modal states, focus, session generations, exactly-once stack cleanup and effect retirement. It covers both normal finished promises, unequal effect durations, a deliberately paused backdrop, partial creation failure, cancellation, reopening, native close, reduced motion, main-thread delay, hidden iframe recovery with guarded focus retry, preservation of parent/new-control focus, pending update RPC, HWID close-event races and dirty/nested/JSON flows. Test-only source instrumentation exposes the existing state map for assertions; it does not ship in UI assets. Fault injection proves recovery from those conditions, not their occurrence in Windows. Run the unchanged motion, P5.1 and demo suites too, and retain actual WebView2/tray/DPI checks as a separate Windows requirement.

For ping-card updates, `python Tests/ping_cards_ui.py` samples real Chromium frames before native acceptance and during checking/error/retry. It checks both rows, DOM identity, hover colours, selected state, control/icon bounds, per-node duplicate suppression, independent requests, menu focus, card selection, sorting and filtering. The matrix includes both themes, 630px width, 100/125/150% zoom and reduced motion. `dotnet run --project Tests/Kot.Tests.csproj -c Release -- --ping-service` links the production `PingService` to a single-thread owner fixture: a controlled HTTP probe failure and real loopback TCP samples check queue ordering, duplicates, cancellation, cleanup and fresh runs. ProbeCore is a test double; this does not verify native Xray process lifecycle, Windows or WebView2.

For the server action popup, `python Tests/node_menu_ui.py` covers both themes, expanded/collapsed sidebar, 630px windows, 100/125/150% zoom, long names, bottom/right placement, repeated input, keyboard and focus, direct hiding and reduced-motion cleanup. It samples the popup edge and text bounds at 0/40/100/160/199ms, reverses at 20/100/170ms and separately records natural-speed RAF traces. Inspect the generated frames visually; passing geometry assertions alone is insufficient. JSON/QR actions retain their target IDs and payloads, and opening/dismissing a menu must not select or connect a server. These are Chromium fixtures, not native Windows/WebView2 or DPI evidence.

Install the pinned `requirements-dev.txt` and Playwright Chromium in the development environment. Run these from the repository root:

```text
python site/check.py
python site/demo_browser_check.py
python Tests/architecture_ui.py
python Tests/ui.py
python Tests/loading_ui.py
python Tests/journal_ui.py
python Tests/flags_ui.py
python Tests/subscriptions_ui.py
python Tests/routing_ui.py
python Tests/statistics_ui.py
python Tests/client_tools_ui.py
python Tests/node_json_ui.py
python Tests/node_menu_ui.py
python Tests/ping_cards_ui.py
python Tests/services_ui.py
python Tests/ui_controls.py
python Tests/about_ui.py
python Tests/build_identity_ui.py
python Tests/ui_polish.py
python Tests/ui_resilience.py
python Tests/ui_p51.py
```

The architecture suite checks RPC reply correlation, native errors, unknown/duplicate/late replies, heartbeat/navigation allowlists, both deadlines and shared token propagation. Existing feature suites cover interactive behavior. These use synthetic bridge data and Chromium. They do **not** run WebView2, Xray, a VPN connection or Windows network restoration.

For a shared layout change capture the same synthetic state before and after at 800x600, 960x640 and 1280x800, both themes, and 100/125/150% rendering scale at the small size. Cover home, servers, statistics, diagnostics, journal, services, settings and dialogs. Freeze the clock and use the same browser/font versions. Compare geometry and pixels, inspect differences rather than blindly updating a baseline. Pixel-equivalent Chromium captures still do not establish native Windows DPI behavior; include WindowTests evidence separately.

For movement changes, track a visible edge or glyph in a fixed crop in both directions. Compare the presented pose just before retargeting with the first frame after it, including the backdrop. Run `Tests/ui_polish.py` for sidebar paint and early/middle/late interruptions. `WindowTests/WebViewTests.cs` captures the interrupted dialog in the installed WebView2 runtime. Run just the UI fixture on Windows with `dotnet run --project WindowTests/Kot.WindowTests.csproj -c Release -- --motion-capture artifacts/window-motion`. Report live playback separately from paused frame samples.

## Native and CI evidence

For Beta, require both workflows to succeed on the candidate revision. `.github/workflows/checks.yml` runs repository/site/browser, portable/Xray integration and native Windows jobs. `.github/workflows/development.yml` builds the signed Beta installer and exercises WebView2, real Windows TUN traffic and installation/recovery. Its current branch filter is `dev/**`. Set the Beta version before pushing the candidate there. A push starts Actions automatically. Keep Dev builds local. Do not invoke the release workflow or create a release tag for Beta testing.

Keep the tested commit, workflow URL, job conclusions and artifact names with the review. A build of the base commit is useful baseline evidence, but cannot validate changed code. A test failure caused by tool installation or a hosted runner is still a blocked check until rerun successfully; do not relabel it as passed. Do not mask failing assertions, broaden permissions or weaken readiness/signature checks to obtain green CI.

The shipped QA kit queries WebView availability through the verified installed SDK loader, not an optional registry display name. `Tests/qa_webview.ps1` exercises this under Windows PowerShell 5.1 against the installed payload. Discovery is not proof of successful browser initialization; WindowTests and installed startup checks provide that separate evidence. See Microsoft's [runtime detection](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution#detect-if-a-webview2-runtime-is-already-installed) and [native loader API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/webview2-idl#getavailablecorewebview2browserversionstring).

On local environments bound subprocess time and log size, close browsers, and clean up owned child processes in `finally`. Windows network tests belong on a disposable/isolated machine. Never delete unrelated routes, drivers or processes as a test cleanup shortcut.

## Report honestly

Separate passed, failed, blocked and not-run checks. Include the commit, environment, mock limitations and residual machine-specific issues. For Dev, report local results. For Beta, include the Actions results for that revision and keep any manual checks separate. A hosted TUN pass cannot establish that a previous failure on a user's particular adapter or subscription has been fixed.

## Branded loading motion

Run `python Tools/loading_motion.py` after editing the canonical choreography and `python Tools/loading_demo.py` to make the standalone review page. `Tests/loading_ui.py` verifies shared catalog identity, ground clearance, contact timing, ballistic acceleration, grounded rolling, weighted selection, exclusive 47th-click behavior, original 443rd-click sound, lifecycle, theme/zoom geometry and reduced motion. `Tests/ui.py` retains first-paint theme, shared native origin, live motion through exit, readiness count and input/RAF cleanup assertions. WindowTests exercise the actual native/WebView handoff on Windows; compilation alone is not execution. Capture normal playback and quarter-speed playback, inspect takeoff/apex/impact/contact/cycle seams and both theme frames.
