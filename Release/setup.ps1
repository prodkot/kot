param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Key)
$ErrorActionPreference='Stop'
$base=Get-Item $Setup; $zip=Get-Item $Archive
$manifest=[ordered]@{version=$Version;installerLength=$base.Length;archiveLength=$zip.Length;installerHash=(Get-FileHash $Setup -Algorithm SHA256).Hash.ToLowerInvariant();archiveHash=(Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant()}
$json=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json -Compress))
$rsa=[Security.Cryptography.RSA]::Create()
try {$rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path $Key)));$signature=$rsa.SignData($json,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)}finally{$rsa.Dispose()}
$file=[IO.File]::Open((Resolve-Path $Setup),[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
  $input=[IO.File]::OpenRead((Resolve-Path $Archive));try{$input.CopyTo($file)}finally{$input.Dispose()}
  $file.Write($json);$file.Write($signature);$file.Write([BitConverter]::GetBytes([int]$json.Length));$file.Write([BitConverter]::GetBytes([int]$signature.Length));$file.Write([Text.Encoding]::ASCII.GetBytes('KOT.SETUP.V1.END'))
}finally{$file.Dispose()}
