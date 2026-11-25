#!/bin/sh
# Run as root on a Linux x86-64 Docker host. Uses its own directory, port and Compose project.
set -eu
umask 077
export LC_ALL=C
repo=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd -P)
work=$(mktemp -d)
NODICARTA_INSTALL_DIR="$work/nodicarta-qa-$(basename "$work" | tr -dc 'a-zA-Z0-9' | tr A-Z a-z)"
NODICARTA_BIND_ADDRESS=127.0.0.1
NODICARTA_PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')
export NODICARTA_INSTALL_DIR NODICARTA_BIND_ADDRESS NODICARTA_PORT
phase='fresh installation'
compose() { (cd "$NODICARTA_INSTALL_DIR" && docker compose "$@"); }
cleanup() {
    result=$?
    if [ "$result" -ne 0 ]; then printf 'Smoke test failed during %s\n' "$phase" >&2; fi
    if [ -f "$NODICARTA_INSTALL_DIR/.env" ]; then
        compose down >/dev/null || return 1
    fi
    rm -rf -- "$work"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
run_install() { sh "$repo/deploy/public/install.sh" >"$work/result"; }
setup_code() { sed -nE 's/^.*首次安装码: ([[:xdigit:]]{32})$/\1/p' "$work/result"; }

run_install
url="http://127.0.0.1:$NODICARTA_PORT"
grep -qF "$url/" "$work/result"
code=$(setup_code)
[ -n "$code" ]
sha256sum "$NODICARTA_INSTALL_DIR/.env" >"$work/config.sha256"
phase='repeat before administrator setup'
run_install
[ "$(setup_code)" = "$code" ]
sha256sum --check --status "$work/config.sha256"
db=$(compose ps -q db)
phase='database port inspection'
[ -n "$db" ]
[ -z "$(docker port "$db")" ]

phase='fetch administrator setup form'
curl --noproxy '*' -fsS -c "$work/cookies" "$url/setup" -o "$work/form"
csrf=$(sed -nE 's/.*name="__RequestVerificationToken"[^>]*value="([^"]+)".*/\1/p' "$work/form" | head -n 1)
[ -n "$csrf" ]
password=$(od -An -v -N24 -tx1 /dev/urandom | tr -d ' \n')
status=$(curl --noproxy '*' -sS -b "$work/cookies" -D "$work/headers" -o /dev/null -w '%{http_code}' \
    --data-urlencode "__RequestVerificationToken=$csrf" --data-urlencode "code=$code" \
    --data-urlencode 'username=qa' --data-urlencode "password=$password" \
    --data-urlencode "confirmPassword=$password" "$url/setup")
phase="create administrator (HTTP $status)"
[ "$status" = 302 ]
grep -iq '^location: /login' "$work/headers"
core=$(compose ps -q core)
phase='restart stopped database'
compose stop db >/dev/null
run_install
[ "$(compose ps -q core)" = "$core" ]
[ "$(compose ps -q db)" = "$db" ]
[ -z "$(setup_code)" ]
sha256sum --check --status "$work/config.sha256"
status=$(curl --noproxy '*' -sS -o /dev/null -w '%{http_code} %{redirect_url}' "$url/")
[ "$status" = "302 $url/login" ]
printf '%s\n' 'PASS: first installation, admin setup, private PostgreSQL, database restart and repeat installation.'
