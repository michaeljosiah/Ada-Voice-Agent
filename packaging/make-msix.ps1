# Builds an MSIX package for Ada.
# Requires the Windows 10/11 SDK on PATH (makeappx.exe, signtool.exe).
# For local installs, sign with a self-signed cert and trust it; for distribution, use your own cert.
#
#   pwsh packaging/make-msix.ps1 -Version 0.1.0.0
#
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0.0",
    [string]$CertPath = "",      # optional .pfx for signing
    [string]$CertPassword = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root "artifacts/msix-layout"
$msix = Join-Path $root "artifacts/Ada-$Version.msix"

Write-Host "1/4  Publishing Ada.App ($Configuration, win-x64)…"
dotnet publish (Join-Path $root "src/Ada.App/Ada.App.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $layout

Write-Host "2/4  Staging the manifest + assets…"
Copy-Item (Join-Path $PSScriptRoot "AppxManifest.xml") (Join-Path $layout "AppxManifest.xml") -Force
$assets = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null
# NOTE: provide real PNG tile assets here (Square44x44Logo.png, Square150x150Logo.png, StoreLogo.png).
if (-not (Test-Path (Join-Path $assets "StoreLogo.png"))) {
    Write-Warning "Tile assets missing under $assets — add PNGs before MakeAppx, or packing will fail."
}

Write-Host "3/4  Packing $msix…"
New-Item -ItemType Directory -Force -Path (Split-Path $msix) | Out-Null
makeappx pack /d $layout /p $msix /o

if ($CertPath) {
    Write-Host "4/4  Signing…"
    signtool sign /fd SHA256 /f $CertPath /p $CertPassword $msix
} else {
    Write-Host "4/4  Skipped signing (no -CertPath). Sign before installing:"
    Write-Host "      signtool sign /fd SHA256 /a /f <your.pfx> /p <pwd> `"$msix`""
}

Write-Host "Done -> $msix"
