param(
    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,
    [Parameter(Mandatory = $true)]
    [string]$NdkPath
)

$ErrorActionPreference = "Stop"

$taglibSource = Join-Path $WorkingDirectory "taglib"
$taglibBuild = Join-Path $taglibSource "build"
$taglibPackage = Join-Path $taglibSource "pkg"
$sdkDirectory = Split-Path (Split-Path $NdkPath -Parent) -Parent
$toolchain = Join-Path $NdkPath "build/cmake/android.toolchain.cmake"
$ninja = Join-Path $sdkDirectory "cmake/3.22.1/bin/ninja.exe"
$abis = @("x86", "x86_64", "armeabi-v7a", "arm64-v8a")

function Invoke-CMake {
    param([string[]]$Arguments)

    & cmake @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "cmake failed with exit code $LASTEXITCODE"
    }
}

foreach ($abi in $abis) {
    $buildDirectory = Join-Path $taglibBuild $abi
    $packageDirectory = Join-Path $taglibPackage $abi

    Invoke-CMake @(
        "-S", $taglibSource,
        "-B", $buildDirectory,
        "-G", "Ninja",
        "-DCMAKE_MAKE_PROGRAM=$ninja",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DANDROID_ABI=$abi",
        "-DBUILD_SHARED_LIBS=OFF",
        "-DVISIBILITY_HIDDEN=ON",
        "-DBUILD_TESTING=OFF",
        "-DBUILD_EXAMPLES=OFF",
        "-DBUILD_BINDINGS=OFF",
        "-DWITH_ZLIB=OFF",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DWITH_APE=OFF",
        "-DWITH_ASF=OFF",
        "-DWITH_MOD=OFF",
        "-DWITH_SHORTEN=OFF",
        "-DWITH_TRUEAUDIO=OFF",
        "-DCMAKE_CXX_FLAGS=-fPIC"
    )
    Invoke-CMake @("--build", $buildDirectory, "--config", "Release", "--parallel")
    Invoke-CMake @("--install", $buildDirectory, "--config", "Release", "--prefix", $packageDirectory, "--strip")
}
