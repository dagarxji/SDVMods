param(
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir 'FishingForecast.csproj'

function Finish-Build {
    param([int]$ExitCode)


    exit $ExitCode
}

Write-Host 'Fishing Forecast - Windows build'
Write-Host '--------------------------------'

if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Error "Project file not found: $projectFile"
    Finish-Build 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'ERROR: .NET SDK was not found in PATH.' -ForegroundColor Red
    Write-Host 'Install the .NET 6 SDK (or build the project in Visual Studio/Rider), then run this script again.'
    Finish-Build 1
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
        Finish-Build $buildExitCode
    }

    Write-Host ''
    Write-Host 'BUILD SUCCEEDED.' -ForegroundColor Green
    Write-Host "Check '$projectDir\bin' for the compiled output/release package."
}
catch {
    Write-Host ''
    Write-Host 'BUILD FAILED.' -ForegroundColor Red
    Write-Host $_.Exception.Message
    Finish-Build 1
}
finally {
    Pop-Location
}

Finish-Build 0
