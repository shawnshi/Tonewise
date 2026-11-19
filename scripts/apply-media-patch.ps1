param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$mediaDir = Join-Path $RepoRoot "android\media"
$patchNames = @(
    "media-windows-ffmpeg.patch",
    "media-float-dsp-processors.patch"
)

if (-not (Test-Path (Join-Path $mediaDir ".git"))) {
    throw "Submodule android/media is not initialized. Run: git submodule update --init --recursive"
}

foreach ($patchName in $patchNames) {
    $patchFile = Join-Path $RepoRoot ("android\patches\" + $patchName)
    if (-not (Test-Path $patchFile)) {
        throw "Patch file not found: $patchFile"
    }

    git -C $mediaDir apply --reverse --check $patchFile 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Output "$patchName is already applied."
        continue
    }

    git -C $mediaDir apply --check $patchFile
    if ($LASTEXITCODE -ne 0) {
        throw "Patch check failed on android/media for $patchName. Check for conflicting local changes."
    }

    git -C $mediaDir apply $patchFile
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply $patchFile"
    }
    Write-Output "Applied $patchName successfully."
}
