param([string]$PublishDirectory,[switch]$Portable)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
python Tests/repository.py
if ($LASTEXITCODE -ne 0) { throw 'Repository checks failed.' }
# A fresh directory prevents old loose DLLs from surviving a new single-file publish.
$publish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $PSScriptRoot ('publish-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path $publish) { throw 'Publish directory must be new; existing files will not be overwritten.' }
$singleFile=(-not $Portable.IsPresent).ToString().ToLowerInvariant()
dotnet publish Windows/Kot.Windows.csproj -c Release -r win-x64 --self-contained true -m:1 "-p:KotPortable=$($Portable.IsPresent.ToString().ToLowerInvariant())" "-p:PublishSingleFile=$singleFile" "-p:IncludeNativeLibrariesForSelfExtract=$singleFile" -p:EnableWindowsTargeting=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$cache = Join-Path $PSScriptRoot '.build-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
Invoke-WebRequest 'https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip' -OutFile "$cache/sing-box.zip"
if ((Get-FileHash "$cache/sing-box.zip" -Algorithm SHA256).Hash -ne "C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32") { throw "Unexpected sing-box archive checksum" }
Expand-Archive "$cache/sing-box.zip" "$cache/core" -Force
New-Item -ItemType Directory -Force "$publish/core" | Out-Null
Copy-Item "$cache/core/sing-box-1.14.2-windows-amd64/sing-box.exe","$cache/core/sing-box-1.14.2-windows-amd64/libcronet.dll" "$publish/core"
if ($Portable) {
    $fixed=Get-Content Release/webview2-fixed.json -Raw | ConvertFrom-Json
    $cab=Join-Path $cache "WebView2-$($fixed.version)-x64.cab"
    if (!(Test-Path $cab) -or (Get-FileHash $cab -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixed.sha256) { Invoke-WebRequest $fixed.url -OutFile $cab }
    if ((Get-FileHash $cab -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixed.sha256) { throw 'Unexpected Fixed Version WebView2 checksum.' }
    $expanded=Join-Path $cache ('webview-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory $expanded | Out-Null
        & expand.exe $cab '-F:*' $expanded | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Fixed Version WebView2 expansion failed.' }
        $browser=Get-ChildItem $expanded -Filter msedgewebview2.exe -Recurse | Select-Object -First 1
        if (!$browser) { throw 'Fixed Version WebView2 executable is missing.' }
        $signature=Get-AuthenticodeSignature $browser.FullName
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation' -or $browser.VersionInfo.FileVersion -ne $fixed.version) { throw 'Fixed Version WebView2 Microsoft signature or version differs.' }
        Move-Item $browser.Directory.FullName "$publish/WebView2Runtime"
    } finally { Remove-Item $expanded -Recurse -Force -ErrorAction SilentlyContinue }
} else {
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile "$publish/Install-WebView2.exe"
    $signature=Get-AuthenticodeSignature "$publish/Install-WebView2.exe"
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 installer signature is not valid Microsoft signature.' }
}
python Release/payload.py $publish
if ($LASTEXITCODE -ne 0) { throw 'Payload preparation failed.' }
if ($Portable) { Copy-Item docs/PORTABLE.md "$publish/README-portable.txt" }
Write-Host "Build ready: $publish"
