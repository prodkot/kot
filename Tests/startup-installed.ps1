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
$success=$false; $running=$null; $ownedFixtureName='KotVPN-S-1-0-0'
# Synthetic runner data only. Persist before cleanup, and print on failure because
# the workflow intentionally uploads no candidate after a failing installation.
$diagnostics=Join-Path $env:RUNNER_TEMP 'kot-reinstall-diagnostics'
New-Item -ItemType Directory -Force $diagnostics | Out-Null
$script:commandNumber=0
function Run($path,$argument,[switch]$Failure) {
  $script:commandNumber++
  $tag='{0:D3}' -f $script:commandNumber
  $log=Join-Path $diagnostics "$tag-setup.log"
  $maintenance=Join-Path $profile 'maintenance-error.txt'
  Remove-Item $maintenance -ErrorAction SilentlyContinue
  if ($path -eq $Setup) { $argument=$argument.Replace('/S ',"/S /LOG=$log ") }
  Write-Host "RUN $tag $([IO.Path]::GetFileName($path)) $argument"
  $watch=[Diagnostics.Stopwatch]::StartNew()
  $p=Start-Process $path -ArgumentList $argument -PassThru
  try {
    if (!$p.WaitForExit(120000)) { $p.Kill($true); $p.WaitForExit(10000) | Out-Null; throw 'Fixture process timed out.' }
    Write-Host "EXIT $tag code=$($p.ExitCode) elapsedMs=$($watch.ElapsedMilliseconds)"
    if (Test-Path $log) { Get-Content $log -Encoding Unicode | Write-Host }
    if ($p.ExitCode -ne 0) {
      if (Test-Path "$log.extract.txt") { Get-Content "$log.extract.txt" | Write-Host }
      if (Test-Path $maintenance) { Get-Content $maintenance | Write-Host }
      Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($target,[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($moved,[StringComparison]::OrdinalIgnoreCase)) } | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath | Format-Table | Out-Host
      Get-Service Schedule | Select-Object Name,Status | Format-Table | Out-Host
      Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue | Select-Object TaskName,State | Format-Table | Out-Host
    }
    if (!$Failure -and $p.ExitCode -ne 0) { throw "Fixture command failed: $argument ($($p.ExitCode))" }
    return $p.ExitCode
  } finally { $p.Dispose() }
}
function Assert($condition,$message) { if (!$condition) { throw $message }; Write-Host "PASS $message" }
function Assert-BuildIdentity($directory) {
  $stamp=Get-Content (Join-Path $directory 'build-info.json') -Raw | ConvertFrom-Json
  foreach ($file in @($Setup,(Join-Path $directory 'Kot.exe'),(Join-Path $directory 'Uninstall.exe'))) {
    Assert ((Get-Item $file).VersionInfo.ProductVersion -ceq $stamp.displayVersion) 'installed client, Setup and uninstaller share the build identity'
  }
  $arp=Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN'
  Assert ($arp.DisplayVersion -ceq $stamp.displayVersion -and $arp.InstallLocation -ieq $directory) 'installed programs show the exact build at the registered path'
}
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
    $stamp=Get-Content (Join-Path $target 'build-info.json') -Raw | ConvertFrom-Json
    Assert ($seen.buildNumber -ceq $stamp.build -and $seen.version -ceq $stamp.version) 'running scheduled client reports the packaged build identity'
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
  Assert-BuildIdentity $target
  Run $exe '--enable-startup' | Out-Null
  Launch-And-Observe
  Run $exe '--disable-startup' | Out-Null
  Assert (@(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue).Count -eq 0) 'disable removes the real startup task'
  Run $exe '--enable-startup' | Out-Null
  Disable-ScheduledTask -TaskName $name | Out-Null
  Run $Setup "/S /D=$target" | Out-Null
  Assert-BuildIdentity $target
  Assert (!(Get-ScheduledTask -TaskName $name).Settings.Enabled) 'same-version Setup respects external Windows disable'
  Run $exe '--enable-startup' | Out-Null
  # A fresh scheduler invocation in the same session tests repeatability, not a login trigger.
  Launch-And-Observe
  # Independent repetitions, never retry a failed installation. Every cycle must pass.
  $stamp=Get-Content (Join-Path $target 'build-info.json') -Raw | ConvertFrom-Json
  $expectedFiles=@(Get-ChildItem $target -Recurse -File | Where-Object { $_.FullName -notmatch '\\.kot-update-' } | ForEach-Object {
    [PSCustomObject]@{Path=$_.FullName; Hash=(Get-FileHash $_.FullName).Hash}
  })
  for ($cycle=1; $cycle -le 12; $cycle++) {
    Write-Host "REINSTALL CYCLE $cycle/12 begin"
    Run $exe '--enable-startup' | Out-Null
    Launch-And-Observe
    Run $exe '--disable-startup' | Out-Null
    Assert (@(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue).Count -eq 0) 'cycle disable removes task'
    Run $exe '--enable-startup' | Out-Null
    Disable-ScheduledTask -TaskName $name | Out-Null
    Run $Setup "/S /D=$target" | Out-Null
    Assert (!(Get-ScheduledTask -TaskName $name).Settings.Enabled) 'cycle reinstall preserves external disable'
    foreach ($file in $expectedFiles) { if (!(Test-Path $file.Path) -or (Get-FileHash $file.Path).Hash -ne $file.Hash) { throw "Cycle payload mismatch: $($file.Path)" } }
    Write-Host "PASS cycle exact payload hashes ($($expectedFiles.Count) files)"
    Assert (@(Get-ChildItem $target -Directory -Filter '.kot-update-*').Count -eq 0) 'cycle leaves no pending transaction'
    Write-Host "REINSTALL CYCLE $cycle/12 passed"
  }
  Run $exe '--enable-startup' | Out-Null
  Copy-Item $target $copy -Recurse
  $before=$folder.GetTask($name).Xml
  Assert ((Run (Join-Path $copy 'Kot.exe') '--enable-startup' -Failure) -ne 0) 'unregistered KotQA-like copy cannot take over installed autostart'
  Assert ($folder.GetTask($name).Xml -eq $before) 'rejected copy leaves the installed task unchanged'
  Run $Setup "/S /D=$moved" | Out-Null
  Assert-BuildIdentity $moved
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq (Join-Path $moved 'Kot.exe')) 'Setup at a new path repairs an enabled task'
  # A second task pointing at the old path must be cleaned without touching the current task.
  $definition=$service.NewTask(0); $definition.XmlText=$folder.GetTask($name).Xml
  $definition.Actions.Item(1).Path=$exe; $definition.Actions.Item(1).WorkingDirectory=$target
  $ownedFixture=$folder.RegisterTaskDefinition($ownedFixtureName,$definition,6,$sid,$null,3,$null)
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($ownedFixture) | Out-Null
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($definition) | Out-Null
  # Remove the old registration owner without disturbing the new one.
  Run (Join-Path $target 'Uninstall.exe') "/S _?=$target" | Out-Null
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq (Join-Path $moved 'Kot.exe')) 'old-copy uninstall preserves the new installation task'
  Assert (@(Get-ScheduledTask -TaskName $ownedFixtureName -ErrorAction SilentlyContinue).Count -eq 0) 'old-copy uninstall removes only tasks pointing at the removed executable'
  Assert ((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN').InstallLocation -ieq $moved) 'old-copy uninstall preserves the new registration'
  Run $Setup "/S /D=$target" | Out-Null
  Assert-BuildIdentity $target
  Run (Join-Path $moved 'Uninstall.exe') "/S _?=$moved" | Out-Null
  Assert ((Get-ScheduledTask -TaskName $name).Actions[0].Execute -ieq $exe) 'moving back keeps autostart on the installed executable'
  $success=$true
  Write-Host 'PASS scheduler launch and integration. Actual Windows sign-out/sign-in remains a separate user-PC test.'
} finally {
  if ($running) { try { if (!$running.HasExited) { $running.Kill($true); $running.WaitForExit(10000) | Out-Null } } finally { $running.Dispose() } }
  Unregister-ScheduledTask -TaskName $ownedFixtureName -Confirm:$false -ErrorAction SilentlyContinue
  if (!$success) { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }
  foreach ($dir in @($copy,$moved)) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
  # Only synthetic profile created by the fixture. Leave it for uninstall-preservation assertion.
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) | Out-Null
  [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
}
