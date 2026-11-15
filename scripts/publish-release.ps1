#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes cross-platform self-contained Kite release archives (CLI, Server, Worker) and SHA256SUMS.txt.
.PARAMETER Version
    Semantic version (e.g. 1.0.0 or v1.0.0).
.PARAMETER Rid
    One or more .NET Runtime Identifiers (win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, linux-arm64).
.PARAMETER OutputDir
    Destination directory for published archives and checksums.
.PARAMETER IncludePortableSingleFile
    When set for win-* RIDs, also produces a single-file portable .zip tailored for Winget portable installation.
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string[]]$Rid = @("win-x64"),
    [string]$OutputDir = "dist",
    [switch]$IncludePortableSingleFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$cleanVersion = $Version.TrimStart("v", "V")
$Rid = @($Rid | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$repoRoot = Split-Path -Parent $PSScriptRoot
$outRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }
$stagingRoot = Join-Path $outRoot "_staging"

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$projects = @(
    @{ Name = "Kite.Cli"; Csproj = (Join-Path $repoRoot "src/Kite.Cli/Kite.Cli.csproj") },
    @{ Name = "Kite.Server"; Csproj = (Join-Path $repoRoot "src/Kite.Server/Kite.Server.csproj") },
    @{ Name = "Kite.Worker"; Csproj = (Join-Path $repoRoot "src/Kite.Worker/Kite.Worker.csproj") }
)

function Invoke-DotNetPublish {
    param(
        [string]$ProjectPath,
        [string]$RuntimeId,
        [string]$TargetDir,
        [bool]$SingleFile
    )

    $publishArgs = @(
        "publish", $ProjectPath,
        "-c", "Release",
        "-r", $RuntimeId,
        "--self-contained", "true",
        "-p:Version=$cleanVersion",
        "-p:PublishTrimmed=false",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-o", $TargetDir
    )

    if ($SingleFile) {
        $publishArgs += @(
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true"
        )
    }

    Write-Host ">> dotnet $($publishArgs -join ' ')"
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath ($RuntimeId)"
    }
}

foreach ($runtimeId in $Rid) {
    $isWindowsRid = $runtimeId.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)
    $stageDir = Join-Path $stagingRoot $runtimeId
    if (Test-Path $stageDir) {
        Remove-Item -Recurse -Force $stageDir
    }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    foreach ($proj in $projects) {
        Invoke-DotNetPublish -ProjectPath $proj.Csproj -RuntimeId $runtimeId -TargetDir $stageDir -SingleFile $false
    }

    # Strip *.pdb if any leaked from dependency packages
    Get-ChildItem -Path $stageDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force

    Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $stageDir "LICENSE") -Force
    Copy-Item (Join-Path $repoRoot "README.md") (Join-Path $stageDir "README.md") -Force

    if (-not $isWindowsRid) {
        $kiteServerWrapper = @'
#!/usr/bin/env bash
set -euo pipefail
SOURCE="${BASH_SOURCE[0]}"
while [ -L "$SOURCE" ]; do
  DIR="$(cd -P "$(dirname "$SOURCE")" >/dev/null 2>&1 && pwd)"
  SOURCE="$(readlink "$SOURCE")"
  [[ "$SOURCE" != /* ]] && SOURCE="$DIR/$SOURCE"
done
BIN_DIR="$(cd -P "$(dirname "$SOURCE")" >/dev/null 2>&1 && pwd)"
exec "$BIN_DIR/Kite.Server" "$@"
'@
        $wrapperPath = Join-Path $stageDir "kite-server"
        [System.IO.File]::WriteAllText($wrapperPath, ($kiteServerWrapper -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

        if (-not $IsWindows) {
            & chmod +x (Join-Path $stageDir "kite") (Join-Path $stageDir "Kite.Server") (Join-Path $stageDir "Kite.Worker") $wrapperPath
        }
    }

    if ($isWindowsRid) {
        $archiveName = "kite-v$cleanVersion-$runtimeId.zip"
        $archivePath = Join-Path $outRoot $archiveName
        if (Test-Path $archivePath) { Remove-Item -Force $archivePath }
        Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal
        Write-Host "Created $archivePath"

        if ($IncludePortableSingleFile) {
            $portableStage = Join-Path $stagingRoot "$runtimeId-portable"
            if (Test-Path $portableStage) { Remove-Item -Recurse -Force $portableStage }
            New-Item -ItemType Directory -Force -Path $portableStage | Out-Null

            foreach ($proj in $projects) {
                Invoke-DotNetPublish -ProjectPath $proj.Csproj -RuntimeId $runtimeId -TargetDir $portableStage -SingleFile $true
            }
            Get-ChildItem -Path $portableStage -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force
            Copy-Item (Join-Path $repoRoot "LICENSE") (Join-Path $portableStage "LICENSE") -Force

            $portableZip = Join-Path $outRoot "kite-v$cleanVersion-$runtimeId-portable.zip"
            if (Test-Path $portableZip) { Remove-Item -Force $portableZip }
            Compress-Archive -Path (Join-Path $portableStage "*") -DestinationPath $portableZip -CompressionLevel Optimal
            Write-Host "Created $portableZip"
        }
    }
    else {
        $archiveName = "kite-v$cleanVersion-$runtimeId.tar.gz"
        $archivePath = Join-Path $outRoot $archiveName
        if (Test-Path $archivePath) { Remove-Item -Force $archivePath }
        & tar -czf $archivePath -C $stageDir .
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed for $archivePath"
        }
        Write-Host "Created $archivePath"
    }
}

if (Test-Path $stagingRoot) {
    Remove-Item -Recurse -Force $stagingRoot
}

$checksumLines = @(Get-ChildItem -Path $outRoot -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" -and ($_.Extension -in ".zip", ".gz") } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    })

$sumsPath = Join-Path $outRoot "SHA256SUMS.txt"
$sumsContent = if ($checksumLines.Count -gt 0) { ($checksumLines -join "`n") + "`n" } else { "" }
[System.IO.File]::WriteAllText($sumsPath, $sumsContent, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $sumsPath"
