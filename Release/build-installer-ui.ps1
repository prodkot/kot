param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $Output | Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Installer UI requires the Visual Studio C++ build tools.' }
$environment=Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars32.bat'
$source=[IO.Path]::GetFullPath('Installer/UI/KotUI.cpp'); $definition=[IO.Path]::GetFullPath('Installer/UI/KotUI.def')
# NSIS uses an x86 Unicode shell, while the installed client is native x64.
$command='call "'+$environment+'" >nul && cl /nologo /LD /O2 /MT /EHsc /W4 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "'+$source+'" /Fo"'+$Output+'\KotUI.obj" /link /DEF:"'+$definition+'" /OUT:"'+$Output+'\KotUI.dll" user32.lib gdi32.lib comctl32.lib uxtheme.lib'
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0 -or !(Test-Path "$Output/KotUI.dll")) { throw 'Installer UI compilation failed.' }
