$ErrorActionPreference='Stop'
$nsis=Join-Path ${env:ProgramFiles(x86)} 'NSIS/makensis.exe'
for ($attempt=1; $attempt -le 3; $attempt++) {
  if (Test-Path -LiteralPath $nsis -PathType Leaf) { $global:LASTEXITCODE=0; return }
  choco install nsis --yes --no-progress
  # Chocolatey can return zero after a feed timeout without installing anything.
  if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $nsis -PathType Leaf)) { return }
  if ($attempt -lt 3) { Start-Sleep -Seconds 5 }
}
throw 'NSIS installation failed after three attempts; makensis.exe is missing.'
