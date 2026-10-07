param([Parameter(Mandatory)][string]$PrivateKeyPath)
$ErrorActionPreference='Stop'
$path=[IO.Path]::GetFullPath($PrivateKeyPath)
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a private-key path outside the repository.' }
if (Test-Path $path) { throw 'Key already exists; refusing to overwrite.' }
$rsa=[Security.Cryptography.RSA]::Create(3072)
[IO.File]::WriteAllText($path,$rsa.ExportRSAPrivateKeyPem(),[Text.UTF8Encoding]::new($false))
$public=$rsa.ExportSubjectPublicKeyInfoPem()
$source="namespace Kot.Core;`npublic static class ReleaseKey`n{`n    public const string Public = `"`"`"`n$public`n`"`"`";`n}`n"
[IO.File]::WriteAllText((Join-Path $root 'Core/ReleaseKey.cs'),$source,[Text.UTF8Encoding]::new($false))
$rsa.Dispose()
Write-Host 'Generated your publisher key and updated Core/ReleaseKey.cs. Keep the private key secret and backed up.'
Write-Host 'Build and manually install this bootstrap once. Keep this same key for all future auto updates.'
