param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$coreDir = Join-Path $RepoRoot "core"

Push-Location $coreDir
try {
    cargo build -p tonewise-ffi -p tonewise-cli --release
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build -p tonewise-ffi -p tonewise-cli --release failed with exit code $LASTEXITCODE"
    }
    $dllPath = Join-Path $coreDir "target\release\tonewise_core.dll"
    if (-not (Test-Path $dllPath)) {
        throw "Expected DLL not found: $dllPath"
    }
    $cliPath = Join-Path $coreDir "target\release\tonewise-cli.exe"
    if (-not (Test-Path $cliPath)) {
        throw "Expected CLI executable not found: $cliPath"
    }
    $item = Get-Item $dllPath
    $cliItem = Get-Item $cliPath
    Write-Output "Built desktop library: $($item.FullName) ($($item.Length) bytes)"
    Write-Output "Built desktop CLI: $($cliItem.FullName) ($($cliItem.Length) bytes)"
} finally {
    Pop-Location
}
