param([string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
python Tests/repository.py
if ($LASTEXITCODE -ne 0) { throw 'Repository checks failed.' }
# A fresh directory prevents old loose DLLs from surviving a new single-file publish.
$publish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $PSScriptRoot ('publish-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path $publish) { throw 'Publish directory must be new; existing files will not be overwritten.' }
dotnet publish Windows/Kot.Windows.csproj -c Release -r win-x64 --self-contained true -m:1 -p:EnableWindowsTargeting=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$cache = Join-Path $PSScriptRoot '.build-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
python Release/xray-core.py "$publish/core"
if ($LASTEXITCODE -ne 0) { throw 'Pinned native Xray build failed.' }
Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile "$publish/Install-WebView2.exe"
$signature=Get-AuthenticodeSignature "$publish/Install-WebView2.exe"
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 installer signature is not valid Microsoft signature.' }
python Release/payload.py $publish
if ($LASTEXITCODE -ne 0) { throw 'Payload preparation failed.' }
Write-Host "Build ready: $publish"
