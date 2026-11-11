param([switch]$ParseOnly)
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot '../QA/Startup-Diagnostics.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($script,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$allowed=@('Failure','ReleaseCom','SafeChoice','TaskDate','PathShape','ActionFacts','TaskNameKind','ComTaskSummary','SamePath','VersionOf','IdentityMatches','SafeDuration','TaskEvents','New-Object','Get-WinEvent','Get-Date',
 'ForEach-Object','Where-Object','Select-Object','Get-ScheduledTask','Get-ScheduledTaskInfo','Test-Path','Get-Process','Start-Sleep',
 'Join-Path','Get-Item','Get-ChildItem','Get-Service','ConvertTo-Json','Get-Content','ConvertFrom-Json')
foreach ($command in $ast.FindAll({param($a) $a -is [Management.Automation.Language.CommandAst]},$true)) {
  if ($command.GetCommandName() -notin $allowed) { throw 'Diagnostic contains an unexpected command.' }
}
$forbidden='(?i)(WriteAll|WriteLine|CreateDirectory|SetValue|Delete|\.Start\(|\.Kill\(|\.Run(Ex)?\(|\.Stop\(|RegisterTask|NewTask\(|SetSecurityDescriptor|Start-Process|Set-Item|Set-ExecutionPolicy|Register-ScheduledTask|Invoke-Expression|Download|Upload)'
if (($ast.FindAll({param($a) $a -is [Management.Automation.Language.InvokeMemberExpressionAst]},$true) | ForEach-Object { $_.Extent.Text }) -match $forbidden) { throw 'Diagnostic contains a mutating method.' }
Write-Host 'PASS diagnostic AST accepts only read operations and stdout serialization'
if ($ParseOnly) { return }
$json=& $script
$result=$json | ConvertFrom-Json
if ($result.schema -ne 'kot.startup-diagnostics.v3' -or !$result.capturedUtc -or !$result.limitations) { throw 'Incomplete diagnostic result.' }
if ($result.taskRead.com -ne 'complete' -or $result.taskRead.cim -ne 'complete' -or $result.taskRead.source -ne 'com') { throw 'A healthy Windows runner must support both readers.' }
if (!$result.schedulerService.available -or $result.schedulerService.status -ne 'Running') { throw 'Service status is missing or wrong.' }
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
foreach ($secret in @($sid,$env:USERPROFILE,$env:COMPUTERNAME)) {
  if ($secret -and ($json -join "`n").Contains($secret)) { throw 'Private machine identifier leaked in diagnostic.' }
}
if (($json -join "`n") -match '(?i)(https?://|[a-z]:\\\\|\\\\users\\\\|S-1-5-21-)') { throw 'Diagnostic exposed a URL, path or SID.' }
Write-Host 'PASS real Windows read-only diagnostic schema and privacy checks'

