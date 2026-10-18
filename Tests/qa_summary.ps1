$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../QA/Report.ps1')
$report=@{checks=@(@{name='local';status='PASS'},@{name='tun-ipv6-dns-sleep-provider';status='NOT_TESTED'});manual=@(@{name='sleep-resume';status='PASS'},@{name='network-change';status='FAIL'},@{name='disconnect-exit';status='NOT_TESTED'})}
$before=$report|ConvertTo-Json -Depth 8
$summary=@(Get-QaSummary $report)
if ($summary[0] -notmatch 'PASS=1; FAIL=0;' -or $summary[1] -notmatch 'PASS=1; FAIL=1;' -or $summary[2] -notmatch 'NOT_TESTED') { throw 'Evidence summary mismatch' }
if (($report|ConvertTo-Json -Depth 8) -ne $before) { throw 'Summary mutated evidence' }
$steps=@(Get-ManualSteps)
if ($steps.Count -ne 5 -or @($steps | ForEach-Object {$_[0]} | Select-Object -Unique).Count -ne 5) { throw 'Manual scenarios must be separate' }
$report.manual=@()
if ((Get-QaSummary $report)[1] -notmatch 'PASS=0; FAIL=0;') { throw 'CI must not claim manual results' }
Write-Host 'PASS QA summary: separate automatic/manual evidence, five scenarios, no promotion or mutation'
