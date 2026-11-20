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

## Packaging as shipped in 0.8.3

The preceding sections specify the policy for future builds. The 0.8.3 tooling reads the base version from `Core/ClientIdentity.cs` and generates a technical stamp such as `0.8.3 (dev.123.1)`. It does not yet generate the public prerelease forms above. If tooling cannot produce the requested version throughout the installed product, report that limitation before handoff. Renaming an existing executable does not implement a channel.

- `dev.<run number>.<attempt>`: Windows development build workflow.
- `release.<run number>.<attempt>`: Windows release workflow.
- `local.<UTC timestamp>.<random suffix>`: local packaging. Ordinary un-stamped `dotnet build` displays `(local)`.

The workflow prefix separates independent GitHub counters. Retrying a run changes its attempt. Missing or invalid CI metadata fails packaging instead of silently producing an ambiguous build. There is no manually maintained counter or semantic version bump per test build.

`Release/build-identity.py` creates one stamp before publishing. `build.ps1` passes it to assembly metadata through `Directory.Build.targets` and writes `build-info.json`. Packaging reuses that stamp for Setup and the real uninstaller. The signed payload includes the stamp, so an edited sidecar cannot silently change a verified package. The running client reads compiled metadata, not an editable sidecar.

The display identity appears in the client header, About, advanced version details, tray menu, startup screen, diagnostic export, startup evidence, Setup and uninstall UI, textual PE ProductVersion, and Windows installed-programs DisplayVersion. Source SHA remains available separately in diagnostics and packaging metadata.

Development installers use `Kot-Setup-0.8.3-build-dev.123.1-Windows-x64.exe`. QA candidate metadata also records the exact build. Numeric FileVersion, AssemblyVersion and manifest version stay semantic; builds are not extra release versions.

The updater still compares semantic versions and uses the existing stable release channel. Switching between two test builds with the same semantic version uses Setup. A later public release keeps the existing canonical asset name for compatibility with older clients, while retaining the build identity inside the installer. This change does not publish a release.

When an older updater commits a new package, it writes a plain semantic ARP DisplayVersion. The new client waits for commit and updater completion, then restores its own display version only if the registered installation path still matches. It does not change another installation's registration.

Validation covers identity generation and invalid metadata, compiled metadata across incremental rebuilds, both UI themes and window sizes, package ProductVersion consistency, real Windows installation/reinstallation and path changes, scheduled-client evidence, and upgrades through older updaters. Real sign-out/sign-in on the user's PC remains separate from CI scheduler tests.
