<#
.SYNOPSIS
    Build and deploy consist-dynamics to Railroader's Mods folder.

.PARAMETER NoBuild
    Skip dotnet build; just copy the existing artifacts.

.PARAMETER Configuration
    Build configuration (Debug or Release). Defaults to Debug.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -NoBuild
    $env:GAME_DIR = 'C:\Games\Railroader'; .\deploy.ps1
#>

param(
    [switch]$NoBuild,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$ModId      = 'ca.jwsm.railroader.experiments.consist-dynamics'
$ProjectDir = $PSScriptRoot
$BuildOut   = Join-Path $ProjectDir "bin\$Configuration"

# Match Directory.Build.props: $env:GAME_DIR overrides the default.
$GameDir = if ($env:GAME_DIR) { $env:GAME_DIR } else { 'D:\SteamLibrary\steamapps\common\Railroader' }

if (-not (Test-Path $GameDir)) {
    throw "Game directory not found: $GameDir. Set `$env:GAME_DIR or edit the script default."
}

$TargetDir = Join-Path (Join-Path $GameDir 'Mods') $ModId

if (-not $NoBuild) {
    Write-Host "Building $Configuration..." -ForegroundColor Cyan
    Push-Location $ProjectDir
    try {
        dotnet build --nologo --verbosity:minimal --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
}

$Dll      = Join-Path $BuildOut "$ModId.dll"
$InfoJson = Join-Path $ProjectDir 'info.json'

if (-not (Test-Path $Dll))      { throw "DLL not found: $Dll" }
if (-not (Test-Path $InfoJson)) { throw "info.json not found: $InfoJson" }

Write-Host "Deploying to $TargetDir..." -ForegroundColor Cyan

if (Test-Path $TargetDir) {
    Remove-Item $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

Copy-Item $Dll      -Destination $TargetDir
Copy-Item $InfoJson -Destination $TargetDir

# Also copy a .pdb if it was built — handy for stack-trace line numbers in the UMM log.
$Pdb = Join-Path $BuildOut "$ModId.pdb"
if (Test-Path $Pdb) { Copy-Item $Pdb -Destination $TargetDir }

# README explains what's wired / not wired for anyone testing the mod.
$Readme = Join-Path $ProjectDir 'README.md'
if (Test-Path $Readme) { Copy-Item $Readme -Destination $TargetDir }

Write-Host "Deployed:" -ForegroundColor Green
Get-ChildItem $TargetDir | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "Restart Railroader to pick up changes." -ForegroundColor Yellow
