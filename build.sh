#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
dotnet run --project tests -c Release
dotnet build src -c Release
cp src/bin/Release/net10.0/Jellyfin.Plugin.JellyFix.dll plugin/
echo 'Built and tested. The installable DLL is in plugin/.'
