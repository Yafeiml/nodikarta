#!/bin/sh
# Bundled PostgreSQL only. External database operators supply their own pg_dump.
set -eu
umask 077
if [ "$#" -ne 1 ] || [ ! -d "$1" ]; then
    echo 'Usage: sudo sh backup.sh /existing/backup-directory' >&2
    exit 2
fi
out=$(cd "$1" && pwd -P)
cd "$(dirname "$0")"
if ! docker compose config --services | grep -qx db; then
    echo 'The bundled database is not configured. See the external database backup instructions.' >&2
    exit 1
fi
stage=$(mktemp -d "$out/.nodicarta-XXXXXX")
restart=false
cleanup() {
    status=$?
    if [ "$restart" = true ]; then docker compose start core || status=1; fi
    rm -f "$stage/database.dump" "$stage/runtime.tar" "$stage/SHA256SUMS"
    rmdir "$stage" 2>/dev/null || true
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
if [ -n "$(docker compose ps --status running -q core)" ]; then
    restart=true
    docker compose stop core
fi
docker compose exec -T db pg_dump -U nodicarta -d nodicarta -Fc > "$stage/database.dump"
docker compose exec -T db pg_restore -l < "$stage/database.dump" > /dev/null
tar -cf "$stage/runtime.tar" .env compose.yaml compose.postgres.yaml postgres-init.sql data/keys
tar -tf "$stage/runtime.tar" > /dev/null
(cd "$stage" && sha256sum database.dump runtime.tar > SHA256SUMS && sha256sum -c SHA256SUMS)
if [ "$restart" = true ]; then docker compose start core; restart=false; fi
dest="$out/nodicarta-$(date -u +%Y%m%dT%H%M%SZ)-${stage##*-}"
mv -T "$stage" "$dest"
echo "Backup: $dest"
