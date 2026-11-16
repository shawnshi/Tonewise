param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$NdkPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($NdkPath)) {
    $sdkRoot = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
    $NdkPath = Join-Path $sdkRoot "ndk\28.2.13676358"
}
if (-not (Test-Path $NdkPath)) {
    throw "Android NDK not found at: $NdkPath"
}

$toolchainBin = Join-Path $NdkPath "toolchains\llvm\prebuilt\windows-x86_64\bin"
if (-not (Test-Path $toolchainBin)) {
    throw "NDK LLVM toolchain bin not found at: $toolchainBin"
}

$targets = @(
    @{ Triple = "aarch64-linux-android";   Abi = "arm64-v8a";   Linker = "aarch64-linux-android24-clang.cmd";     EnvKey = "CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER" },
    @{ Triple = "armv7-linux-androideabi"; Abi = "armeabi-v7a"; Linker = "armv7a-linux-androideabi24-clang.cmd";  EnvKey = "CARGO_TARGET_ARMV7_LINUX_ANDROIDEABI_LINKER" },
    @{ Triple = "x86_64-linux-android";    Abi = "x86_64";      Linker = "x86_64-linux-android24-clang.cmd";      EnvKey = "CARGO_TARGET_X86_64_LINUX_ANDROID_LINKER" },
    @{ Triple = "i686-linux-android";      Abi = "x86";         Linker = "i686-linux-android24-clang.cmd";        EnvKey = "CARGO_TARGET_I686_LINUX_ANDROID_LINKER" }
)

$coreDir = Join-Path $RepoRoot "core"
$jniLibsRoot = Join-Path $RepoRoot "android\musikr\src\main\jniLibs"
$llvmAr = Join-Path $toolchainBin "llvm-ar.exe"

Push-Location $coreDir
try {
    $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_AR = $llvmAr
    $env:CARGO_TARGET_ARMV7_LINUX_ANDROIDEABI_AR = $llvmAr
    $env:CARGO_TARGET_X86_64_LINUX_ANDROID_AR = $llvmAr
    $env:CARGO_TARGET_I686_LINUX_ANDROID_AR = $llvmAr
    $env:CC_aarch64_linux_android = Join-Path $toolchainBin "aarch64-linux-android24-clang.cmd"
    $env:CC_armv7_linux_androideabi = Join-Path $toolchainBin "armv7a-linux-androideabi24-clang.cmd"
    $env:CC_x86_64_linux_android = Join-Path $toolchainBin "x86_64-linux-android24-clang.cmd"
    $env:CC_i686_linux_android = Join-Path $toolchainBin "i686-linux-android24-clang.cmd"
    $env:AR_aarch64_linux_android = $llvmAr
    $env:AR_armv7_linux_androideabi = $llvmAr
    $env:AR_x86_64_linux_android = $llvmAr
    $env:AR_i686_linux_android = $llvmAr
    $env:RUSTFLAGS = "-C link-arg=-Wl,-z,max-page-size=16384"

    foreach ($t in $targets) {
        $linkerPath = Join-Path $toolchainBin $t.Linker
        if (-not (Test-Path $linkerPath)) {
            throw "NDK linker not found: $linkerPath"
        }
        Set-Item -Path ("Env:" + $t.EnvKey) -Value $linkerPath
        Write-Output "Building tonewise-ffi for $($t.Triple) ($($t.Abi))..."
        cargo build -p tonewise-ffi --release --target $t.Triple
        if ($LASTEXITCODE -ne 0) {
            throw "cargo build failed for $($t.Triple)"
        }

        $soSrc = Join-Path $coreDir ("target\" + $t.Triple + "\release\libtonewise_core.so")
        if (-not (Test-Path $soSrc)) {
            throw "Compiled shared library missing: $soSrc"
        }
        $abiDir = Join-Path $jniLibsRoot $t.Abi
        if (-not (Test-Path $abiDir)) {
            New-Item -ItemType Directory -Path $abiDir -Force | Out-Null
        }
        $soDst = Join-Path $abiDir "libtonewise_core.so"
        Copy-Item -Path $soSrc -Destination $soDst -Force
        Write-Output "  -> Copied to $soDst ($((Get-Item $soDst).Length) bytes)"
    }
} finally {
    Remove-Item Env:RUSTFLAGS -ErrorAction SilentlyContinue
    Pop-Location
}
