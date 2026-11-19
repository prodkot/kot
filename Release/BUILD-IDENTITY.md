# Version and build identity

The semantic version stays `0.8.3`. Packaged builds also have an automatic identity, for example `0.8.3 (dev.123.1)`.

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
