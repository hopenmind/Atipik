<#
  .SYNOPSIS
    Builds portable, self-contained A'Tipik packages for Windows.

  .DESCRIPTION
    1. (Optional) Builds the Rust local-LLM bridge if cargo is on PATH (x64 only).
    2. Publishes the .NET app as a single-file self-contained exe for each arch.
    3. Stages the exe (+ DLL + assets) and zips it.

  .PARAMETER Arch
    x64 | arm64 | both  (default: x64)

  .EXAMPLE
    .\scripts\build-release.ps1
    .\scripts\build-release.ps1 -Arch both
#>
[CmdletBinding()]
param(
    [ValidateSet("x64","arm64","both")][string]$Arch = "x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$archs = if ($Arch -eq "both") { @("x64","arm64") } else { @($Arch) }

$cargo = Get-Command cargo -ErrorAction SilentlyContinue
$dllRel = "rust\atypik-llm\target\release\atypik_llm.dll"
$haveDll = $false
if ($cargo -and ($archs -contains "x64")) {
    Write-Host "==> Building the local LLM bridge via build-llm-bridge.ps1" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "build-llm-bridge.ps1")
    $haveDll = (Test-Path $dllRel)
    if ($haveDll) { Write-Host "    bridge OK: $dllRel" -ForegroundColor Green }
} else {
    Write-Host "==> Skipping the optional LLM bridge (app works without it)" -ForegroundColor Yellow
}

foreach ($a in $archs) {
    $rid = "win-$a"
    $outDir = "publish-$a"
    Write-Host ""
    Write-Host "==> Publishing $rid (single-file, self-contained)" -ForegroundColor Cyan
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    dotnet publish Atypik.csproj -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }

    $stage = "stage\Atypik"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "$outDir\Atypik.exe" -Destination $stage -Force
    Copy-Item "assets" -Destination $stage -Recurse -ErrorAction SilentlyContinue
    if ($haveDll -and $a -eq "x64") { Copy-Item $dllRel -Destination $stage -Force }

    $zip = "Atypik-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
    Write-Host "    -> $zip" -ForegroundColor Green
}

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
