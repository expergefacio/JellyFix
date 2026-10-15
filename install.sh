#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/container.sh" "$@"
if ! version="$(docker exec "$container" jellyfin --version 2>&1)"; then
    # Linuxserver supplies a build-version label even if its CLI cannot run in a bare exec environment.
    version="$(docker inspect --format '{{ index .Config.Labels "build_version" }}' "$container")"
fi
if ! [[ "$version" =~ (^|[^0-9])12\.2(\.0)?([^0-9]|$) ]]; then
    echo "This prototype requires Jellyfin 12.2. Version reported: $version" >&2
    exit 1
fi
if docker exec "$container" test -e "$target"; then
    echo 'This plugin folder already exists. No files changed.' >&2
    exit 1
fi
docker exec -u 0 "$container" mkdir -p "$target"
docker cp "$script_dir/plugin/." "$container:$target/"
docker exec -u 0 "$container" chmod 755 "$target"
docker exec -u 0 "$container" chmod 644 "$target/Jellyfin.Plugin.JellyFix.dll" "$target/meta.json"
docker exec -u 0 "$container" chown -R --reference=/config/data/plugins "$target"
# Retire prior builds of the same plugin, preserving them for rollback.
for old_name in SubtitlePriority_0.1.0.0 SubtitlePriority_0.1.1.0 JellyFix_0.1.2.0 JellyFix_0.2.0.0; do
    legacy="/config/data/plugins/$old_name"
    if docker exec "$container" test -d "$legacy"; then
        backup="/config/data/plugin-backups/${old_name}_$(date -u +%Y%m%dT%H%M%SZ)"
        docker exec -u 0 "$container" mkdir -p /config/data/plugin-backups
        docker exec -u 0 "$container" mv "$legacy" "$backup"
    fi
done
echo "Installed JellyFix in $container. Restarting Jellyfin..."
docker restart "$container"
echo 'Open Dashboard > Plugins > JellyFix to configure. Reload the web client after changing its title.'
