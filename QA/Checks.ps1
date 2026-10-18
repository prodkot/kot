param([string]$InstallDirectory, [string]$ReportDirectory=$PSScriptRoot, [switch]$NonInteractive)
$ErrorActionPreference='Stop'
$report=[ordered]@{schema=1; commit=''; version=''; os=[Environment]::OSVersion.Version.ToString(); checks=@(); manual=@(); startup=@()}
$owned=New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$work=$null
$failed=$false
$lock=$null
$guard=$null
function Result([string]$name,[string]$status,[string]$detail='') {
    $script:report.checks+=@{name=$name;status=$status;detail=$detail}
    if ($status -eq 'FAIL') { $script:failed=$true }
    Write-Host "$status $name $detail"
}
function Port {
    $s=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    try { $s.Start(); return ([Net.IPEndPoint]$s.LocalEndpoint).Port } finally { $s.Stop() }
}
function Core([string]$arguments,[int]$seconds=0,[string]$json='') {
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$script:xray; $info.Arguments=$arguments; $info.WorkingDirectory=$script:work
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    if ($json.Length) { $info.RedirectStandardInput=$true }
    $info.EnvironmentVariables['XRAY_LOCATION_ASSET']=$script:work
    $p=New-Object Diagnostics.Process; $p.StartInfo=$info
    # .NET Framework constructs stdin with Console.InputEncoding and AutoFlush.
    # A UTF-8 console can therefore emit a BOM during Start, before even a raw
    # BaseStream write. Xray's stdin JSON reader rejects that leading BOM.
    $encoding=$null
    try {
        if ($json.Length) { $encoding=[Console]::InputEncoding;[Console]::InputEncoding=New-Object Text.UTF8Encoding($false) }
        if (!$p.Start()) { throw 'core-start' }
        $script:owned.Add($p)
        $script:guard.Add($p)
    } finally { if ($encoding) { [Console]::InputEncoding=$encoding } }
    # Drain both pipes; fixture logging is disabled and the run is bounded.
    $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
    if ($json.Length) {
        $bytes=[Text.Encoding]::UTF8.GetBytes($json)
        if (!$p.StandardInput.BaseStream.WriteAsync($bytes,0,$bytes.Length).Wait(10000)) { $p.Kill();$p.WaitForExit();throw 'core-stdin-timeout' }
        $p.StandardInput.Close()
    }
    if ($seconds) {
        if (!$p.WaitForExit($seconds*1000)) { $p.Kill(); $p.WaitForExit(); throw 'core-timeout' }
        return @{code=$p.ExitCode; output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()}
    }
    return $p
}
function Ready([int]$port,[Diagnostics.Process]$process) {
    $until=[DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $until) {
        if ($process.HasExited) { throw 'fixture-exit' }
        $s=New-Object Net.Sockets.TcpClient
        try {
            $connect=$s.ConnectAsync('127.0.0.1',$port)
            if ($connect.Wait(200) -and $s.Connected) { return }
        } catch {} finally { $s.Dispose() }
        Start-Sleep -Milliseconds 100
    }
    throw 'fixture-listener-timeout'
}
try {
    Write-Host 'Проверка kot: версии, файлы, WebView2 и локальный трафик Xray.'
    Write-Host 'Настройки и сеть не изменяются. Администратор не требуется. Отправки данных нет.'
    $expected=Get-Content (Join-Path $PSScriptRoot 'expected.json') -Raw | ConvertFrom-Json
    if ($expected.schema -ne 1 -or $expected.commit -notmatch '^[0-9a-f]{40}$') { throw 'manifest' }
    $report.commit=$expected.commit; $report.version=$expected.version
    [IO.Directory]::CreateDirectory($ReportDirectory) | Out-Null
    # The lock prevents a second run from deleting an active run's scratch files.
    $lock=[IO.File]::Open((Join-Path $ReportDirectory '.qa-lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    foreach ($old in Get-ChildItem $ReportDirectory -Directory -Filter '.kot-qa-*') {
        if ($old.Name -match '^\.kot-qa-[0-9a-f]{32}$' -and !($old.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Remove-Item $old.FullName -Recurse -Force }
    }
    $work=Join-Path $ReportDirectory ('.kot-qa-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($work) | Out-Null
    if (!$InstallDirectory) {
        foreach ($candidate in @((Join-Path $env:LOCALAPPDATA 'Programs\kot'),(Join-Path $env:LOCALAPPDATA 'Programs\Kot'),(Join-Path $env:ProgramFiles 'kot'))) {
            if (Test-Path (Join-Path $candidate 'Kot.exe')) { $InstallDirectory=$candidate;break }
        }
    }
    if (!$InstallDirectory -and !$NonInteractive) {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog=New-Object Windows.Forms.OpenFileDialog
        $dialog.Title='Выберите установленный Kot.exe'; $dialog.Filter='kot|Kot.exe'
        try { if ($dialog.ShowDialog() -eq 'OK') { $InstallDirectory=[IO.Path]::GetDirectoryName($dialog.FileName) } } finally { $dialog.Dispose() }
    }
    if (!$InstallDirectory) { throw 'installation-not-selected' }
    $InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory)
    $mismatch=0; $checked=0
    foreach ($entry in $expected.files.PSObject.Properties) {
        if ($entry.Name -match '(^/|\\|:|(^|/)\.\.(/|$))' -or $entry.Value -notmatch '^[0-9a-f]{64}$') { throw 'manifest-path' }
        $path=Join-Path $InstallDirectory $entry.Name
        $checked++
        if (!(Test-Path $path -PathType Leaf) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $entry.Value) { $mismatch++ }
    }
    Result 'exact-installed-payload' $(if ($mismatch) {'FAIL'} else {'PASS'}) "files=$checked mismatches=$mismatch"
    if ($mismatch) { throw 'payload-mismatch' }
    $xray=Join-Path $InstallDirectory 'core\xray.exe'
    Add-Type -Path (Join-Path $PSScriptRoot 'ProcessGuard.cs')
    $guard=New-Object KotQaProcessGuard
    $coreHash=(Get-FileHash $xray -Algorithm SHA256).Hash.ToLowerInvariant()
    $report.coreSha256=$coreHash
    $report.clientSha256=(Get-FileHash (Join-Path $InstallDirectory 'Kot.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $version=Core 'version' 10
    if ($version.code -ne 0 -or $version.output -notmatch ('Xray '+[regex]::Escape($expected.coreVersion)+' ') -or $version.output -notmatch [regex]::Escape($expected.goVersion)) { throw 'core-version' }
    Result 'xray-version' 'PASS' ($expected.coreVersion+' '+$expected.goVersion)
    $wv=@()
    foreach ($base in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients','HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients','HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients')) {
        if (Test-Path $base) {
            foreach ($key in Get-ChildItem $base) {
                $value=Get-ItemProperty $key.PSPath
                if ($value.name -match 'WebView2' -and $value.pv -match '^\d+\.\d+\.\d+\.\d+$') { $wv+=$value.pv }
            }
        }
    }
    Result 'webview2-runtime' $(if ($wv.Count) {'PASS'} else {'FAIL'}) (($wv | Select-Object -Unique) -join ',')
    # Only fixed fields from the new structured startup record are allowed out.
    $startupPath=Join-Path $env:LOCALAPPDATA 'KotVPN\startup-error.json'
    if ((Test-Path $startupPath) -and (Get-Item $startupPath).Length -le 16384) {
        try {
            $startup=Get-Content $startupPath -Raw | ConvertFrom-Json
            foreach ($e in @($startup.exceptions) | Select-Object -First 4) {
                if ($e.type -match '^(System|Microsoft|Kot)\.[A-Za-z0-9_.+`]+Exception$') {
                    $report.startup+=@{stage=$(if ($startup.stage -eq 'interface') {'interface'} else {'startup'});type=$e.type;hresult=[int]$e.hresult}
                }
            }
        } catch { Result 'startup-record' 'SKIP' 'unreadable' }
    }
    $ports=@();while ($ports.Count -lt 3) { $next=Port;if ($ports -notcontains $next) { $ports+=$next } }
    $serverPort=$ports[0];$proxyPort=$ports[1];$webPort=$ports[2]
    $id=[Guid]::NewGuid().ToString()
    $server=@{log=@{loglevel='none'};inbounds=@(@{listen='127.0.0.1';port=$serverPort;protocol='vless';settings=@{clients=@(@{id=$id});decryption='none'}});outbounds=@(@{protocol='freedom';settings=@{finalRules=@(@{action='allow';ip=@('127.0.0.0/8')})}})}
    $client=@{log=@{loglevel='none'};inbounds=@(@{listen='127.0.0.1';port=$proxyPort;protocol='http';settings=@{}});outbounds=@(@{protocol='vless';settings=@{vnext=@(@{address='127.0.0.1';port=$serverPort;users=@(@{id=$id;encryption='none'})})}})}
    $utf8=New-Object Text.UTF8Encoding($false)
    foreach ($name in @('server','client')) {
        $value=Get-Variable $name -ValueOnly
        [IO.File]::WriteAllText((Join-Path $work ($name+'.json')),($value|ConvertTo-Json -Depth 12 -Compress),$utf8)
        $validation=Core ('run -test -c '+$name+'.json') 10
        if ($validation.code -ne 0) { throw 'fixture-config-rejected' }
    }
    Result 'xray-config-acceptance' 'PASS' 'synthetic-loopback-only'
    $serverJson=$server|ConvertTo-Json -Depth 12 -Compress
    $clientJson=$client|ConvertTo-Json -Depth 12 -Compress
    $validation=Core 'run -test -format json -c stdin:' 10 ((' ' * 262144)+$clientJson)
    if ($validation.code -ne 0) { throw 'fixture-stdin-rejected' }
    Result 'xray-stdin-acceptance' 'PASS' 'large-json; no-runtime-file'
    Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
public static class KotQaOrigin {
    static TcpListener listener;
    static Task worker;
    public static void Start(int port) {
        listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        worker = Task.Run(() => {
            try { using (var c = listener.AcceptTcpClient()) {
                c.ReceiveTimeout = 10000; c.SendTimeout = 10000;
                using (var s = c.GetStream()) {
                    var b = new byte[8192]; s.Read(b,0,b.Length);
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 14\r\nConnection: close\r\n\r\nkot-qa-traffic");
                    s.Write(reply,0,reply.Length);
                }
            }} catch (SocketException) {} catch (ObjectDisposedException) {} catch (System.IO.IOException) {}
        });
    }
    public static void Stop() { if (listener != null) listener.Stop(); if (worker != null) worker.Wait(11000); }
}
'@
    [KotQaOrigin]::Start($webPort)
    $serverProcess=Core 'run -format json -c stdin:' 0 $serverJson;Ready $serverPort $serverProcess
    $clientProcess=Core 'run -format json -c stdin:' 0 $clientJson;Ready $proxyPort $clientProcess
    $socket=New-Object Net.Sockets.TcpClient
    try {
        $socket.ReceiveTimeout=10000;$socket.SendTimeout=10000
        if (!$socket.ConnectAsync('127.0.0.1',$proxyPort).Wait(5000)) { throw 'proxy-timeout' }
        $stream=$socket.GetStream()
        $bytes=[Text.Encoding]::ASCII.GetBytes("GET http://127.0.0.1:$webPort/ HTTP/1.1`r`nHost: 127.0.0.1`r`nConnection: close`r`n`r`n")
        $stream.Write($bytes,0,$bytes.Length)
        $reader=New-Object IO.StreamReader($stream)
        $response=$reader.ReadToEnd()
        if ($response -notmatch '^HTTP/1.[01] 200 ' -or !$response.EndsWith('kot-qa-traffic')) { throw 'traffic-response' }
        Result 'real-vless-traffic' 'PASS' 'loopback-only; not TUN or provider'
    } finally { $socket.Dispose() }
    Result 'tun-ipv6-dns-sleep-provider' 'NOT_TESTED' 'requires manual checks on this PC'
} catch {
    # No raw exception messages enter the report; they may contain private paths.
    $known=@('manifest','manifest-path','installation-not-selected','payload-mismatch','core-version','core-start','core-timeout','core-stdin-timeout','fixture-exit','fixture-listener-timeout','fixture-config-rejected','fixture-stdin-rejected','proxy-timeout','traffic-response')
    $code=if ($known -contains $_.Exception.Message) {$_.Exception.Message} else {'check-exception'}
    Result 'automatic-checks' 'FAIL' ($code+' hresult='+$_.Exception.HResult)
} finally {
    foreach ($p in $owned) { try { if (!$p.HasExited) { $p.Kill();$p.WaitForExit(10000)|Out-Null } } catch { $failed=$true } finally { $p.Dispose() } }
    if ($guard) { $guard.Dispose() }
    if ('KotQaOrigin' -as [type]) { [KotQaOrigin]::Stop() }
    if ($work -and (Test-Path $work)) { Remove-Item $work -Recurse -Force }
}
if (!$NonInteractive -and $lock) {
    Write-Host ''
    Write-Host 'Ручная часть добровольна. Вход в TUN запросит права администратора.'
    Write-Host 'Подключение, смена сети и сон временно прервут интернет. Сначала завершите важные загрузки.'
    Write-Host 'Восстановление: kot в трее > Отключить. Kill switch снимайте только для разрешения прямого интернета.'
    $steps=@(
        @('window-tray','Откройте kot, сверните в трей, откройте снова, повторно запустите ярлык: одно рабочее окно.'),
        @('provider-and-auto','Подключите свой сервер в TUN, откройте сайт. Полную конфигурацию выберите вручную. В Auto должны участвовать только обычные серверы.'),
        @('resume-and-cleanup','При подключении выполните сон/пробуждение или смену Wi-Fi. Проверьте восстановление. Затем Отключить и Выход: сеть восстановлена, значка в трее нет.')
    )
    foreach ($step in $steps) {
        Write-Host $step[1]
        $answer=Read-Host '1 = прошло, 2 = ошибка, 3 = пропущено'
        $status=switch ($answer) {'1' {'PASS'} '2' {'FAIL'} default {'NOT_TESTED'}}
        $report.manual+=@{name=$step[0];status=$status}
    }
}
try {
    if ($lock) {
        $out=Join-Path $ReportDirectory '.qa-report-content'
        [IO.Directory]::CreateDirectory($out)|Out-Null
        try {
            [IO.File]::WriteAllText((Join-Path $out 'report.json'),($report|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
            $zip=Join-Path $ReportDirectory 'qa-report.zip'
            if (Test-Path $zip) { Move-Item $zip (Join-Path $ReportDirectory 'qa-report.previous.zip') -Force }
            # Include only the allowlisted report, never a recursive user directory.
            Compress-Archive -LiteralPath (Join-Path $out 'report.json') -DestinationPath $zip
            Write-Host 'Готово: qa-report.zip в папке набора. Отправьте этот файл в чат.'
        } finally { Remove-Item $out -Recurse -Force }
    }
} finally { if ($lock) { $lock.Dispose() } }
if ($failed) { exit 1 }
