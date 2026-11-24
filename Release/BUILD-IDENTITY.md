# Version and build identity

## Choose the public version

Choose the intended stage under the mandatory [lifecycle policy](LIFECYCLE.md): Dev for active development, Beta for stabilization, Stable only after acceptance. Major new features in Beta require direct owner approval. Select the intended channel in `Directory.Build.props` when preparing a new build; documentation-only work does not require a version change. Branch names, workflow names and the compiler's `Release` configuration do not set the public version or readiness. A Beta-labelled local build without the acceptance gates is only a **local Beta candidate**, not a full Beta.

| Intended use | Canonical version | In the app | Windows x64 installer |
| --- | --- | --- | --- |
| Internal active development | `0.8.3-dev.1` | `kot. 0.8.3 Dev 1` | `Kot-Setup-0.8.3-dev.1-Windows-x64.exe` (if building Setup locally) |
| Beta stabilization (acceptance reported separately) | `0.8.3-beta.1` | `kot. 0.8.3 Beta 1` | `Kot-Setup-0.8.3-beta.1-Windows-x64.exe` |
| Stable release candidate | `0.8.3-rc.1` | `kot. 0.8.3 RC 1` | `Kot-Setup-0.8.3-rc.1-Windows-x64.exe` |
| Stable release | `0.8.3` | `kot. 0.8.3` | `Kot-Setup-0.8.3-Windows-x64.exe` |

These are format examples. Use the intended base version. For each base version and prerelease channel, start at 1 and advance the number for each distinct build handed out. For example, after sending Beta 1, a build with a further fix is Beta 2. Stable uses the base version alone. A new label does not complete acceptance; do not rename old bytes or replace a published installer to imply that a new revision was tested.

RC is an optional existing prerelease label, not a fourth way around the lifecycle gates. It remains outside the stable update channel and must not be published as a normal Stable Release. Completed Beta acceptance and explicit release approval are still required.

Generate all representations automatically from one authoritative version record (base version, channel and prerelease number). Never maintain separate version strings by hand in the UI, project files or installer. The app header and About, textual product metadata, Setup name and UI, and the installed program's Windows `DisplayVersion` must identify the same version, including its channel and number. Derive numeric-only Windows fields from the same base version. Keep CI run IDs and attempts, temporary or local IDs, commit hashes and build time in provenance metadata. They must never stand in for the public version.

## Local Dev handoff

Build and check the requested revision locally. If handing over a ZIP, verify its contents and build identity, hash the final archive and give the tester its version, filename, commit, UTC build time and SHA-256. Include the local test results and any missing coverage, particularly Windows-only checks. A local Dev handoff uses no Actions run or public upload; ordinary authorized PR checks are separate source validation. For a local Setup, the signature and verification steps below also apply.

## Local Beta candidate handoff

A requested local Beta build may retain its intended `-beta.N` public version, but it has not completed Beta acceptance. Mark the handoff filename, caption and report explicitly as **local Beta candidate** (for example, `Kot-0.8.5-beta.2-local-candidate-Windows-x64.zip`). This handoff qualifier does not change the canonical version record or installed product identity. Preserve the build ID, source commit and UTC stamp. Report missing or failed Windows evidence; do not claim that compilation, portable tests or Chromium validate native Windows. Do not rename a ZIP as Setup or present a local installer as an Actions-verified artifact.

Only successful **Windows checks** and **Windows development build** on the same revision, the required real Windows scenarios and a verified signed Setup from Actions satisfy the [Beta gates](LIFECYCLE.md). A CI artifact is not a GitHub Pre-release. Beta publication needs explicit approval and `prerelease=true`; Stable publication needs separate acceptance and approval, from main as a normal Release. Neither publication nor replacement of published installers follows automatically from a handoff request.

## Before handing over a Setup

1. Identify the exact source commit containing the requested changes. After a source change, build a fresh installer from the updated revision. An old Setup with a new filename is still the old build.
2. Verify the candidate's commit against the intended revision. Check the UTC build time against the packaging run that produced the installer. Use the run or its `build-info.json` (`builtAtUtc`), since copying or downloading a file can change its filesystem timestamps.
3. Compute SHA-256 on the final executable you will send, for example with `Get-FileHash -Algorithm SHA256 -LiteralPath $setup` in PowerShell. Compare its name and digest with `setup.name` and `setup.sha256` in that run's `candidate.json`. Keep the existing package and Setup signature checks. If the version, revision, time or digest cannot be verified, resolve the discrepancy before delivery.
4. Give the recipient the version, filename, commit, UTC build time and verified SHA-256. State the intended channel and actual acceptance status separately. Report passed, failed, unavailable and not-run checks. For accepted Beta include both Actions run links and revisions plus the signed Setup artifact. A version label alone certifies nothing; a claim of full Beta readiness must meet every lifecycle gate. A required unperformed check cannot be hidden under a general limitations note while calling the Beta accepted.

## Packaging from 0.8.4

Set the numeric base, channel and sequence in `Directory.Build.props` for an authorized new build. A stable build has empty channel and sequence fields, after acceptance and release authorization. Choose the base version for the scope of that version; examples above are not a permanent reservation of a particular future number. Do not bump the version solely for documentation work or to imply higher readiness.

The build writes a single signed-payload stamp with the public version, technical build ID, UTC time and source commit. The stable updater reads the numeric base from stable releases, excludes draft/prerelease releases and must never offer Dev, Beta, RC or experimental builds. Preserve signature, version-comparison and distribution-channel compatibility; do not repurpose technical build IDs as public versions. Install subsequent betas or a stable version with the same numeric base through Setup. The stable publication workflow rejects prerelease candidates.
