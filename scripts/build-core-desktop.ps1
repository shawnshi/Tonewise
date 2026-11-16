param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$coreDir = Join-Path $RepoRoot "core"

Push-Location $coreDir
try {
    cargo build -p tonewise-ffi --release
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build -p tonewise-ffi --release failed with exit code $LASTEXITCODE"
    }
    $dllPath = Join-Path $coreDir "target\release\tonewise_core.dll"
    if (-not (Test-Path $dllPath)) {
        throw "Expected DLL not found: $dllPath"
    }
    $item = Get-Item $dllPath
    Write-Output "Built desktop library: $($item.FullName) ($($item.Length) bytes)"
} finally {
    Pop-Location
}