# Mutations below belong to the test harness only, never the delivered diagnostic.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Fault-injection task fixtures require a disposable GitHub Windows runner.' }
$name='KotQA-diagnostics-'+[guid]::NewGuid().ToString('N')
$service=$null; $folder=$null; $definition=$null; $registered=$null
try {
  $service=New-Object -ComObject Schedule.Service; $service.Connect(); $folder=$service.GetFolder('\')
  $definition=$service.NewTask(0)
  $definition.Principal.UserId=$sid; $definition.Principal.LogonType=3; $definition.Principal.RunLevel=1
  $definition.Settings.Enabled=$false; $definition.Settings.Hidden=$true
  $definition.Settings.DisallowStartIfOnBatteries=$false; $definition.Settings.StopIfGoingOnBatteries=$false
  $definition.Settings.ExecutionTimeLimit='PT0S'; $definition.Settings.MultipleInstances=2
  $trigger=$definition.Triggers.Create(9); $trigger.UserId=$sid; $trigger.Enabled=$true
  $action=$definition.Actions.Create(0)
  $action.Path=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
  $action.Arguments='--startup'; $action.WorkingDirectory=[IO.Path]::GetDirectoryName($action.Path)
  $registered=$folder.RegisterTaskDefinition($name,$definition,6,$sid,$null,3,$null)
  $originalXml=$registered.Xml; $originalRun=$registered.LastRunTime

  # Same failure class as the real-PC report, with a nested code and deliberately
  # sensitive message. It must be diagnosed without echoing any private message.
  function Get-ScheduledTask { throw [System.SystemException]::new('PRIVATE-DIAGNOSTIC-MESSAGE C:\Users\PrivateUser https://private.invalid/token',
    [Runtime.InteropServices.COMException]::new('PRIVATE-INNER-MESSAGE',-2147024891)) }
  try { $fallbackJson=& $script; $fallback=$fallbackJson | ConvertFrom-Json }
  finally { Remove-Item Function:Get-ScheduledTask }
  if ($fallback.taskRead.cim -ne 'failed' -or $fallback.taskRead.com -ne 'complete' -or $fallback.taskRead.source -ne 'com') { throw 'COM did not recover a CIM failure.' }
  $fixture=@($fallback.tasks | Where-Object { $_.testName -and $_.actions[0].startupArgument -and $_.actions[0].exists -and $_.actions[0].workingDirectoryMatchesExecutable })
  if ($fixture.Count -ne 1 -or $fixture[0].enabled -or !$fixture[0].currentPrincipal -or !$fixture[0].triggers[0].currentUser -or $fixture[0].triggers[0].type -ne 'LogonTrigger') { throw 'Hidden disabled fixture was not collected correctly.' }
  if ($fixture[0].logonType -ne 'InteractiveToken' -or $fixture[0].runLevel -ne 'HighestAvailable' -or $fixture[0].conditions.executionTimeLimit -ne 'PT0S') { throw 'COM task definition lost important settings.' }
  if ($fixture[0].lastResult -notmatch '^0x[0-9A-F]{8}$') { throw 'Task result missing.' }
  $failure=@($fallback.readErrors | Where-Object { $_.stage -eq 'scheduled-tasks' })
  if ($failure.Count -ne 1 -or $failure[0].hresult -ne '0x80131501' -or !($failure[0].exceptionChain | Where-Object { $_.hresult -eq '0x80070005' })) { throw 'Nested error evidence was lost.' }
  foreach ($secret in @($sid,$env:USERPROFILE,$env:COMPUTERNAME,$name,'PRIVATE-','PrivateUser','private.invalid')) {
    if ($secret -and ($fallbackJson -join "`n").Contains($secret)) { throw 'Fallback leaked a private identifier or exception message.' }
  }
  if (($fallbackJson -join "`n") -match '(?i)(https?://|[a-z]:\\\\|S-1-5-21-)') { throw 'Fallback exposed a URL, path or SID.' }
  if ($registered.Xml -ne $originalXml -or $registered.LastRunTime -ne $originalRun -or $registered.Enabled) { throw 'Diagnostic changed or launched the task.' }
  Write-Host 'PASS actual COM reads hidden task after injected 0x80131501; preserves task, last-run state and privacy'

  # Real Windows PowerShell 5.1 regression: GetDirectoryName throws on a quoted
  # executable. Preserve the task summary even when path normalization fails.
  $plainExe=$action.Path
  $action.Path='"'+$plainExe+'"'
  [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($registered)
  $registered=$folder.RegisterTaskDefinition($name,$definition,6,$sid,$null,3,$null)
  $originalXml=$registered.Xml; $originalRun=$registered.LastRunTime
  $quotedJson=& $script; $quoted=$quotedJson | ConvertFrom-Json
  if ($quoted.taskRead.cim -ne 'complete' -or $quoted.taskRead.com -ne 'complete' -or $quoted.readErrors.Count) { throw 'Quoted executable broke task collection.' }
  $fixture=@($quoted.tasks | Where-Object { $_.testName -and $_.actions[0].quotedExecutable })
  if ($fixture.Count -ne 1 -or !$fixture[0].actions[0].normalizedExists -or $fixture[0].actions[0].exists -or $fixture[0].actions[0].literalPathError -ne '0x80070057') { throw 'Quoted task path evidence was lost.' }
  if (!$fixture[0].currentPrincipal -or !$fixture[0].triggers[0].currentUser -or $fixture[0].nameKind -ne 'test' -or !$fixture[0].lastResult) { throw 'A malformed path hid task identity or run history.' }
  if ($registered.Xml -ne $originalXml -or $registered.LastRunTime -ne $originalRun -or $registered.Enabled) { throw 'Quoted task was changed or launched.' }
  foreach ($secret in @($sid,$plainExe,$env:USERPROFILE,$env:COMPUTERNAME,$name)) {
    if ($secret -and ($quotedJson -join "`n").Contains($secret)) { throw 'Quoted task diagnostic leaked an identifier.' }
  }
  Write-Host 'PASS actual quoted action survives both CIM and COM on Windows PowerShell 5.1 with unchanged definition and last-run'

  # Exercise a range of malformed values without registering or launching them.
  # Load just the pure helpers from the diagnostic source under test.
  foreach ($functionAst in $ast.FindAll({param($a) $a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -in @('PathShape','ActionFacts','SamePath','VersionOf')},$true)) {
    . ([scriptblock]::Create($functionAst.Extent.Text))
  }
  $exe=$plainExe
  foreach ($badPath in @('', ' ', '"unterminated', 'C:\bad|path\Kot.exe', ('C:\bad'+[char]0+'path'), '\\private.invalid\share\Kot.exe', '"C:\not-installed\Kot.exe" --startup')) {
    $facts=ActionFacts $badPath '' '--startup' 'Exec'
    if ($facts.exists -or $facts.normalizedExists -or ($badPath.StartsWith('\\') -and $facts.localFixedDrive)) { throw 'Unsafe/nonexistent path was probed as an existing local executable.' }
    $encoded=$facts | ConvertTo-Json -Depth 5
    if ($encoded.Contains('private.invalid') -or $encoded.Contains('not-installed')) { throw 'Path helper leaked a raw path.' }
  }
  $quotedInline=ActionFacts ('"'+$plainExe+'" --startup') ([IO.Path]::GetDirectoryName($plainExe)) '' 'Exec'
  if (!$quotedInline.inlineArgumentsInExecutable -or !$quotedInline.normalizedRegisteredExecutable -or $quotedInline.registeredExecutable -or $quotedInline.startupArgument) { throw 'Normalized candidate confused with a healthy raw task action.' }
  Write-Host 'PASS malformed, empty, inline-argument and UNC paths retain sanitized evidence without aborting'

  # Both APIs unavailable: report unknown, not "task absent". Also distinguish the
  # service from a disabled operational log. Neither simulation starts/stops anything.
  function Get-ScheduledTask { throw [System.SystemException]::new('PRIVATE-CIM') }
  function New-Object {
    param([string]$TypeName,[string]$ComObject)
    if ($ComObject) { throw [Runtime.InteropServices.COMException]::new('PRIVATE-COM',-2147023174) }
    Microsoft.PowerShell.Utility\New-Object -TypeName $TypeName
  }
  function Get-WinEvent {
    param($ListLog,$FilterHashtable,$MaxEvents,$ErrorAction)
    if ($ListLog) { return [pscustomobject]@{IsEnabled=$false} }
  }
  try { $failed=& $script | ConvertFrom-Json }
  finally { Remove-Item Function:Get-ScheduledTask,Function:New-Object,Function:Get-WinEvent }
  if ($failed.taskRead.cim -ne 'failed' -or $failed.taskRead.com -ne 'failed' -or $failed.taskRead.source -ne 'none' -or $failed.taskRead.canonicalCurrentUser -ne 'unknown') { throw 'Failed readers misreported task absence.' }
  if ($failed.tasks.Count -ne 0 -or $failed.readErrors.Count -lt 2) { throw 'Failed readers lost failure evidence.' }
  if ($failed.scheduler.enabled -or $failed.schedulerService.status -ne 'Running') { throw 'Disabled event log confused with service status.' }
  Write-Host 'PASS double failure stays unknown; disabled event log is independent of running Scheduler service'
} finally {
  if ($folder) { try { $folder.DeleteTask($name,0) } catch { } }
  foreach ($obj in @($registered,$action,$trigger,$definition,$folder,$service)) {
    if ($obj -and [Runtime.InteropServices.Marshal]::IsComObject($obj)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($obj) }
  }
}
