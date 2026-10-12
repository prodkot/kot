param([string]$PublishDirectory = 'artifacts/portable-payload')
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$version=[regex]::Match([IO.File]::ReadAllText('Core/ClientIdentity.cs'),'Version\s*=\s*"([0-9.]+)"').Groups[1].Value
$publish=[IO.Path]::GetFullPath($PublishDirectory)
./build.ps1 -PublishDirectory $publish -Portable
if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }
$archive=Join-Path $PWD "artifacts/Kot-Portable-$version-Windows-x64.zip"
python Release/archive.py $publish $archive
if ($LASTEXITCODE -ne 0) { throw 'Portable archive creation failed.' }
$digest=(Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256","$digest  $([IO.Path]::GetFileName($archive))`n")
Write-Host "Portable ready: $archive"
