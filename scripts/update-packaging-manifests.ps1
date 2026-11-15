#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Updates Winget, Chocolatey, and Homebrew packaging manifests with version, release date, and SHA-256 hashes from SHA256SUMS.txt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$DistDir = "dist",
    [string]$ReleaseDate = (Get-Date -Format "yyyy-MM-dd")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$cleanVersion = $Version.TrimStart("v", "V")
$repoRoot = Split-Path -Parent $PSScriptRoot
$distPath = if ([System.IO.Path]::IsPathRooted($DistDir)) { $DistDir } else { Join-Path $repoRoot $DistDir }
$sumsFile = Join-Path $distPath "SHA256SUMS.txt"

if (-not (Test-Path $sumsFile)) {
    throw "SHA256SUMS.txt not found at $sumsFile"
}

$hashes = @{}
foreach ($line in Get-Content -Path $sumsFile) {
    if ($line -match '^\s*([0-9a-fA-F]{64})\s+(\S+)\s*$') {
        $hashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
    }
}

function Require-Hash([string]$fileName) {
    if (-not $hashes.ContainsKey($fileName)) {
        throw "Checksum entry '$fileName' not found in $sumsFile"
    }
    return $hashes[$fileName]
}

$winX64Portable = (Require-Hash "kite-v$cleanVersion-win-x64-portable.zip").ToUpperInvariant()
$winArm64Portable = (Require-Hash "kite-v$cleanVersion-win-arm64-portable.zip").ToUpperInvariant()
$winX64Zip = Require-Hash "kite-v$cleanVersion-win-x64.zip"
$winArm64Zip = Require-Hash "kite-v$cleanVersion-win-arm64.zip"
$osxArm64 = Require-Hash "kite-v$cleanVersion-osx-arm64.tar.gz"
$osxX64 = Require-Hash "kite-v$cleanVersion-osx-x64.tar.gz"

# 1. Winget
$wingetRoot = Join-Path $repoRoot "packaging/winget/manifests/k/KiteConstructionTeam/Kite"
$targetWingetDir = Join-Path $wingetRoot $cleanVersion
if (-not (Test-Path $targetWingetDir)) {
    $templateDir = Get-ChildItem -Path $wingetRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($null -eq $templateDir) {
        throw "No template Winget manifest folder found under $wingetRoot"
    }
    Copy-Item -Path $templateDir.FullName -Destination $targetWingetDir -Recurse -Force
}

$wingetVersionFile = Join-Path $targetWingetDir "KiteConstructionTeam.Kite.yaml"
$wingetLocaleFile = Join-Path $targetWingetDir "KiteConstructionTeam.Kite.locale.en-US.yaml"
$wingetInstallerFile = Join-Path $targetWingetDir "KiteConstructionTeam.Kite.installer.yaml"

(Get-Content $wingetVersionFile -Raw) -replace 'PackageVersion:\s*\S+', "PackageVersion: $cleanVersion" | Set-Content $wingetVersionFile -NoNewline

$localeText = Get-Content $wingetLocaleFile -Raw
$localeText = $localeText -replace 'PackageVersion:\s*\S+', "PackageVersion: $cleanVersion"
$localeText = $localeText -replace 'https://github\.com/kite-construction-team/kite/releases/tag/v\S+', "https://github.com/kite-construction-team/kite/releases/tag/v$cleanVersion"
Set-Content $wingetLocaleFile $localeText -NoNewline

$installerText = @"
# Created following the Winget 1.9.0 manifest schema
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json

PackageIdentifier: KiteConstructionTeam.Kite
PackageVersion: $cleanVersion
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: kite.exe
    PortableCommandAlias: kite
  - RelativeFilePath: Kite.Server.exe
    PortableCommandAlias: kite-server
UpgradeBehavior: install
Commands:
  - kite
  - kite-server
ReleaseDate: $ReleaseDate
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/kite-construction-team/kite/releases/download/v$cleanVersion/kite-v$cleanVersion-win-x64-portable.zip
    InstallerSha256: $winX64Portable
  - Architecture: arm64
    InstallerUrl: https://github.com/kite-construction-team/kite/releases/download/v$cleanVersion/kite-v$cleanVersion-win-arm64-portable.zip
    InstallerSha256: $winArm64Portable
ManifestType: installer
ManifestVersion: 1.9.0
"@
[System.IO.File]::WriteAllText($wingetInstallerFile, ($installerText -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

# 2. Chocolatey
$nuspecPath = Join-Path $repoRoot "packaging/chocolatey/kite.nuspec"
$chocoInstallPath = Join-Path $repoRoot "packaging/chocolatey/tools/chocolateyinstall.ps1"

(Get-Content $nuspecPath -Raw) -replace '<version>[^<]+</version>', "<version>$cleanVersion</version>" | Set-Content $nuspecPath -NoNewline

$chocoText = Get-Content $chocoInstallPath -Raw
$chocoText = $chocoText -replace "\`$checksumX64\s*=\s*'[^']*'", "`$checksumX64   = '$winX64Zip'"
$chocoText = $chocoText -replace "\`$checksumArm64\s*=\s*'[^']*'", "`$checksumArm64 = '$winArm64Zip'"
Set-Content $chocoInstallPath $chocoText -NoNewline

# 3. Homebrew Formula
$formulaPath = Join-Path $repoRoot "packaging/homebrew/Formula/kite.rb"
$formulaText = Get-Content $formulaPath -Raw
$formulaText = $formulaText -replace 'version "[^"]+"', "version `"$cleanVersion`""
$formulaText = $formulaText -replace 'releases/download/v[^/]+/kite-v[^-]+-osx-arm64\.tar\.gz', "releases/download/v$cleanVersion/kite-v$cleanVersion-osx-arm64.tar.gz"
$formulaText = $formulaText -replace 'releases/download/v[^/]+/kite-v[^-]+-osx-x64\.tar\.gz', "releases/download/v$cleanVersion/kite-v$cleanVersion-osx-x64.tar.gz"
$formulaText = [regex]::Replace($formulaText, '(osx-arm64\.tar\.gz"\s+sha256\s+")[^"]+"', "`${1}$osxArm64`"")
$formulaText = [regex]::Replace($formulaText, '(osx-x64\.tar\.gz"\s+sha256\s+")[^"]+"', "`${1}$osxX64`"")
[System.IO.File]::WriteAllText($formulaPath, ($formulaText -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

Write-Host "Updated Winget, Chocolatey, and Homebrew manifests for version $cleanVersion."
