$ErrorActionPreference='Stop'
$archive=Get-ChildItem artifacts/Kot-Portable-*-Windows-x64.zip | Select-Object -First 1
$folder=Join-Path $env:RUNNER_TEMP ('kot portable тест ' + [Guid]::NewGuid().ToString('N'))
$installed=Join-Path $env:LOCALAPPDATA 'KotVPN'
function Fingerprints($root) {
    $values=@{}
    if (Test-Path $root) { foreach ($file in Get-ChildItem $root -File -Recurse) { $values[[IO.Path]::GetRelativePath($root,$file.FullName)]=(Get-FileHash $file.FullName).Hash } }
    return $values
}
$before=Fingerprints $installed
$process=$null
$close=$null
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$stableMutex=[Threading.Mutex]::new($true,"Local\KotVPN-$sid")
try {
    Expand-Archive $archive.FullName $folder
    if (Test-Path "$folder/data") { throw 'Portable archive contains test or user data.' }
    if (Test-Path "$folder/Install-WebView2.exe") { throw 'Portable must not install a shared browser runtime.' }
    if (!(Test-Path "$folder/Kot.Portable.exe") -or !(Test-Path "$folder/coreclr.dll") -or !(Test-Path "$folder/WebView2Runtime/msedgewebview2.exe")) { throw 'Portable runtime payload is incomplete.' }
    $process=Start-Process "$folder/Kot.Portable.exe" -WorkingDirectory $env:RUNNER_TEMP -PassThru
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
      Start-Sleep -Milliseconds 100
      $process.Refresh()
      if ($process.HasExited) { throw "Actual portable exited early: $($process.ExitCode)" }
      if ($clock.Elapsed.TotalSeconds -gt 40) { throw 'Actual portable did not open its independent window and browser profile.' }
    } until ($process.MainWindowTitle -eq 'kot. portable' -and (Test-Path "$folder/data/WebView/EBWebView"))
    Write-Host 'PASS actual self-contained portable runs from a Unicode folder with an unrelated working directory while the installed instance mutex is held.'
    $close=Start-Process "$folder/Kot.Portable.exe" -ArgumentList '--shutdown' -PassThru
    if (!$close.WaitForExit(40000) -or $close.ExitCode -ne 0 -or !$process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Actual portable shutdown failed.' }
    $after=Fingerprints $installed
    if ($after.Count -ne $before.Count) { throw 'Portable changed installed profile files.' }
    foreach ($name in $before.Keys) { if ($after[$name] -ne $before[$name]) { throw "Portable modified installed data: $name" } }
    $clock.Restart()
    while (Test-Path $folder) {
        try { Remove-Item $folder -Recurse -Force }
        catch { if ($clock.Elapsed.TotalSeconds -gt 15) { throw }; Start-Sleep -Milliseconds 200 }
    }
    if (Test-Path $folder) { throw 'Portable retained locked files after shutdown.' }
    Write-Host 'PASS deleting the entire portable folder removes its browser and app data; installed data stays unchanged.'
} finally {
    foreach ($owned in @($close,$process)) {
        if ($owned) {
            if (!$owned.HasExited) { $owned.Kill($true); $owned.WaitForExit(10000) | Out-Null }
            $owned.Dispose()
        }
    }
    $stableMutex.Dispose()
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue }
}
