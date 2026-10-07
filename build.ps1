param([string]$Repository='')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# A fresh directory prevents old loose DLLs from surviving a new single-file publish.
$publish = Join-Path $PSScriptRoot ('publish-' + [Guid]::NewGuid().ToString('N'))
if ($Repository -and $Repository -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]{1,100}$') { throw 'Repository must be owner/repo' }
dotnet publish Windows/Kot.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true -o $publish "-p:UpdateRepository=$Repository"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$cache = Join-Path $PSScriptRoot '.build-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
Invoke-WebRequest 'https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip' -OutFile "$cache/sing-box.zip"
if ((Get-FileHash "$cache/sing-box.zip" -Algorithm SHA256).Hash -ne "C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32") { throw "Unexpected sing-box archive checksum" }
Expand-Archive "$cache/sing-box.zip" "$cache/core" -Force
New-Item -ItemType Directory -Force "$publish/core" | Out-Null
Copy-Item "$cache/core/sing-box-1.14.2-windows-amd64/sing-box.exe","$cache/core/sing-box-1.14.2-windows-amd64/libcronet.dll" "$publish/core"
Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile "$publish/Install-WebView2.exe"
$signature=Get-AuthenticodeSignature "$publish/Install-WebView2.exe"
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 installer signature is not valid Microsoft signature.' }
Copy-Item README.md,README.txt,VALIDATION.txt,LICENSE.txt,THIRD-PARTY.txt,SECURITY-AUDIT.md $publish
Copy-Item Release/HOSTING.md "$publish/UPDATE-HOSTING.md"
Copy-Item Release/GITHUB.md "$publish/GITHUB-RELEASES.md"
Copy-Item licenses "$publish/licenses" -Recurse -Force
Write-Host "Build ready: $publish"
