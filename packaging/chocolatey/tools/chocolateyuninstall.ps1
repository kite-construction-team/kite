$ErrorActionPreference = 'Stop'

$toolsLocation = Get-ToolsLocation
$installDir    = Join-Path $toolsLocation 'kite'

$kiteExe       = Join-Path $installDir 'kite.exe'
$kiteServerExe = Join-Path $installDir 'Kite.Server.exe'

if (Get-Command Uninstall-BinFile -ErrorAction SilentlyContinue) {
    Uninstall-BinFile -Name 'kite' -Path $kiteExe
    Uninstall-BinFile -Name 'Kite.Server' -Path $kiteServerExe
    Uninstall-BinFile -Name 'kite-server' -Path $kiteServerExe
}

if (Test-Path $installDir) {
    Remove-Item -Recurse -Force $installDir
}

# Remove installDir from Machine PATH if present
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not [string]::IsNullOrWhiteSpace($machinePath)) {
    $filtered = ($machinePath -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not [string]::Equals($_.TrimEnd('\'), $installDir.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)
    }) -join ';'
    if ($filtered -ne $machinePath) {
        [Environment]::SetEnvironmentVariable('Path', $filtered, 'Machine')
    }
}

Write-Host "Uninstalled Kite binaries from $installDir. User site data in %LOCALAPPDATA%\Kite was preserved."
