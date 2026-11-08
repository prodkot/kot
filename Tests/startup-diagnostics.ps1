param([switch]$ParseOnly)
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '../QA/Startup-Diagnostics.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($script,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$allowed=@('Failure','SamePath','VersionOf','IdentityMatches','SafeDuration','TaskEvents','New-Object','Get-WinEvent','Get-Date',
 'ForEach-Object','Where-Object','Select-Object','Get-ScheduledTask','Get-ScheduledTaskInfo','Test-Path','Get-Process','Start-Sleep',
 'Join-Path','Get-Item','Get-ChildItem','ConvertTo-Json','Get-Content','ConvertFrom-Json')
foreach ($command in $ast.FindAll({param($a) $a -is [Management.Automation.Language.CommandAst]},$true)) {
  if ($command.GetCommandName() -notin $allowed) { throw 'Diagnostic contains an unexpected command.' }
}
$forbidden='(?i)(WriteAll|WriteLine|CreateDirectory|SetValue|Delete|\.Start\(|\.Kill\(|Start-Process|Set-Item|Set-ExecutionPolicy|Register-ScheduledTask|Invoke-Expression|Download|Upload)'
if (($ast.FindAll({param($a) $a -is [Management.Automation.Language.InvokeMemberExpressionAst]},$true) | ForEach-Object { $_.Extent.Text }) -match $forbidden) { throw 'Diagnostic contains a mutating method.' }
Write-Host 'PASS diagnostic AST accepts only read operations and stdout serialization'
if ($ParseOnly) { return }
$json=& $script
$result=$json | ConvertFrom-Json
if ($result.schema -ne 'kot.startup-diagnostics.v1' -or !$result.capturedUtc -or !$result.limitations) { throw 'Incomplete diagnostic result.' }
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
foreach ($secret in @($sid,$env:USERPROFILE,$env:COMPUTERNAME)) {
  if ($secret -and ($json -join "`n").Contains($secret)) { throw 'Private machine identifier leaked in diagnostic.' }
}
if (($json -join "`n") -match '(?i)(https?://|[a-z]:\\\\|\\\\users\\\\|S-1-5-21-)') { throw 'Diagnostic exposed a URL, path or SID.' }
Write-Host 'PASS real Windows read-only diagnostic schema and privacy checks'
