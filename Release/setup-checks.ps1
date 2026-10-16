param(
  [Parameter(Mandatory)][string]$Setup,
  [Parameter(Mandatory)][string]$Extractor,
  [Parameter(Mandatory)][string]$Publish,
  [Parameter(Mandatory)][string]$Version,
  [switch]$LegacyUpdate
)
$ErrorActionPreference='Stop'
$Setup=[IO.Path]::GetFullPath($Setup); $Extractor=[IO.Path]::GetFullPath($Extractor); $Publish=[IO.Path]::GetFullPath($Publish)
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('kot setup проверка ' + [Guid]::NewGuid().ToString('N'))
$backup=$null
New-Item -ItemType Directory $scratch | Out-Null

function Run-Checked([string]$Exe, [string[]]$Arguments, [string]$Directory, [int]$Timeout=60000, [switch]$AllowFailure) {
  $start=[Diagnostics.ProcessStartInfo]::new($Exe)
  $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WorkingDirectory=$Directory
  foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
  $process=[Diagnostics.Process]::Start($start)
  try {
    if (!$process.WaitForExit($Timeout)) { throw "Process timed out: $Exe" }
    if (!$AllowFailure -and $process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $Exe" }
    return $process.ExitCode
  } finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
    $process.Dispose()
  }
}
function Extract([string]$Package, [string]$Target, [string]$Expected) {
  $errorFile=Join-Path $scratch 'extract-error.txt'
  Remove-Item $errorFile -Force -ErrorAction SilentlyContinue
  $result=Run-Checked $Extractor @('--extract',$Package,$Target,$Expected,$errorFile) $scratch -AllowFailure
  if ($result -ne 0) {
    $detail=if (Test-Path $errorFile) { [IO.File]::ReadAllText($errorFile) } else { 'No error file.' }
    throw "Native extraction failed ($result): $detail"
  }
}
function Verify-Files([string]$Target) {
  foreach ($file in Get-ChildItem $Publish -File -Recurse) {
    $installed=Join-Path $Target ([IO.Path]::GetRelativePath($Publish,$file.FullName))
    if (!(Test-Path $installed) -or (Get-FileHash $installed).Hash -ne (Get-FileHash $file.FullName).Hash) { throw "Installed file mismatch: $installed" }
  }
}
function Remove-TestDirectory([string]$Directory) {
  # WebView2 can release its cache shortly after the app exits. Wait only for
  # this fixture's files, with a strict deadline instead of killing all browsers.
  $timer=[Diagnostics.Stopwatch]::StartNew()
  while (Test-Path -LiteralPath $Directory) {
    try { Remove-Item -LiteralPath $Directory -Recurse -Force; return }
    catch [IO.IOException], [UnauthorizedAccessException] {
      if ($timer.Elapsed.TotalSeconds -ge 30) { throw }
      Start-Sleep -Milliseconds 250
    }
  }
}
try {
  # NativeAOT must produce a native PE, rather than a second bundled CLR.
  $pe=[IO.File]::ReadAllBytes($Extractor)
  $header=[BitConverter]::ToInt32($pe,0x3c)
  if ([BitConverter]::ToUInt16($pe,$header+24) -ne 0x20b -or [BitConverter]::ToUInt32($pe,$header+24+112+14*8) -ne 0) { throw 'Extractor is not a native x64 PE.' }
  $stream=[IO.File]::OpenRead($Setup)
  try {
    $stream.Position=$stream.Length-24; $footer=[byte[]]::new(24); $stream.ReadExactly($footer)
    $length=[BitConverter]::ToInt32($footer,0); $sigLength=[BitConverter]::ToInt32($footer,4)
    $stream.Position=$stream.Length-24-$length-$sigLength; $json=[byte[]]::new($length); $stream.ReadExactly($json)
    $manifest=[Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json
    if ($manifest.installerLength -gt 8MB -or $manifest.archiveLength -le $manifest.installerLength) { throw 'Setup contains an unexpectedly large installer prefix.' }
    Write-Host "Single payload Setup: shell $($manifest.installerLength) bytes, signed ZIP $($manifest.archiveLength) bytes, total $($stream.Length) bytes."
  } finally { $stream.Dispose() }

  $target=Join-Path $scratch 'установка с пробелами'
  Extract $Setup $target $Version
  Verify-Files $target
  [IO.File]::WriteAllText((Join-Path $target 'user-note.txt'),'preserve this file')
  Extract $Setup $target $Version
  Verify-Files $target
  if ([IO.File]::ReadAllText((Join-Path $target 'user-note.txt')) -ne 'preserve this file' -or (Get-ChildItem $target -Directory -Filter '.kot-update-*').Count) { throw 'Reinstall changed user files or left a recovery folder.' }
  Write-Host 'PASS native signed extraction and same-version reinstall with Unicode and spaces in paths.'

  $bad=Join-Path $scratch 'changed-setup.exe'; Copy-Item $Setup $bad
  $stream=[IO.File]::Open($bad,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite)
  try { $stream.Position=4; $value=$stream.ReadByte(); $stream.Position=4; $stream.WriteByte($value -bxor 1) } finally { $stream.Dispose() }
  $errorFile=Join-Path $scratch 'rejected.txt'
  $result=Run-Checked $Extractor @('--extract',$bad,$target,$Version,$errorFile) $scratch -AllowFailure
  if ($result -eq 0 -or !(Test-Path $errorFile)) { throw 'Native helper accepted a damaged Setup.' }
  Verify-Files $target
  if ([IO.File]::ReadAllText((Join-Path $target 'user-note.txt')) -ne 'preserve this file') { throw 'Rejected Setup changed user files.' }
  Write-Host 'PASS native helper rejects a changed installer before replacing any files.'

  if ($LegacyUpdate) {
    # This launches the authentic 0.6.1 updater and the new UI. Run only in a
    # disposable Windows test account, never over an existing user profile.
    $profile=Join-Path $env:LOCALAPPDATA 'KotVPN'
    foreach ($name in 'profile.bin','device.bin','traffic-history.json') {
      if (Test-Path (Join-Path $profile $name)) { throw 'Legacy update test requires a fresh Windows test account. Existing user data was left untouched.' }
    }
    $backup=Join-Path $scratch 'original-profile'; $ownedProfile=$false; $legacy=Join-Path $scratch 'legacy client'; $parent=$null
    try {
      if (Test-Path $profile) { Move-Item $profile $backup }
      New-Item -ItemType Directory $profile | Out-Null; $ownedProfile=$true
      Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
      $oldProfile='{"name":"Setup upgrade fixture","nodes":[],"selected":"","theme":"light","accent":"purple","mode":"smart","connectionMode":"proxy","startup":false,"autoConnect":false,"tray":true,"killSwitch":false,"sendHwid":false,"subscriptions":[],"favorites":[]}'
      $profileBytes=[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($oldProfile),[Text.Encoding]::UTF8.GetBytes('KotVPN.profile.v1'),[Security.Cryptography.DataProtectionScope]::CurrentUser)
      $deviceBytes=[Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::ASCII.GetBytes(('C'*64)),[Text.Encoding]::UTF8.GetBytes('KotVPN.device.v1'),[Security.Cryptography.DataProtectionScope]::CurrentUser)
      $traffic='{"version":1,"firstRecorded":"2026-10-11T00:00:00+00:00","allTime":{"download":8192,"upload":1024,"connectedMilliseconds":90000,"sessions":2},"lastSession":null,"days":[]}'
      [IO.File]::WriteAllBytes((Join-Path $profile 'profile.bin'),$profileBytes)
      [IO.File]::WriteAllBytes((Join-Path $profile 'device.bin'),$deviceBytes)
      [IO.File]::WriteAllText((Join-Path $profile 'traffic-history.json'),$traffic)

      $oldSetup=Join-Path $scratch 'Kot-Setup-0.6.1-Windows-x64.exe'
      Invoke-WebRequest 'https://github.com/prodkot/kot/releases/download/v0.6.1/Kot-Setup-0.6.1-Windows-x64.exe' -OutFile $oldSetup -TimeoutSec 120 -MaximumRetryCount 2 -RetryIntervalSec 2
      # The pinned publisher key validates both the old Setup and its payload.
      Extract $oldSetup $legacy '0.6.1'
      if ((Get-Item (Join-Path $legacy 'Kot.exe')).VersionInfo.ProductVersion -notlike '0.6.1*') { throw 'Legacy fixture does not contain the released 0.6.1 client.' }
      $work=Join-Path $legacy ('.kot-update-'+[Guid]::NewGuid().ToString('N'))
      New-Item -ItemType Directory $work | Out-Null
      Copy-Item (Join-Path $legacy 'Kot.exe') (Join-Path $work 'Updater.exe')
      Copy-Item $Setup (Join-Path $work 'package.exe')
      [IO.File]::WriteAllText((Join-Path $legacy 'user-note.txt'),'keep through automatic update')
      $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/cmd.exe'))
      $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.ArgumentList.Add('/c'); $start.ArgumentList.Add('ping -n 2 127.0.0.1 >nul')
      $parent=[Diagnostics.Process]::Start($start)
      $request=[ordered]@{Version=$Version;ParentPid=$parent.Id;ParentStarted=$parent.StartTime.ToUniversalTime().Ticks;Resume=$false;Startup=$false}
      [IO.File]::WriteAllText((Join-Path $work 'request.json'),($request | ConvertTo-Json -Compress))
      if (!$parent.WaitForExit(10000)) { throw 'Legacy updater parent fixture did not exit.' }
      Run-Checked (Join-Path $work 'Updater.exe') @('--apply-update') $work -Timeout 150000 | Out-Null
      if (Test-Path (Join-Path $work 'error.txt')) { throw ([IO.File]::ReadAllText((Join-Path $work 'error.txt'))) }
      if ((Test-Path $work) -and !(Test-Path (Join-Path $work 'committed'))) { throw 'Legacy updater did not commit the new UI receipt.' }
      Verify-Files $legacy
      if ([IO.File]::ReadAllText((Join-Path $legacy 'user-note.txt')) -ne 'keep through automatic update') { throw 'Automatic update changed a user file.' }
      if (!(Test-Path (Join-Path $profile 'device.bin')) -or (Get-FileHash (Join-Path $profile 'device.bin')).Hash -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($deviceBytes))) { throw 'Automatic update changed the existing encrypted HWID.' }
      $newProfile=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $profile 'profile.bin')),[Text.Encoding]::UTF8.GetBytes('KotVPN.profile.v1'),[Security.Cryptography.DataProtectionScope]::CurrentUser)) | ConvertFrom-Json
      $history=Get-Content (Join-Path $profile 'traffic-history.json') -Raw | ConvertFrom-Json
      if ($newProfile.name -ne 'Setup upgrade fixture' -or $newProfile.theme -ne 'light' -or $newProfile.sendHwid -ne $false -or $history.allTime.download -ne 8192 -or $history.allTime.sessions -ne 2) { throw 'Automatic update changed the old profile or traffic history.' }
      Write-Host 'PASS authentic 0.6.1 updater installs the single payload Setup, launches the new WebView2 UI and commits; encrypted HWID, preferences, history and user files are preserved.'
    } finally {
      if ($null -ne $parent) { if (!$parent.HasExited) { $parent.Kill($true); $parent.WaitForExit(5000) | Out-Null }; $parent.Dispose() }
      if ($ownedProfile) {
        try { if (Test-Path (Join-Path $legacy 'Kot.exe')) { Run-Checked (Join-Path $legacy 'Kot.exe') @('--shutdown') $legacy -Timeout 40000 | Out-Null } }
        finally {
          # Confine cleanup to app processes created from this private fixture.
          foreach ($app in [Diagnostics.Process]::GetProcessesByName('Kot')) {
            try { if ($app.MainModule.FileName -eq (Join-Path $legacy 'Kot.exe') -and !$app.HasExited) { $app.Kill($true); $app.WaitForExit(10000) | Out-Null } } finally { $app.Dispose() }
          }
          Remove-TestDirectory $profile
          if (Test-Path $backup) { Move-Item $backup $profile }
        }
      }
    }
  }
} finally {
  if ($backup -and (Test-Path -LiteralPath $backup)) {
    Write-Warning "The original test-account folder is preserved at $backup."
  } else { Remove-TestDirectory $scratch }
}
