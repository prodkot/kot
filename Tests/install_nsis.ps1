$ErrorActionPreference='Stop'
$original=${env:ProgramFiles(x86)}
$testRoot=Join-Path $PSScriptRoot ('nsis-test-'+[Guid]::NewGuid().ToString('N'))
$scriptPath=Join-Path $PSScriptRoot '../Release/install-nsis.ps1'
New-Item -ItemType Directory -Path (Join-Path $testRoot 'NSIS') | Out-Null
try {
  ${env:ProgramFiles(x86)}=$testRoot
  $global:kotNsisCalls=0
  function global:choco { $global:kotNsisCalls++; $global:LASTEXITCODE=0 }
  function global:Start-Sleep {}
  $failed=$false
  try { & $scriptPath } catch { $failed=$_.Exception.Message -match 'after three attempts' }
  if (!$failed -or $global:kotNsisCalls -ne 3) { throw 'A successful exit without a compiler was not rejected after bounded retries.' }
  Write-Host 'PASS zero exit without compiler fails after exactly three attempts'
  $global:kotNsisCalls=0
  function global:choco {
    $global:kotNsisCalls++; $global:LASTEXITCODE=0
    if ($global:kotNsisCalls -eq 2) { [IO.File]::WriteAllText((Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'),'fixture') }
  }
  & $scriptPath
  if ($global:kotNsisCalls -ne 2 -or $LASTEXITCODE -ne 0) { throw 'Transient feed failure did not recover on the second attempt.' }
  Write-Host 'PASS transient missing compiler recovers on the second attempt'
  $global:kotNsisCalls=0; $global:LASTEXITCODE=1
  & $scriptPath
  if ($global:kotNsisCalls -ne 0 -or $LASTEXITCODE -ne 0) { throw 'Existing compiler did not return success without reinstalling.' }
  Write-Host 'PASS existing compiler needs no installation and clears a stale exit code'
} finally {
  ${env:ProgramFiles(x86)}=$original
  Remove-Item Function:/choco,Function:/Start-Sleep -ErrorAction SilentlyContinue
  # The generated absolute test directory is confined to this workspace.
  if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($testRoot)) -ne [IO.Path]::GetFullPath($PSScriptRoot)) { throw 'Unexpected test directory.' }
  Remove-Item -LiteralPath $testRoot -Recurse -Force
}
