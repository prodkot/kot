# MUTATING integration fixture, disposable GitHub Windows runner ONLY.
param([Parameter(Mandatory)][string]$InstallDirectory,[Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$env:RUNNER_TEMP) { throw 'Disposable GitHub runner required.' }
$target=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (!$target.StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP),[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture target must be inside runner temp.' }
$exe=Join-Path $target 'Kot.exe'; $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$name='KotVPN-'+$sid; $service=New-Object -ComObject Schedule.Service; $service.Connect(); $folder=$service.GetFolder('\')
$old=@(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)
if ($old.Count) { throw 'Fixture refuses to overwrite an existing startup task.' }
$profile=Join-Path $env:LOCALAPPDATA 'KotVPN'
foreach ($file in 'profile.bin','device.bin') { if (Test-Path (Join-Path $profile $file)) { throw 'Fixture requires an account without user data.' } }
$receipt=Join-Path $profile 'startup-lifecycle.json'
$moved=Join-Path $env:RUNNER_TEMP ('kot moved тест '+[Guid]::NewGuid().ToString('N'))
$copy=Join-Path $env:RUNNER_TEMP ('KotQA '+[Guid]::NewGuid().ToString('N'))
$success=$false; $running=$null
function Run($path,$argument,[switch]$Failure) {
  $p=Start-Process $path -ArgumentList $argument -PassThru
  try {
    if (!$p.WaitForExit(120000)) { $p.Kill($true); throw 'Fixture process timed out.' }
    if (!$Failure -and $p.ExitCode -ne 0) { throw "Fixture command failed: $argument ($($p.ExitCode))" }
    return $p.ExitCode
  } finally { $p.Dispose() }
}
function Assert($condition,$message) { if (!$condition) { throw $message }; Write-Host "PASS $message" }
function Launch-And-Observe {
  if (Test-Path $receipt) { Remove-Item $receipt }
  $task=$folder.GetTask($name)
  $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
  if ($session -le 0) { throw 'A logged-on interactive Windows session is required for this fixture.' }
  # TASK_RUN_USE_SESSION_ID, respects configured conditions. This is NOT a real logoff/logon.
  $instance=$task.RunEx($null,4,$session,$null)
  try {
    $watch=[Diagnostics.Stopwatch]::StartNew(); $seen=$null
    while ($watch.Elapsed.TotalSeconds -lt 60) {
      if (Test-Path $receipt) {
        try { $seen=Get-Content $receipt -Raw | ConvertFrom-Json } catch { $seen=$null }
        if ($seen -and $seen.uiReady -and $seen.trayVisible -and $seen.stage -eq 'ready') { break }
      }
      Start-Sleep -Milliseconds 250
    }
    Assert ($seen -and $seen.uiReady -and $seen.trayVisible -and $seen.stage -eq 'ready') 'scheduled actual client reaches WebView ready with its tray icon enabled'
    $script:running=Get-Process -Id $seen.pid
    Assert ($script:running.SessionId -eq $session -and $script:running.Path -ieq $exe) 'scheduled client runs the installed executable in the interactive user session'
    Start-Sleep -Seconds 15
    $script:running.Refresh()
    Assert (!$script:running.HasExited) 'scheduled client remains alive for 15 seconds after UI readiness'
    Run $exe '--shutdown' | Out-Null
    Assert ($script:running.WaitForExit(10000)) 'scheduled client shuts down cleanly'
    $script:running.Dispose(); $script:running=$null
  } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($instance) | Out-Null; [Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) | Out-Null }
}
try {
  Run $exe '--enable-startup' | Out-Null
  Launch-And-Observe
  Run $exe '--disable-startup' | Out-Null
  Assert (@(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue).Count -eq 0) 'disable removes the real startup task'
  Run $exe '--enable-startup' | Out-Null
  Disable-ScheduledTask -TaskName $name | Out-Null
  Run $Setup "/S /D=$target" | Out-Null
  Assert (!(Get-ScheduledTask -TaskName $name).Settings.Enabled) 'same-version Setup respects external Windows disable'
  Run $exe '--enable-startup' | Out-Null
  # A fresh scheduler invocation in the same session tests repeatability, not a login trigger.
  Launch-And-Observe
  Copy-Item $target $copy -Recurse
  $before=$folder.GetTask($name).Xml
  Assert ((Run (Join-Path $copy 'Kot.exe') '--enable-startup' -Failure) -ne 0) 'unregistered KotQA-like copy cannot take over installed autostart'
  Assert ($folder.GetTask($name).Xml -eq $before) 'rejected copy leaves the installed task unchanged'
  Run $Setup "/S /D=$moved" | Out-Null
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq (Join-Path $moved 'Kot.exe')) 'Setup at a new path repairs an enabled task'
  # Remove the old registration owner without disturbing the new one.
  Run (Join-Path $target 'Uninstall.exe') "/S _?=$target" | Out-Null
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq (Join-Path $moved 'Kot.exe')) 'old-copy uninstall preserves the new installation task'
  Assert ((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN').InstallLocation -ieq $moved) 'old-copy uninstall preserves the new registration'
  Run $Setup "/S /D=$target" | Out-Null
  Run (Join-Path $moved 'Uninstall.exe') "/S _?=$moved" | Out-Null
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq $exe) 'moving back keeps autostart on the installed executable'
  $success=$true
  Write-Host 'PASS scheduler launch and integration. Actual Windows sign-out/sign-in remains a separate user-PC test.'
} finally {
  if ($running) { try { if (!$running.HasExited) { $running.Kill($true); $running.WaitForExit(10000) | Out-Null } } finally { $running.Dispose() } }
  if (!$success) { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }
  foreach ($dir in @($copy,$moved)) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
  # Only synthetic profile created by the fixture. Leave it for uninstall-preservation assertion.
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
}
