#!/usr/bin/env bash
# Shared by install.sh and uninstall.sh. No changes to the container here.
set -euo pipefail
if ! command -v docker >/dev/null; then
    echo 'Docker is not available. Run this on your Jellyfin Docker host.' >&2
    exit 1
fi
container="${1:-}"
if [[ -z "$container" ]]; then
    mapfile -t candidates < <(docker ps --format '{{.Names}} {{.Image}}' | awk 'tolower($2) ~ /jellyfin/ {print $1}')
    if (( ${#candidates[@]} != 1 )); then
        echo 'Specify your Jellyfin container name after the script name.' >&2
        docker ps --format 'table {{.Names}}\t{{.Image}}'
        exit 1
    fi
    container="${candidates[0]}"
fi
if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then
    echo "Container $container must be running." >&2
    exit 1
fi
target='/config/data/plugins/JellyFix_0.3.0.0'
