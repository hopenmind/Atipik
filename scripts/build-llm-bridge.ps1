<#
  .SYNOPSIS
    Builds the optional local-LLM Rust bridge (atypik_llm.dll).

  .DESCRIPTION
    llama-cpp-sys-2 compiles llama.cpp via CMake. On machines where the bundled
    Visual Studio CMake is broken (e.g. VS 18 preview's 4.3/4.2 module mismatch)
    or the "Visual Studio <N> <year>" generator is not recognised, this script
    forces the Ninja generator with a standalone CMake and the MSVC toolchain
    from vcvars64. It is self-contained: it installs pip `cmake` and `ninja` if
    missing, finds vcvars64, and runs cargo in the right environment.

    On a machine with a healthy VS 2022 + CMake, a plain `cargo build --release`
    also works; this script is the reliable path regardless.

  .EXAMPLE
    .\scripts\build-llm-bridge.ps1
#>
[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot "..\rust\atypik-llm\Cargo.toml")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$Manifest = (Resolve-Path $Manifest).Path

# 1. Ensure a standalone CMake + Ninja are available (pip), independent of VS.
$py = (Get-Command python -ErrorAction SilentlyContinue)
if (-not $py) { throw "Python is required to provide CMake/Ninja." }
$pipCmake = Join-Path $env:APPDATA "Python\Python314\Scripts\cmake.exe"
$pipNinja = Join-Path $env:APPDATA "Python\Python314\Scripts\ninja.exe"
if (-not (Test-Path $pipCmake) -or -not (Test-Path $pipNinja)) {
    Write-Host "==> Installing standalone CMake + Ninja via pip" -ForegroundColor Cyan
    & python -m pip install --quiet --upgrade cmake ninja
}

# 2. Find an MSVC environment (vcvars64). Prefer VS 2022, fall back to others.
$candidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
)
$vcvars = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw "vcvars64.bat not found. Install the VS 'Desktop development with C++' workload." }

# 3. Locate a Ninja (pip one, else the one shipped with VS).
$ninjaDir = Split-Path $pipNinja
if (-not (Test-Path $pipNinja)) {
    $vsNinja = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio' -Recurse -Filter 'ninja.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($vsNinja) { $ninjaDir = $vsNinja.DirectoryName }
}

Write-Host "==> vcvars : $vcvars" -ForegroundColor Cyan
Write-Host "==> cmake  : $pipCmake" -ForegroundColor Cyan
Write-Host "==> ninja  : $ninjaDir" -ForegroundColor Cyan
Write-Host "==> Ninja generator + MSVC toolchain" -ForegroundColor Cyan

# 4. Run cargo inside the vcvars environment with Ninja forced.
#    CMAKE_GENERATOR_<target> (not bare CMAKE_GENERATOR) is used because the
#    llama-cpp-sys-2 build script forwards every CMAKE_* env var as a -D define;
#    the target-specific name is read by the cmake crate for -G but ignored by
#    CMake as an unknown cache variable, avoiding a conflict. And it MUST be set
#    with quoted `set` to avoid the classic cmd trailing-space trap.
$inner = '"' + $vcvars + '" >nul' +
    ' && set "CMAKE_GENERATOR_x86_64-pc-windows-msvc=Ninja"' +
    ' && set "CMAKE=' + $pipCmake + '"' +
    ' && set "PATH=' + $ninjaDir + ';%PATH%"' +
    ' && cargo build --release --manifest-path "' + $Manifest + '"'

Write-Host "==> cargo build --release" -ForegroundColor Cyan
cmd /c $inner
if ($LASTEXITCODE -ne 0) { throw "Rust bridge build failed (exit $LASTEXITCODE)." }

$dll = Join-Path (Split-Path $Manifest) 'target\release\atypik_llm.dll'
if (Test-Path $dll) {
    Write-Host "==> Built: $dll" -ForegroundColor Green
} else {
    Write-Host "==> Build reported success but the DLL was not found at $dll" -ForegroundColor Yellow
}
