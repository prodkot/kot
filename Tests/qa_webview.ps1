param([Parameter(Mandatory)][string]$LoaderPath)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../QA/WebViewRuntime.cs')
# Run the shipped probe in Windows PowerShell 5.1. Do not install a runtime,
# change registry names, start a browser or turn a missing runtime into PASS.
$loader=[IO.Path]::GetFullPath($LoaderPath)
$version=[KotQaWebViewRuntime]::Probe($loader)
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+( (beta|dev|canary))?$' -or $version -match '^0\.0\.0\.0($| )') { throw 'Installed runtime not detected' }
$again=[KotQaWebViewRuntime]::Probe($loader)
if ($again -ne $version) { throw 'Repeated loader probe changed version' }
foreach ($invalid in @('WebView2Loader.dll',($loader+'.missing'))) {
    $rejected=$false
    try { [KotQaWebViewRuntime]::Probe($invalid) | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw 'Unverified loader path was accepted' }
}
Write-Host "PASS shipped QA WebView probe: native SDK discovery ($version), repeated load/unload, missing/relative loader rejected"
