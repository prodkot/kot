# Version and build identity

## Choose the public version

For a request such as “send me an installer so I can test the fix”, use **Beta** unless the requester explicitly specifies another channel. A branch name or a development workflow does not select a channel for the user.

| Intended use | Canonical version | In the app | Windows x64 installer |
| --- | --- | --- | --- |
| Intermediate development build | `0.8.3-dev.1` | `kot. 0.8.3 Dev 1` | `Kot-Setup-0.8.3-dev.1-Windows-x64.exe` |
| User testing | `0.8.3-beta.1` | `kot. 0.8.3 Beta 1` | `Kot-Setup-0.8.3-beta.1-Windows-x64.exe` |
| Stable release candidate | `0.8.3-rc.1` | `kot. 0.8.3 RC 1` | `Kot-Setup-0.8.3-rc.1-Windows-x64.exe` |
| Stable release | `0.8.3` | `kot. 0.8.3` | `Kot-Setup-0.8.3-Windows-x64.exe` |

These are format examples. Use the intended base version. For each base version and prerelease channel, start at 1 and advance the number for each distinct build handed out. For example, after sending Beta 1, a build with a further fix is Beta 2. Stable uses the base version alone.

Generate all representations automatically from one authoritative version record (base version, channel and prerelease number). Never maintain separate version strings by hand in the UI, project files or installer. The app header and About, textual product metadata, Setup name and UI, and the installed program's Windows `DisplayVersion` must identify the same version, including its channel and number. Derive numeric-only Windows fields from the same base version. Keep CI run IDs and attempts, temporary or local IDs, commit hashes and build time in provenance metadata. They must never stand in for the public version.

## Before handing over a Setup

1. Identify the exact source commit containing the requested changes. After a source change, build a fresh installer from the updated revision. An old Setup with a new filename is still the old build.
2. Verify the candidate's commit against the intended revision. Check the UTC build time against the packaging run that produced the installer. Use the run or its `build-info.json` (`builtAtUtc`), since copying or downloading a file can change its filesystem timestamps.
3. Compute SHA-256 on the final executable you will send, for example with `Get-FileHash -Algorithm SHA256 -LiteralPath $setup` in PowerShell. Compare its name and digest with `setup.name` and `setup.sha256` in that run's `candidate.json`. Keep the existing package and Setup signature checks. If the version, revision, time or digest cannot be verified, resolve the discrepancy before delivery.
4. Give the recipient the version, filename, commit, UTC build time and verified SHA-256. Report the checks actually run on that build and their results. List untested features and known limitations explicitly. Dev, Beta and RC describe the intended audience or stage. None certifies that a test passed.

## Packaging from 0.8.4

Set the numeric base, channel and sequence in `Directory.Build.props`. A stable build has empty channel and sequence fields. Reserve 0.9.0 for a substantial user-visible milestone. Small fixes and interface improvements use the next 0.8.x patch.

The build writes a single signed-payload stamp with the public version, technical build ID, UTC time and source commit. The stable updater reads the numeric base. Install subsequent betas or a stable version with the same numeric base through Setup. The stable publication workflow rejects prerelease candidates.
