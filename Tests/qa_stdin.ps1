param([Parameter(Mandatory)][string]$CorePath)
$ErrorActionPreference='Stop'
# Exercise the exact shipped function under Windows PowerShell 5.1, including
# a UTF-8 console with a BOM. No provider configuration or user state is used.
$source=Join-Path $PSScriptRoot '../QA/Checks.ps1'
$tokens=$null;$errors=$null
# The repository is UTF-8 without BOM; packaging adds the BOM for PowerShell 5.1.
# Read explicitly so ParseFile does not interpret Russian text as Windows ANSI.
$text=[IO.File]::ReadAllText($source,[Text.Encoding]::UTF8)
$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ('QA syntax error: '+(($errors | ForEach-Object {$_.Message}) -join '; ')) }
$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Core'},$false)
if (!$function) { throw 'QA Core function missing' }
. ([scriptblock]::Create($function.Extent.Text))
Add-Type -Path (Join-Path $PSScriptRoot '../QA/ProcessGuard.cs')
$script:guard=New-Object KotQaProcessGuard
$script:owned=New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$script:xray=[IO.Path]::GetFullPath($CorePath)
$script:work=[IO.Path]::GetDirectoryName($script:xray)
$saved=[Console]::InputEncoding
try {
    [Console]::InputEncoding=New-Object Text.UTF8Encoding($true)
    $before=[Console]::InputEncoding.GetPreamble().Length
    $tag=[string][char]0x51FA+[char]0x53E3
    $json=(' ' * 524288)+'{"outbounds":[{"protocol":"freedom","tag":"'+$tag+'"}]}'
    $valid=Core 'run -test -format json -c stdin:' 10 $json
    if ($valid.code -ne 0) { throw ('Synthetic stdin validation failed: '+$valid.output) }
    if ([Console]::InputEncoding.GetPreamble().Length -ne $before) { throw 'Console encoding was not restored' }
    $invalid=Core 'run -test -format json -c stdin:' 10 '{"outbounds":[{"protocol":"fixture-invalid"}]}'
    if ($invalid.code -eq 0 -or $invalid.output -notmatch 'unknown config id: fixture-invalid') { throw 'Invalid JSON configuration was not rejected for its real error' }
    Write-Host 'PASS shipped QA stdin: large Unicode JSON, UTF-8 BOM console, encoding restored, invalid outbound rejected'
} finally {
    foreach ($p in $owned) { try { if (!$p.HasExited) { $p.Kill();$p.WaitForExit(10000)|Out-Null } } finally { $p.Dispose() } }
    $guard.Dispose();[Console]::InputEncoding=$saved
}
