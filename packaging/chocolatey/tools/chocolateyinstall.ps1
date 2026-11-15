$ErrorActionPreference = 'Stop'

$packageName  = 'kite'
$version      = $env:ChocolateyPackageVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.0.0' }
$cleanVersion = $version.TrimStart('v', 'V')

$toolsLocation = Get-ToolsLocation
$installDir    = Join-Path $toolsLocation 'kite'

$urlX64        = "https://github.com/kite-construction-team/kite/releases/download/v$cleanVersion/kite-v$cleanVersion-win-x64.zip"
$checksumX64   = '0000000000000000000000000000000000000000000000000000000000000000'
$urlArm64      = "https://github.com/kite-construction-team/kite/releases/download/v$cleanVersion/kite-v$cleanVersion-win-arm64.zip"
$checksumArm64 = '0000000000000000000000000000000000000000000000000000000000000000'

$isArm64 = ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') -or ($env:PROCESSOR_ARCHITEW6432 -eq 'ARM64')
$downloadUrl  = if ($isArm64) { $urlArm64 } else { $urlX64 }
$checksum     = if ($isArm64) { $checksumArm64 } else { $checksumX64 }

$packageArgs = @{
    PackageName    = $packageName
    UnzipLocation  = $installDir
    Url64bit       = $downloadUrl
    Checksum64     = $checksum
    ChecksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

$kiteExe       = Join-Path $installDir 'kite.exe'
$kiteServerExe = Join-Path $installDir 'Kite.Server.exe'
$kiteWorkerExe = Join-Path $installDir 'Kite.Worker.exe'

if (Test-Path $kiteExe) {
    Install-BinFile -Name 'kite' -Path $kiteExe
}
if (Test-Path $kiteServerExe) {
    Install-BinFile -Name 'Kite.Server' -Path $kiteServerExe
    Install-BinFile -Name 'kite-server' -Path $kiteServerExe
}
if (Test-Path $kiteWorkerExe) {
    # Prevent Chocolatey from creating an unwanted PATH shim for the internal worker host
    New-Item -ItemType File -Force -Path "$kiteWorkerExe.ignore" | Out-Null
}

Install-ChocolateyPath -PathToInstall $installDir -PathType 'Machine'

Write-Host "Kite $cleanVersion installed to $installDir"
Write-Host "Run 'kite paths' to inspect resolved KITE_HOME and server paths, or 'kite init && kite run' to start."
