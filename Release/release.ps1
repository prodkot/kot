param([Parameter(Mandatory)][string]$Key,[string]$Repository='',[string]$Nsis=(Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'))
$ErrorActionPreference='Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (!(Test-Path $Nsis)) { throw 'Install NSIS 3 or pass -Nsis path/to/makensis.exe.' }
$version=[regex]::Match([IO.File]::ReadAllText('Core/ClientIdentity.cs'),'Version\s*=\s*"([0-9.]+)"').Groups[1].Value
if (!$version -or [IO.File]::ReadAllText('Windows/Kot.Windows.csproj') -notmatch "<Version>$([regex]::Escape($version))</Version>") { throw 'Version constants disagree.' }
if ([IO.File]::ReadAllText('Windows/app.manifest') -notmatch ('assemblyIdentity version="'+[regex]::Escape($version)+'.0"')) { throw 'Manifest version disagrees.' }
./build.ps1 -Repository $Repository
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$publish=(Get-ChildItem 'publish-*' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
./Release/sign.ps1 -Folder $publish -Version $version -Key $Key
New-Item -ItemType Directory -Force artifacts | Out-Null
$archive=Join-Path $PWD "artifacts/Kot-Client-$version-beta-Windows-x64.zip"
python Release/archive.py $publish $archive
if ($LASTEXITCODE -ne 0) { throw 'Update archive creation failed.' }
dotnet run --project Release/VerifyPackage/Kot.Verify.csproj -c Release -- $archive $version
if ($LASTEXITCODE -ne 0) { throw 'The client rejected the generated update package.' }
python Release/installer-files.py $publish Installer/uninstall-files.nsh
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller file list generation failed.' }
$setup=Join-Path $PWD "artifacts/Kot-Setup-$version-Windows-x64.exe"
& $Nsis "/WX" "/DVERSION=$version" "/DPAYLOAD=$publish" "/DOUTPUT=$setup" Installer/Kot.nsi
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
./Release/setup.ps1 -Setup $setup -Archive $archive -Version $version -Key $Key
dotnet run --project Release/VerifyPackage/Kot.Verify.csproj -c Release -- $setup $version
if ($LASTEXITCODE -ne 0) { throw 'The client rejected the signed Setup.' }
Remove-Item $archive -Force
Write-Host "Release ready: $setup"
