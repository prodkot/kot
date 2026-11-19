# Validation by change boundary

Use [BUILD.md](BUILD.md) for prerequisites and commands, [release development](../Release/DEVELOPMENT.md) for installer/recovery details and the checked-in workflows for their exact pinned toolchains. This matrix explains which evidence is required; it is not a claim that every environment supports every check.

| Change | Required evidence |
| --- | --- |
| Every PR, including docs | `python Tests/repository.py`; inspect diff, base and uncommitted work |
| Build identity, release refs, upstream source | `Tests/build_identity.py`, `Tests/release_ref.py`, `Tests/xray_source.py` and applicable compiled identity/upstream checks |
| Portable rules, profiles, package validation | `dotnet run --project Tests/Kot.Tests.csproj -c Release`; old-profile/failure/rollback coverage for schema or transaction changes |
| UI or bridge contract | `Tests/architecture_ui.py`, relevant existing browser suites, both-theme screenshots, demo checks; native dispatcher changes also require Windows checks |
| Shared CSS / global UI composition | Complete browser matrix below, small windows, scale, long labels, keyboard/focus and reduced motion |
| Windows window/bridge/lifecycle | NativeTests and WindowTests on Windows, real WebView2 startup/recovery and owned-process cleanup |
| Xray translation/routing | Actual pinned Xray configuration/traffic integration, not only JSON assertions |
| TUN, WFP, DNS, proxy or connection lifecycle | Development workflow's real Windows TUN tests, cancel/exit/failure cleanup and restoration evidence |
| Managed service lifecycle/packages | Real Windows `--services-test`, cancellation, integrity, install rollback and stop ownership |
| Payload layout, installation or updating | Development workflow installer publish/install/startup, update and interrupted-recovery checks; owned-data-preserving uninstall |

## Browser and demo matrix

Install the pinned `requirements-dev.txt` and Playwright Chromium in the development environment. Run these from the repository root:

```text
python site/check.py
python site/demo_browser_check.py
python Tests/architecture_ui.py
python Tests/ui.py
python Tests/journal_ui.py
python Tests/flags_ui.py
python Tests/subscriptions_ui.py
python Tests/routing_ui.py
python Tests/statistics_ui.py
python Tests/client_tools_ui.py
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

## Native and CI evidence

`.github/workflows/checks.yml` runs repository/site/browser, portable/Xray integration and native Windows jobs. `.github/workflows/development.yml` additionally builds the real development installer and exercises Windows TUN and installation/recovery. A `dev/**` branch or its manual development dispatch is the supported validation route. These are development artifacts; do not invoke the release workflow or create a release tag for testing.

Keep the tested commit, workflow URL, job conclusions and artifact names with the review. A build of the base commit is useful baseline evidence, but cannot validate changed code. A test failure caused by tool installation or a hosted runner is still a blocked check until rerun successfully; do not relabel it as passed. Do not mask failing assertions, broaden permissions or weaken readiness/signature checks to obtain green CI.

The shipped QA kit queries WebView availability through the verified installed SDK loader, not an optional registry display name. `Tests/qa_webview.ps1` exercises this under Windows PowerShell 5.1 against the installed payload. Discovery is not proof of successful browser initialization; WindowTests and installed startup checks provide that separate evidence. See Microsoft's [runtime detection](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution#detect-if-a-webview2-runtime-is-already-installed) and [native loader API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/webview2-idl#getavailablecorewebview2browserversionstring).

On local environments bound subprocess time and log size, close browsers, and clean up owned child processes in `finally`. Windows network tests belong on a disposable/isolated machine. Never delete unrelated routes, drivers or processes as a test cleanup shortcut.

## Report honestly

Separate passed, failed, blocked and not-run checks. Include known environmental differences, mock limitations and residual machine-specific issues. When local Windows/.NET checks are unavailable, use the authorized existing CI and report both the local limitation and remote result. A hosted TUN pass cannot establish that a previous failure on a user's particular adapter or subscription has been fixed.
