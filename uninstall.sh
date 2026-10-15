#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/container.sh" "$@"
if ! docker exec "$container" test -d "$target"; then
    echo 'The JellyFix folder is not present. Nothing changed.'
    exit 0
fi
backup="/config/data/plugin-backups/JellyFix_0.3.0.0_$(date -u +%Y%m%dT%H%M%SZ)"
docker exec -u 0 "$container" mkdir -p /config/data/plugin-backups
docker exec -u 0 "$container" mv "$target" "$backup"
docker restart "$container"
echo "Plugin removed from loading. Files retained at $backup inside the container."
