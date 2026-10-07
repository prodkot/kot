param([Parameter(Mandatory)][string]$Folder,[Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Key)
$ErrorActionPreference='Stop'
$Folder=(Resolve-Path $Folder).Path
$Key=(Resolve-Path $Key).Path
if ($Key.StartsWith($Folder + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the private key outside published files.' }
$rsa=[Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem([IO.File]::ReadAllText($Key))
$public=[Security.Cryptography.RSA]::Create()
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Core/ReleaseKey.cs'))
$public.ImportFromPem([regex]::Match($source,'(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value)
if ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())) { throw 'Signing key does not match the public key pinned in the client.' }
$files=[ordered]@{}
Get-ChildItem $Folder -Recurse -File | Sort-Object FullName | ForEach-Object {
  $relative=[IO.Path]::GetRelativePath($Folder,$_.FullName).Replace('\','/')
  if ($relative -notin @('release.json','release.sig')) { $files[$relative]=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
if (!$files.Contains('Kot.exe') -or !$files.Contains('core/sing-box.exe')) { throw 'Incomplete payload.' }
$data=[Text.Encoding]::UTF8.GetBytes((@{version=$Version;files=$files} | ConvertTo-Json -Depth 5 -Compress))
[IO.File]::WriteAllBytes((Join-Path $Folder 'release.json'),$data)
[IO.File]::WriteAllBytes((Join-Path $Folder 'release.sig'),$rsa.SignData($data,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))
$rsa.Dispose();$public.Dispose()
