<#
.SYNOPSIS
    Build and deploy track-switches to Railroader's Mods folder.

.PARAMETER NoBuild
    Skip dotnet build; just copy the existing artifacts.

.PARAMETER Configuration
    Build configuration (Debug or Release). Defaults to Debug.

.PARAMETER Watch
    Watch src/, info.json, and the csproj for changes; rebuild and
    redeploy on every save. Restart Railroader (or use UMM's reload)
    to pick up the new DLL.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -NoBuild
    .\deploy.ps1 -Watch
    $env:GAME_DIR = 'C:\Games\Railroader'; .\deploy.ps1
#>

param(
    [switch]$NoBuild,
    [switch]$Watch,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$ModId      = 'ca.jwsm.railroader.experiments.track-switches'
$ProjectDir = $PSScriptRoot
$BuildOut   = Join-Path $ProjectDir "bin\$Configuration"

# Match Directory.Build.props: $env:GAME_DIR overrides the default.
$GameDir = if ($env:GAME_DIR) { $env:GAME_DIR } else { 'D:\SteamLibrary\steamapps\common\Railroader' }

if (-not (Test-Path $GameDir)) {
    throw "Game directory not found: $GameDir. Set `$env:GAME_DIR or edit the script default."
}

$TargetDir = Join-Path (Join-Path $GameDir 'Mods') $ModId

function Invoke-DeployOnce {
    if (-not $NoBuild) {
        Write-Host "Building $Configuration..." -ForegroundColor Cyan
        Push-Location $ProjectDir
        try {
            dotnet build --nologo --verbosity:minimal --configuration $Configuration
            if ($LASTEXITCODE -ne 0) {
                if ($Watch) {
                    Write-Host "  build failed - leaving previous deploy in place." -ForegroundColor Red
                    return
                } else {
                    throw "dotnet build failed (exit $LASTEXITCODE)"
                }
            }
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

    $Pdb = Join-Path $BuildOut "$ModId.pdb"
    if (Test-Path $Pdb) { Copy-Item $Pdb -Destination $TargetDir }

    $Readme = Join-Path $ProjectDir 'README.md'
    if (Test-Path $Readme) { Copy-Item $Readme -Destination $TargetDir }

    # data/ holds the topology.json the mod reads at runtime. Mirror the
    # whole folder so any new files (future extra topologies, etc) come along.
    $DataDir = Join-Path $ProjectDir 'data'
    if (Test-Path $DataDir) {
        Copy-Item $DataDir -Destination $TargetDir -Recurse -Force
    }

    Write-Host "Deployed:" -ForegroundColor Green
    Get-ChildItem $TargetDir | ForEach-Object { Write-Host "  $($_.Name)" }
}

Invoke-DeployOnce

if (-not $Watch) {
    if (-not $NoBuild) {
        Write-Host "Restart Railroader to pick up changes." -ForegroundColor Yellow
    }
    return
}

Write-Host ""
Write-Host "Watching src\, info.json, and *.csproj for changes (Ctrl+C to stop)..." -ForegroundColor Cyan

$srcDir = Join-Path $ProjectDir 'src'

# One watcher for src\**\*.cs, plus we'll check info.json + csproj at the project root.
$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $srcDir
$watcher.Filter = '*.cs'
$watcher.IncludeSubdirectories = $true
$watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::FileName

$rootWatcher = New-Object System.IO.FileSystemWatcher
$rootWatcher.Path = $ProjectDir
$rootWatcher.Filter = '*.*'
$rootWatcher.IncludeSubdirectories = $false
$rootWatcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::FileName

# Coalesce bursty save events: editors often trigger several writes in <100ms.
# We just remember the latest write time and let the main loop pick it up.
$script:lastChange = [DateTime]::MinValue
$script:processedAt = [DateTime]::MinValue

$onChange = {
    $name = $Event.SourceEventArgs.Name
    if ($name -match '\.cs$' -or $name -match '^info\.json$' -or $name -match '\.csproj$') {
        $script:lastChange = [DateTime]::Now
    }
}

$subA = Register-ObjectEvent $watcher 'Changed' -Action $onChange
$subB = Register-ObjectEvent $watcher 'Created' -Action $onChange
$subC = Register-ObjectEvent $watcher 'Renamed' -Action $onChange
$subD = Register-ObjectEvent $rootWatcher 'Changed' -Action $onChange
$subE = Register-ObjectEvent $rootWatcher 'Created' -Action $onChange
$subF = Register-ObjectEvent $rootWatcher 'Renamed' -Action $onChange

$watcher.EnableRaisingEvents = $true
$rootWatcher.EnableRaisingEvents = $true

try {
    while ($true) {
        Start-Sleep -Milliseconds 250
        if ($script:lastChange -gt $script:processedAt) {
            # Wait until writes have settled for ~400ms before rebuilding.
            $idle = ([DateTime]::Now - $script:lastChange).TotalMilliseconds
            if ($idle -lt 400) { continue }
            $script:processedAt = $script:lastChange
            Write-Host ""
            Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] change detected - rebuilding..." -ForegroundColor Magenta
            try {
                Invoke-DeployOnce
            } catch {
                Write-Host "  deploy error: $_" -ForegroundColor Red
            }
        }
    }
} finally {
    $watcher.EnableRaisingEvents = $false
    $rootWatcher.EnableRaisingEvents = $false
    Unregister-Event -SubscriptionId $subA.Id -ErrorAction SilentlyContinue
    Unregister-Event -SubscriptionId $subB.Id -ErrorAction SilentlyContinue
    Unregister-Event -SubscriptionId $subC.Id -ErrorAction SilentlyContinue
    Unregister-Event -SubscriptionId $subD.Id -ErrorAction SilentlyContinue
    Unregister-Event -SubscriptionId $subE.Id -ErrorAction SilentlyContinue
    Unregister-Event -SubscriptionId $subF.Id -ErrorAction SilentlyContinue
    $watcher.Dispose()
    $rootWatcher.Dispose()
}
