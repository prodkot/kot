# Read-only, Windows PowerShell 5.1+. Writes a sanitized JSON report to stdout only.
# No profile decryption, raw paths, account names, SIDs, command lines or event messages.
param([ValidateRange(1,14)][int]$Days=3)
$ErrorActionPreference='Stop'
$issues=New-Object System.Collections.Generic.List[object]
function Failure($stage,$errorRecord) {
  # Exception messages/targets may contain account names, paths or subscription URLs.
  # Keep only known framework type names, numeric codes and the script line number.
  $chain=@(); $ex=$errorRecord.Exception
  for ($i=0; $ex -and $i -lt 5; $i++) {
    $name=$ex.GetType().FullName
    if ($name -notin @('System.SystemException','System.Exception','System.InvalidOperationException',
      'System.UnauthorizedAccessException','System.IO.FileNotFoundException','System.IO.DirectoryNotFoundException',
      'System.Runtime.InteropServices.COMException','System.ComponentModel.Win32Exception',
      'System.Management.Automation.RuntimeException','System.Management.Automation.CmdletInvocationException',
      'System.Management.Automation.MethodInvocationException','System.Management.Automation.CommandNotFoundException',
      'Microsoft.Management.Infrastructure.CimException')) { $name='Other' }
    $chain+=@{type=$name;hresult=('0x{0:X8}' -f ($ex.HResult -band 0xffffffffL))}
    $ex=$ex.InnerException
  }
  $issues.Add([ordered]@{stage=$stage;hresult=$chain[0].hresult;exceptionChain=$chain;
    category=[string]$errorRecord.CategoryInfo.Category;scriptLine=[int]$errorRecord.InvocationInfo.ScriptLineNumber})
}
function ReleaseCom($value) {
  if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
  }
}
function SafeChoice($value,$choices) { if ($value -in $choices) { return [string]$value }; return 'Other' }
function TaskDate($value) {
  if ($value -and ([DateTime]$value).Year -gt 2000) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
  return $null
}
function ComTaskSummary($task,$index) {
  $xml=New-Object System.Xml.XmlDocument
  $xml.XmlResolver=$null
  $xml.LoadXml($task.Xml)
  $root=$xml.DocumentElement
  $actions=@(foreach ($action in $root.Actions.ChildNodes) {
    $path=[string]$action.Command
    @{type=(SafeChoice $action.LocalName @('Exec','ComHandler','SendEmail','ShowMessage'));
      exists=[bool]($path -and (Test-Path -LiteralPath $path -PathType Leaf));registeredExecutable=(SamePath $path $exe);
      workingDirectoryMatchesExecutable=[bool]($path -and (SamePath ([string]$action.WorkingDirectory) ([IO.Path]::GetDirectoryName($path))));
      startupArgument=([string]$action.Arguments -ceq '--startup');version=(VersionOf $path)}
  })
  $triggers=@(foreach ($trigger in $root.Triggers.ChildNodes) {
    @{type=(SafeChoice $trigger.LocalName @('LogonTrigger','BootTrigger','TimeTrigger','CalendarTrigger','EventTrigger','IdleTrigger','SessionStateChangeTrigger','RegistrationTrigger'));
      enabled=([string]$trigger.Enabled -ne 'false');currentUser=(IdentityMatches ([string]$trigger.UserId));delay=(SafeDuration ([string]$trigger.Delay));
      hasStartBoundary=[bool]$trigger.StartBoundary;hasEndBoundary=[bool]$trigger.EndBoundary}
  })
  $s=$root.Settings; $p=$root.Principals.Principal
  return @{alias=('task-'+$index);canonicalCurrentUser=($task.Name -eq ('KotVPN-'+$sid));testName=($task.Name -match '^(KotQA|KotVPN-test-)');
    state=(SafeChoice ([string]$task.State) @('0','1','2','3','4'));stateSource='TASK_STATE';enabled=[bool]$task.Enabled;
    currentPrincipal=(IdentityMatches ([string]$p.UserId));
    logonType=(SafeChoice ([string]$p.LogonType) @('InteractiveToken','Password','S4U','InteractiveTokenOrPassword','ServiceAccount','Group','None'));
    runLevel=(SafeChoice ([string]$p.RunLevel) @('HighestAvailable','LeastPrivilege'));actions=$actions;triggers=$triggers;
    conditions=@{batteryBlocked=([string]$s.DisallowStartIfOnBatteries -ne 'false');stopOnBattery=([string]$s.StopIfGoingOnBatteries -ne 'false');
      networkRequired=([string]$s.RunOnlyIfNetworkAvailable -eq 'true');idleRequired=([string]$s.RunOnlyIfIdle -eq 'true');
      executionTimeLimit=(SafeDuration ([string]$s.ExecutionTimeLimit));startWhenAvailable=([string]$s.StartWhenAvailable -eq 'true');
      allowDemandStart=([string]$s.AllowStartOnDemand -ne 'false');multipleInstances=(SafeChoice ([string]$s.MultipleInstancesPolicy) @('IgnoreNew','Parallel','Queue','StopExisting'))};
    lastRunUtc=(TaskDate $task.LastRunTime);nextRunUtc=(TaskDate $task.NextRunTime);
    lastResult=('0x{0:X8}' -f ([long]$task.LastTaskResult -band 0xffffffffL));missedRuns=[int]$task.NumberOfMissedRuns}
}
function SamePath($a,$b) {
  if (!$a -or !$b) { return $false }
  try { return [IO.Path]::GetFullPath($a).TrimEnd('\') -ieq [IO.Path]::GetFullPath($b).TrimEnd('\') } catch { return $false }
}
function VersionOf($path) {
  try {
    $value=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    if ($value -match '^\d+\.\d+\.\d+(\.\d+)?') { return $Matches[0] }
  } catch { }
  return $null
}
function IdentityMatches($value) {
  if (!$value) { return $false }
  try {
    if ($value -match '^S-1-') { return $value -eq $sid }
    return ([Security.Principal.NTAccount]::new($value).Translate([Security.Principal.SecurityIdentifier])).Value -eq $sid
  } catch { return $false }
}
function SafeDuration($value) { if ($value -match '^P[0-9DT HMS.]+$') { return $value }; return $null }
function TaskEvents($logName,$ids) {
  $items=@()
  try {
    $log=Get-WinEvent -ListLog $logName
    if (!$log.IsEnabled) { return @{available=$true;enabled=$false;events=@()} }
    $filter=@{LogName=$logName;StartTime=(Get-Date).AddDays(-$Days)}
    if ($ids) { $filter.Id=$ids }
    # Bounded scan. No matches is distinct from a disabled or inaccessible log.
    $events=@(Get-WinEvent -FilterHashtable $filter -MaxEvents 500 -ErrorAction SilentlyContinue)
    foreach ($event in $events) {
      [xml]$xml=$event.ToXml()
      $values=@($xml.Event.EventData.Data | ForEach-Object { $_.'#text' })
      if (!($values | Where-Object { $_ -match '(?i)(\\KotVPN-|\\KotQA|\bKot\.exe\b)' })) { continue }
      $codes=@($xml.Event.EventData.Data | Where-Object { $_.Name -match '^(ResultCode|ErrorCode|Result|ExceptionCode)$' } | ForEach-Object {
        $v=$_.'#text'; if ($v -match '^(0x[0-9a-fA-F]{1,8}|\d{1,10})$') { $v }
      })
      $items+=@{id=$event.Id;utc=$event.TimeCreated.ToUniversalTime().ToString('o');level=$event.Level;codes=$codes}
    }
    return @{available=$true;enabled=$true;scanLimit=500;events=@($items | Select-Object -First 60)}
  } catch { Failure 'event-log' $_; return @{available=$false;events=@()} }
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid=$identity.User.Value; $admin=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$install=$null; $registeredVersion=$null
try {
  $registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
  try {
    $key=$registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN',$false)
    if ($key) { try { $install=$key.GetValue('InstallLocation'); $v=$key.GetValue('DisplayVersion'); if ($v -match '^\d+\.\d+\.\d+$') { $registeredVersion=$v } } finally { $key.Dispose() } }
  } finally { $registry.Dispose() }
} catch { Failure 'install-registration' $_ }
$exe=if ($install) { Join-Path $install 'Kot.exe' } else { $null }
$uninstaller=if ($install) { Join-Path $install 'Uninstall.exe' } else { $null }
$tasks=@(); $taskIndex=0
$taskRead=@{cim='failed';com='failed';source='none';scope='root-folder';canonicalCurrentUser='unknown'}
try {
  foreach ($task in @(Get-ScheduledTask -TaskPath '\' | Where-Object { $_.TaskName -match '^(KotVPN-|KotQA)' })) {
    $taskIndex++
    $info=$null; try { $info=Get-ScheduledTaskInfo -InputObject $task } catch { Failure 'task-info' $_ }
    $actions=@(foreach ($action in $task.Actions) {
      $path=$action.Execute
      @{type=$action.CimClass.CimClassName;exists=[bool]($path -and (Test-Path -LiteralPath $path -PathType Leaf));registeredExecutable=(SamePath $path $exe);
        workingDirectoryMatchesExecutable=[bool]($path -and (SamePath $action.WorkingDirectory ([IO.Path]::GetDirectoryName($path))));
        startupArgument=($action.Arguments -ceq '--startup');version=(VersionOf $path)}
    })
    $triggers=@(foreach ($trigger in $task.Triggers) {
      @{type=$trigger.CimClass.CimClassName;enabled=$trigger.Enabled;currentUser=(IdentityMatches $trigger.UserId);delay=(SafeDuration $trigger.Delay);
        hasStartBoundary=[bool]$trigger.StartBoundary;hasEndBoundary=[bool]$trigger.EndBoundary}
    })
    $s=$task.Settings
    $tasks+=@{alias=('task-'+$taskIndex);canonicalCurrentUser=($task.TaskName -eq ('KotVPN-'+$sid));testName=($task.TaskName -match '^(KotQA|KotVPN-test-)');
      state=[string]$task.State;enabled=$s.Enabled;currentPrincipal=(IdentityMatches $task.Principal.UserId);
      logonType=[string]$task.Principal.LogonType;runLevel=[string]$task.Principal.RunLevel;actions=$actions;triggers=$triggers;
      conditions=@{batteryBlocked=$s.DisallowStartIfOnBatteries;stopOnBattery=$s.StopIfGoingOnBatteries;networkRequired=$s.RunOnlyIfNetworkAvailable;
        idleRequired=$s.RunOnlyIfIdle;executionTimeLimit=(SafeDuration $s.ExecutionTimeLimit);startWhenAvailable=$s.StartWhenAvailable;allowDemandStart=$s.AllowDemandStart;multipleInstances=[string]$s.MultipleInstances};
      lastRunUtc=$(if ($info -and $info.LastRunTime.Year -gt 2000) { $info.LastRunTime.ToUniversalTime().ToString('o') });
      nextRunUtc=$(if ($info -and $info.NextRunTime.Year -gt 2000) { $info.NextRunTime.ToUniversalTime().ToString('o') });
      lastResult=$(if ($info) { '0x{0:X8}' -f ([long]$info.LastTaskResult -band 0xffffffffL) });missedRuns=$(if ($info) { $info.NumberOfMissedRuns })}
  }
  $taskRead.cim='complete'; $taskRead.source='cim'
} catch { Failure 'scheduled-tasks' $_ }
# The app uses the Scheduler COM API. Query it independently of the PowerShell/CIM
# provider, including hidden tasks. A failed enumeration is never proof of absence.
$service=$null; $folder=$null; $collection=$null; $canonicalTask=$null; $comTasks=@()
try {
  $service=New-Object -ComObject Schedule.Service
  $service.Connect()
  $folder=$service.GetFolder('\')
  try {
    $canonicalTask=$folder.GetTask('KotVPN-'+$sid)
    $taskRead.canonicalCurrentUser='present'
    $comTasks+=ComTaskSummary $canonicalTask 1
  } catch {
    $code=$_.Exception.GetBaseException().HResult -band 0xffffffffL
    if ($code -eq 0x80070002L -or $code -eq 0x80070003L) { $taskRead.canonicalCurrentUser='absent' }
    else { Failure 'canonical-task-com' $_ }
  } finally { ReleaseCom $canonicalTask }
  $collection=$folder.GetTasks(1)
  $taskRead.com='complete'
  for ($i=1; $i -le $collection.Count; $i++) {
    $item=$null
    try {
      $item=$collection.Item($i)
      if ($item.Name -notmatch '^(KotVPN-|KotQA)') { continue }
      if ($item.Name -eq ('KotVPN-'+$sid) -and ($comTasks | Where-Object { $_.canonicalCurrentUser })) { continue }
      $comTasks+=ComTaskSummary $item ($comTasks.Count+1)
    } catch { $taskRead.com='partial'; Failure 'task-com' $_ }
    finally { ReleaseCom $item }
  }
} catch { Failure 'scheduled-tasks-com' $_ }
finally { ReleaseCom $collection; ReleaseCom $folder; ReleaseCom $service }
if ($taskRead.com -eq 'complete' -or ($taskRead.cim -ne 'complete' -and $comTasks.Count)) {
  $tasks=$comTasks; $taskRead.source='com'
}
$schedulerService=@{available=$false}
try {
  $svc=Get-Service -Name Schedule
  try { $schedulerService=@{available=$true;status=[string]$svc.Status;startType=[string]$svc.StartType} }
  finally { $svc.Dispose() }
} catch { Failure 'scheduler-service' $_ }
$processes=@()
foreach ($p in @(Get-Process -Name Kot,KotQA -ErrorAction SilentlyContinue)) {
  try {
    $start=$p.StartTime.ToUniversalTime(); $path=$p.Path
    $processes+=@{pid=$p.Id;currentSession=($p.SessionId -eq $session);startedUtc=$start.ToString('o');
      ageSeconds=[int]([DateTime]::UtcNow-$start).TotalSeconds;registeredExecutable=(SamePath $path $exe);version=(VersionOf $path);aliveAfterSample=$false}
  } catch { Failure 'process' $_ } finally { $p.Dispose() }
}
if ($processes.Count) {
  Start-Sleep -Seconds 3
  foreach ($p in $processes) {
    $again=Get-Process -Id $p.pid -ErrorAction SilentlyContinue
    if ($again) { try { $p.aliveAfterSample=($again.StartTime.ToUniversalTime().ToString('o') -eq $p.startedUtc) } catch { } finally { $again.Dispose() } }
  }
}
$otherStartup=@()
foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder')) {
  try {
    if (Test-Path $root) {
      $key=Get-Item $root
      try { foreach ($name in $key.GetValueNames()) { if ($name -match '^Kot(\.|VPN|QA|$)') { $otherStartup+=@{source=($root -replace '^.*\\(Run|StartupFolder)$','$1');approved=$root.Contains('StartupApproved');present=$true} } } } finally { $key.Dispose() }
    }
  } catch { Failure 'alternate-startup' $_ }
}
$startupLinks=0
foreach ($special in @('Startup','CommonStartup')) {
  try { $folder=[Environment]::GetFolderPath($special); if ($folder -and (Test-Path $folder)) { $startupLinks+=@(Get-ChildItem -LiteralPath $folder -Filter '*kot*.lnk').Count } } catch { Failure 'startup-folder' $_ }
}
$lifecycle=$null
try {
  $path=Join-Path $env:LOCALAPPDATA 'KotVPN/startup-lifecycle.json'
  if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -le 4096) {
    $r=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($r.stage -in @('starting','ready','exited','failed') -and $r.version -match '^\d+\.\d+\.\d+$') {
      $lifecycle=@{stage=[string]$r.stage;version=[string]$r.version;pid=[int]$r.pid;uiReady=($r.uiReady -eq $true);trayVisible=($r.trayVisible -eq $true);
        startedUtc=([DateTime]$r.startedUtc).ToUniversalTime().ToString('o');updatedUtc=([DateTime]$r.updatedUtc).ToUniversalTime().ToString('o')}
    }
  }
} catch { Failure 'startup-receipt' $_ }
[ordered]@{schema='kot.startup-diagnostics.v2';capturedUtc=[DateTime]::UtcNow.ToString('o');elevated=$admin;
  powershellVersion=$PSVersionTable.PSVersion.ToString();is64BitProcess=[Environment]::Is64BitProcess;
  installed=@{registered=[bool]$install;displayVersion=$registeredVersion;clientVersion=(VersionOf $exe);uninstallerVersion=(VersionOf $uninstaller)};
  tasks=$tasks;taskRead=$taskRead;schedulerService=$schedulerService;processes=$processes;lifecycle=$lifecycle;alternateStartup=$otherStartup;startupLinkCount=$startupLinks;
  scheduler=(TaskEvents 'Microsoft-Windows-TaskScheduler/Operational' $null);
  application=(TaskEvents 'Application' @(1000,1001,1026));readErrors=@($issues.ToArray());
  limitations=@('No sign-in or launch is performed. Run after a failed sign-in before starting kot manually.',
    'Process sample is three seconds, not proof of successful UI or VPN connection.',
    'Disabled logs, access restrictions and the bounded event scan can hide historical failures.',
    'An empty tasks array with failed readers does not mean no tasks exist. The operational log enabled flag is not service status.')
} | ConvertTo-Json -Depth 9
