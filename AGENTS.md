# Working on kot

This is a Windows VPN client with privileged networking and a local WebView2 UI. A successful browser test does not establish that a VPN works. Read the relevant implementation, its callers and tests before editing. This guide applies to all directories; follow the user's authorized scope and do not turn routine checks into additional approval steps.

## Start and finish

1. Inspect `git status`, the intended base branch, open PRs and existing worktrees. Use a separate branch/worktree. Preserve uncommitted work; do not reset, force-push or replace another change.
2. Read the mandatory [version lifecycle policy](Release/LIFECYCLE.md), [architecture](docs/ARCHITECTURE.md), the relevant feature document and nearby tests. Identify the owner of state, cancellation, error presentation and cleanup. Record available baseline checks before refactoring.
3. Make the smallest cohesive change. Reuse current mechanisms, avoid unrelated rewrites, new global stores, duplicated business rules and wrappers without independent responsibility. Keep commits independently reviewable.
4. Run the checks in [the validation matrix](docs/VALIDATION.md). Fix regressions. State which checks passed, failed, were unavailable or were not run, with commit and environment. Never label mocks as Windows, WebView2, Xray or TUN verification.
5. Review the final diff for changed contracts, secrets, generated artifacts and lost tests. Update the ownership map and feature docs if behavior or ownership changes. Provide the result and concrete limitations.

Do not merge PRs, merge changes into main, publish releases, replace published installers or force-push without explicit authorization. Build/test authorization does not imply merge, publication or replacement authorization.

## Build channels

[Release/LIFECYCLE.md](Release/LIFECYCLE.md) is the authoritative lifecycle policy for every future version and agent. Read it before choosing a channel, preparing or handing off a build, claiming readiness, merging or publishing. Reconcile conflicting summaries with it; historical reports do not override it.

- **Dev** is active development: new features, experiments, architecture changes and UI rework within the authorized task. Intermediate solutions are allowed with their limitations documented. Run applicable automated checks. Builds are internal, not full releases. A local Dev handoff needs no Actions run; do not push to a CI-triggering branch just to obtain it. Ordinary authorized PR checks do not promote the build's channel or readiness.
- **Beta** is stabilization: fixes, polish, compatibility and regression checks. Major new features require the owner's direct permission. Full Beta acceptance requires both **Windows checks** and **Windows development build** on the exact candidate revision, real Windows WebView2, tray, DPI, TUN, startup, shutdown, installation, update and uninstall checks, and a verified signed Setup from Actions. Without these gates a local build is only a **local Beta candidate**, regardless of its version string. Public Beta publication is only a **GitHub Pre-release** with explicit owner approval.
- **Stable** is an accepted, reliable and predictable release without known critical regressions. After Beta, changes to client code, dependencies or packaging must be validated in Beta again. Publish only from **main**, as a normal **GitHub Release**, after acceptance and explicit owner approval. The stable updater must not offer experimental or prerelease builds. The compiler's `Release` configuration is not this lifecycle status.

Separate channel, verified readiness and publication permission. Never promote readiness without evidence. Report passed, failed, unavailable and not-run checks separately; compilation, portable tests and Chromium are not real Windows verification. Preserve update, build identity and distribution-channel compatibility. Follow [Windows acceptance](Release/DEVELOPMENT.md), [artifact identity](Release/BUILD-IDENTITY.md) and [publication](Release/GITHUB.md) details without treating them as permission to publish.

## Ownership and dependency direction

| Location | Responsibility | Extension point |
| --- | --- | --- |
| `Core/` | Portable profiles, parsing, routing, Xray translation, validation, statistics, package verification | Domain logic and deterministic tests in `Tests/` |
| `Windows/` | DPAPI, WinForms/WebView2, owned processes, TUN/WFP/DNS/proxy, automation, storage and updates | Platform implementation calling Core |
| `Windows/MainWindow*.cs` and existing feature partials | One UI-thread owner, bridge dispatch, profile mutations and views | Add a focused feature handler; retain shared gates and lifetime |
| `Windows/ManagedServices*.cs` | One service lifecycle owner, packages, process jobs and strategy runs | Extend the responsible partial; preserve cancellation and rollback coordination |
| `Windows/ui/` | Presentation, transient drafts, input validation and RPC intents | Existing factory/controller pattern; no network policy in JS |
| `Tests/`, `NativeTests/`, `WindowTests/` | Portable logic/browser, native process, real Windows/window/network checks | Choose the layer which can actually reproduce the change |
| `Release/`, `Installer/`, `.github/` | Reproducible signed payload, installation/update/recovery and CI | Keep release provenance and owned-file cleanup |
| `site/` | Landing page and isolated demo built from client assets | Mock bridge only; do not fork app UI |
| `docs/`, `licenses/`, `upstream/` | Feature contracts, notices and pinned corresponding source | Update with dependency or workflow changes |

Core must not reference Windows, UI, site or test projects. Windows references Core; the UI uses the constrained JSON bridge, not native host objects. Page factories receive `getModel`, RPC and navigation/dialog callbacks from `app.js`. They must not import each other cyclically, mutate the host snapshot or create another authoritative connection state. `presentation.js` has no transport dependency. The site consumes UI assets in one direction.

