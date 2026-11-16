param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$mediaDir = Join-Path $RepoRoot "android\media"
$patchFile = Join-Path $RepoRoot "android\patches\media-windows-ffmpeg.patch"

if (-not (Test-Path $patchFile)) {
    throw "Patch file not found: $patchFile"
}
if (-not (Test-Path (Join-Path $mediaDir ".git"))) {
    throw "Submodule android/media is not initialized. Run: git submodule update --init --recursive"
}

git -C $mediaDir apply --reverse --check $patchFile 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Output "media-windows-ffmpeg.patch is already applied."
    exit 0
}

git -C $mediaDir apply --check $patchFile
if ($LASTEXITCODE -ne 0) {
    throw "Patch check failed on android/media. Check for conflicting local changes."
}

git -C $mediaDir apply $patchFile
if ($LASTEXITCODE -ne 0) {
    throw "Failed to apply $patchFile"
}
Write-Output "Applied media-windows-ffmpeg.patch successfully."
