#!/bin/sh
set -eu
umask 077
cd "$(dirname "$0")"

mode=bundled
if [ "${1:-}" = --external ]; then mode=external; shift; fi
if [ "$#" -gt 1 ]; then
    echo 'Usage: sh init.sh [--external] [registry/image:version or registry/image@sha256:digest]' >&2
    exit 2
fi
if [ -e .env ] || [ -L .env ]; then
    echo 'An .env already exists. Edit it to change settings; existing credentials are preserved.' >&2
    exit 1
fi
if [ -d data ] && [ -n "$(find data -mindepth 1 ! -type d -print -quit)" ]; then
    echo 'Existing data found. Restore its matching .env before starting Core.' >&2
    exit 1
fi
image=${1:-${NODICARTA_CORE_IMAGE:-ghcr.io/yafeiml/nodikarta-core:1.0.0}}
# A tagless image silently means latest. Accept explicit release tags or SHA-256 digests.
case "$image" in
    *[!a-zA-Z0-9._/@:-]*|'') echo 'Supply a versioned Core image from the release or your registry mirror.' >&2; exit 2 ;;
esac
case "${image##*/}" in
    *@sha256:*) digest=${image##*@sha256:}
                if [ "${#digest}" -ne 64 ]; then echo 'Invalid SHA-256 digest.' >&2; exit 2; fi
                case "$digest" in *[!0-9a-f]*) echo 'Invalid SHA-256 digest.' >&2; exit 2 ;; esac ;;
    *:latest|*:) echo 'Choose an explicit version tag or image digest.' >&2; exit 2 ;;
    *:*) ;;
    *) echo 'Choose an explicit version tag or image digest.' >&2; exit 2 ;;
esac
if [ "$(id -u)" -ne 0 ]; then
    echo 'Run init.sh with sudo to prepare the Linux data directories.' >&2
    exit 1
fi

random_password() {
    value=$(od -An -v -N32 -tx1 /dev/urandom | tr -d ' \n')
    [ "${#value}" -eq 64 ] || return 1
    printf '%s' "$value"
}

tmp=$(mktemp ./.env.XXXXXX)
trap 'rm -f "$tmp"' EXIT
files=compose.yaml
db_connection=
if [ "$mode" = bundled ]; then
    files="$files:compose.postgres.yaml"
    app_password=$(random_password)
    admin_password=$(random_password)
    db_connection="Host=db;Database=nodicarta;Username=nodicarta;Password=$app_password"
fi
{
    echo "COMPOSE_FILE=$files"
    echo "NODICARTA_CORE_IMAGE=$image"
    echo 'NODICARTA_BIND_ADDRESS=127.0.0.1'
    echo 'NODICARTA_PORT=8080'
    echo 'NODICARTA_LOCAL_HTTP=true'
    echo 'NODICARTA_PUBLIC_URL='
    echo 'NODICARTA_TRUSTED_PROXY='
    printf "NODICARTA_DATABASE='%s'\n" "$db_connection"
    if [ "$mode" = bundled ]; then
        echo "NODICARTA_DB_PASSWORD=$app_password"
        echo "NODICARTA_POSTGRES_PASSWORD=$admin_password"
    fi
} > "$tmp"
install -d -m 0700 data
install -d -m 0700 -o 1654 -g 1654 data/keys
# PostgreSQL 18 drops privileges and traverses this mount to reach its 0700 PGDATA.
# The enclosing host directory (data) remains private.
if [ "$mode" = bundled ]; then install -d -m 0755 data/postgres; fi
ln "$tmp" .env
echo 'Deployment directory initialized. Run Docker Compose commands in this directory.'
if [ "$mode" = external ]; then echo 'Set NODICARTA_DATABASE in .env before starting.'; fi