Partial classes are organizational boundaries, not independent services. Do not assume moving a method to another partial gives it a separate lock, process or cancellation lifetime.

## UI and RPC changes

Before any change to UI, UX or motion, including copy, navigation, focus, state presentation and reduced motion, read the [kot-ui-system skill](.agents/skills/kot-ui-system/SKILL.md) and the reference sections it selects. This applies to the client, demo, website and native surfaces. Consult [UI architecture](docs/UI-ARCHITECTURE.md), the affected feature documentation and [the validation matrix](docs/VALIDATION.md).

- Use `styles/tokens.css` and existing selectors/components first. Preserve approved visual behavior in both themes; do not silently redesign while refactoring.
- Preserve DOM IDs, action names, payloads, reply IDs, snapshot fields and error semantics unless an explicit coordinated migration is part of the task. Update native dispatch, demo and fixtures together for a new action.
- Native C# validates untrusted input. JS validation improves UX but is not authorization. Render subscription/server text with text nodes, never HTML interpolation. Use the flag allowlist; do not turn labels into asset paths.
- Use `dialog-ui.js` for modal lifecycle, dirty confirmation, focus return and validation. Keep pending controls, cancellation, error messages, keyboard access, ARIA and reduced motion. Reserve geometry when labels change.
- A click or successful RPC acceptance is not completed work. Render connection/service/update success only from the confirmed native state; a pending task may still fail.
- Keep UI modules in the bundled asset tree. Check demo isolation and installer asset inclusion after moving files. Do not ship tools, private fixtures or agent instructions in `Windows/ui/`.

## Persistence and privileged lifecycle

- Keep settings/profile compatibility: use `Store.Change` / `ProfilePersistence`, normalization and previous/recovery copies. Do not mutate a live profile then save it through an alternative path. Schema changes require old-profile, failure and recovery tests.
- TUN, WFP, Kill Switch, DNS and system proxy changes require explicit tracing of success, error, cancellation, process exit, reconnect and application shutdown. Keep one owner of each resource. Await owned work before removing resources it can recreate.
- Preserve `Tunnel` generation/gate checks, real readiness probes and Xray process supervision. Do not mark `connected` merely because a process or adapter exists. Do not loosen a readiness check just to make a fixture pass.
- Do not stop arbitrary processes/drivers or delete arbitrary routes. Preserve owned-job cleanup and existing fail-closed behavior when Kill Switch is enabled. Network tests belong on an isolated Windows runner, with before/after restoration checks.
- Keep cancellation commands reachable while a long operation holds its mutation gate. Check lifetimes/generations after `await`, especially for replaced browsers, processes and dialogs.
- Use existing `UserError`, `Store.Friendly` and sanitized logging boundaries. Do not swallow failures or fabricate success. Long work needs bounded timeouts, bounded logs and cleanup in `finally`.

## Security and release invariants

- Never commit or publish subscription URLs, credentials, keys, HWID, user profiles, raw diagnostic dumps or private signing material. Keep such data out of PR bodies, screenshots, fixtures and search queries. Use synthetic fixtures.
- Preserve WebView origin/navigation/CSP restrictions, size limits and generation checks. The renderer must not gain arbitrary native execution or unrestricted navigation.
- Preserve Setup and package signature verification, pinned public key, version checks, exact-byte validation, archive/path/reparse defenses and rollback evidence. Do not add unsigned fallback updates or replace trusted sources silently.
- Installer cleanup removes only owned files and preserves user data. Read [release development](Release/DEVELOPMENT.md) before changing installation/recovery.
- Pin dependencies/actions/toolchains, update notices and corresponding source, and justify new dependencies. A refactor is not an implicit dependency upgrade.
- Before preparing, naming or handing off a build, follow the [version and handoff policy](Release/BUILD-IDENTITY.md). Choose the intended channel by the lifecycle policy and the authorized task; a Beta label or test installer does not confer Beta acceptance. Keep the public version distinct from build metadata. Do not fabricate a new release tag for an architecture change.

## Validation and reporting

Run `python Tests/repository.py` for every PR, including documentation. Core changes also need `dotnet run --project Tests/Kot.Tests.csproj -c Release`; UI changes need the affected browser suites and `python Tests/architecture_ui.py`, site checks and both-theme screenshots. Native window/bridge/package/lifecycle changes require `NativeTests` and `WindowTests` on Windows. Networking and installer changes additionally require real TUN, installation and recovery evidence. Use the [validation matrix](docs/VALIDATION.md) to select local checks for Dev. Accepted Beta must pass both required Windows workflows on the same candidate revision and the full acceptance gates in Release/LIFECYCLE.md.

Check small windows, 100/125/150% scale, long strings, focus and reduced motion when relevant. Keep baseline and candidate screenshots with synthetic data. Investigate differences rather than accepting a changed baseline automatically.

If the environment cannot run a check, report the concrete limitation. An unavailable Windows check remains open on Dev and blocks Beta acceptance when required by the lifecycle policy; a local Beta build must remain labelled a local Beta candidate. Do not weaken a test, hide its failure or claim a hosted runner proves the user's machine is fixed. Record remaining debt separately from completed changes.
