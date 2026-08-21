$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    dotnet build -c Release

    $dll = Join-Path $PSScriptRoot "bin\Release\net6.0\BetterBundleOverview.dll"
    if (!(Test-Path $dll)) {
        throw "Build succeeded but BetterBundleOverview.dll wasn't found at $dll"
    }

    $distRoot = Join-Path $PSScriptRoot "dist"
    $modRoot = Join-Path $distRoot "BetterBundleOverview"
    $zip = Join-Path $distRoot "BetterBundleOverview-0.2.0.zip"

    Remove-Item $modRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $modRoot -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "manifest.json") $modRoot
    Copy-Item $dll $modRoot

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $modRoot -DestinationPath $zip

    Write-Host ""
    Write-Host "Built installable mod: $zip"
}
finally {
    Pop-Location
}
