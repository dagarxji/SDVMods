@echo off
setlocal
cd /d "%~dp0src\SeasonFlexibleCommunityCenter"
dotnet restore || exit /b 1
dotnet build -c Release || exit /b 1
