param(
    [switch]$NoPause,
    [switch]$DeployToVortex,
    [string]$VortexModsPath = 'G:\Vortex Mods\stardewvalley',
    [string]$GameModsPath = 'G:\SteamLibrary\steamapps\common\Stardew Valley\Mods'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir 'ForegroundControllerInput.csproj'

function Exit-Build {
    param([int]$ExitCode)


    exit $ExitCode
}

Write-Host 'Foreground Controller Input - Windows build'
Write-Host '--------------------------------'

if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Error "Project file not found: $projectFile"
    Exit-Build 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'ERROR: .NET SDK was not found in PATH.' -ForegroundColor Red
    Write-Host 'Install the .NET 6 SDK (or build the project in Visual Studio/Rider), then run this script again.'
    Exit-Build 1
}

Push-Location $projectDir
try {
    Write-Host ''
    Write-Host "Using: $(dotnet --version)"
    Write-Host 'Building Release configuration...'
    Write-Host ''

    & dotnet build $projectFile -c Release
    $buildExitCode = $LASTEXITCODE

    if ($buildExitCode -ne 0) {
        Write-Host ''
        Write-Host 'BUILD FAILED.' -ForegroundColor Red
        Write-Host "dotnet exited with code $buildExitCode."
        Write-Host "If Stardew Valley wasn't detected, configure GamePath for Stardew.ModBuildConfig."
        Exit-Build $buildExitCode
    }

    Write-Host ''
    Write-Host 'BUILD SUCCEEDED.' -ForegroundColor Green
    Write-Host "Check '$projectDir\bin' for the compiled output/release package."

    if ($DeployToVortex) {
        $outputDir = Join-Path $projectDir 'bin\Release\net6.0'

        if (-not (Test-Path -LiteralPath $outputDir)) {
            Write-Error "Build output directory not found: $outputDir"
            Exit-Build 1
        }

        if (-not (Test-Path -LiteralPath $VortexModsPath)) {
            Write-Error "Vortex staging folder not found: $VortexModsPath"
            Exit-Build 1
        }

        $modVersion = (Get-Content -LiteralPath (Join-Path $projectDir 'manifest.json') -Raw | ConvertFrom-Json).Version

        # Vortex stages each mod in its own versioned folder; reuse the existing one so the deployment link stays valid.
        $stagingRoot = Get-ChildItem -LiteralPath $VortexModsPath -Directory -Filter 'ForegroundControllerInput*' |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1 -ExpandProperty FullName

        if (-not $stagingRoot) {
            $stagingRoot = Join-Path $VortexModsPath "ForegroundControllerInput $modVersion"
        }

        $targets = @((Join-Path $stagingRoot 'ForegroundControllerInput'))

        $gameModDir = Join-Path $GameModsPath 'ForegroundControllerInput'
        if (Test-Path -LiteralPath $gameModDir) {
            $targets += $gameModDir
        }

        foreach ($target in $targets) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $outputDir 'ForegroundControllerInput.dll') -Destination $target -Force
            Copy-Item -LiteralPath (Join-Path $projectDir 'manifest.json') -Destination $target -Force
            Write-Host "DEPLOYED: $target" -ForegroundColor Green
        }

        if ($targets.Count -eq 1) {
            Write-Host "NOTE: game mod folder not found at $gameModDir - run a Vortex deploy to apply." -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host ''
    Write-Host 'BUILD FAILED.' -ForegroundColor Red
    Write-Host $_.Exception.Message
    Exit-Build 1
}
finally {
    Pop-Location
}

Exit-Build 0
