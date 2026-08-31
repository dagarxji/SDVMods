#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/src/SeasonFlexibleCommunityCenter"
dotnet restore
dotnet build -c Release
