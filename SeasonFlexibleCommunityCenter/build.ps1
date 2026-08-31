Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modRoot = $PSScriptRoot
$projectDir = Join-Path $modRoot 'src\SeasonFlexibleCommunityCenter'
$projectFile = Join-Path $projectDir 'SeasonFlexibleCommunityCenter.csproj'
$targetFramework = 'net6.0'

Write-Host 'Season-Flexible Community Center - Windows build'
Write-Host '-----------------------------------------------'

if (-not (Test-Path -LiteralPath $projectFile)) {
    Write-Error "Project file not found: $projectFile"
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'ERROR: .NET SDK was not found in PATH.' -ForegroundColor Red
    Write-Host 'Install the required .NET SDK (or build the project in Visual Studio/Rider), then run this script again.'
    exit 1
}

Push-Location $projectDir
try {
    Write-Host ''
    Write-Host "Using: $(dotnet --version)"
    Write-Host 'Restoring dependencies...'
    Write-Host ''
    & dotnet restore $projectFile
    $restoreExitCode = $LASTEXITCODE

    if ($restoreExitCode -ne 0) {
        Write-Host ''
        Write-Host 'BUILD FAILED.' -ForegroundColor Red
        Write-Host "dotnet restore exited with code $restoreExitCode."
        exit $restoreExitCode
    }

    Write-Host ''
    Write-Host 'Building Release configuration...'
    Write-Host ''
    & dotnet build $projectFile -c Release --no-restore
    $buildExitCode = $LASTEXITCODE

    if ($buildExitCode -ne 0) {
        Write-Host ''
        Write-Host 'BUILD FAILED.' -ForegroundColor Red
        Write-Host "dotnet build exited with code $buildExitCode."
        Write-Host "If Stardew Valley wasn't detected, configure GamePath in Directory.Build.props."
        exit $buildExitCode
    }

    Write-Host ''
    Write-Host 'BUILD SUCCEEDED.' -ForegroundColor Green
    Write-Host "Check '$projectDir\bin\Release\$targetFramework' for the compiled output/release package."
}
catch {
    Write-Host ''
    Write-Host 'BUILD FAILED.' -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}
finally {
    Pop-Location
}

exit 0
