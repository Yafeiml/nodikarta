#!/bin/sh
# Install the published Nodicarta Core stack, or resume the installation in this directory.
set -eu
umask 077
die() { printf '%s\n' "$*" >&2; exit 1; }

if [ "$(id -u)" != 0 ]; then
    command -v sudo >/dev/null || die 'Run this installer as root, or use an account with sudo access.'
    exec sudo -- env NODICARTA_INSTALL_DIR="${NODICARTA_INSTALL_DIR:-}" \
        NODICARTA_BIND_ADDRESS="${NODICARTA_BIND_ADDRESS:-}" NODICARTA_PORT="${NODICARTA_PORT:-}" \
        NODICARTA_CORE_IMAGE="${NODICARTA_CORE_IMAGE:-}" sh "$0" "$@"
fi
[ "$#" = 0 ] || die 'Configure a fresh installation with NODICARTA_INSTALL_DIR, NODICARTA_BIND_ADDRESS, NODICARTA_PORT or NODICARTA_CORE_IMAGE.'
for cmd in docker curl flock ss ip sha256sum; do
    command -v "$cmd" >/dev/null || die "Missing $cmd. Install Docker Engine, its Compose plugin, curl, iproute2 and util-linux on the Linux host."
done
docker compose version >/dev/null || die 'Install the Docker Compose plugin.'
engine=$(docker context inspect --format '{{.Endpoints.docker.Host}}' "$(docker context show)")
case "$engine" in unix:///*) ;; *) die 'Run this installer on the Linux host with its local Docker Engine.' ;; esac
case "$(docker version --format '{{.Server.Os}}/{{.Server.Arch}}')" in
    linux/amd64) ;;
    *) die 'This Core release requires a running Linux x86-64 Docker Engine.' ;;
esac

src=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
dir=${NODICARTA_INSTALL_DIR:-/home/docker/nodicarta}
bind=${NODICARTA_BIND_ADDRESS:-}
port=${NODICARTA_PORT:-8080}
image=${NODICARTA_CORE_IMAGE:-ghcr.io/yafeiml/nodikarta-core:1.0.0}
# For an existing stack Compose must read its own credentials, port and database topology.
unset NODICARTA_CORE_IMAGE NODICARTA_BIND_ADDRESS NODICARTA_PORT NODICARTA_DATABASE \
    NODICARTA_DB_PASSWORD NODICARTA_POSTGRES_PASSWORD NODICARTA_LOCAL_HTTP NODICARTA_PUBLIC_URL \
    NODICARTA_TRUSTED_PROXY NODICARTA_DB_NETWORK COMPOSE_FILE COMPOSE_PROJECT_NAME COMPOSE_ENV_FILES COMPOSE_DISABLE_ENV_FILE
case "$dir" in /*) ;; *) die 'NODICARTA_INSTALL_DIR must be an absolute path.' ;; esac
[ "$dir" != / ] && [ ! -L "$dir" ] || die 'Choose a dedicated deployment directory.'
install -d -m 0700 /run/nodicarta-installer
exec 9</run/nodicarta-installer
flock -n 9 || die 'Another Nodicarta installation is running. Retry after it finishes.'
if [ ! -d "$dir" ]; then install -d -m 0755 "$dir"; fi
cd "$dir"
dir=$(pwd -P)
[ "$dir" != / ] && [ "$(stat -c %u .)" = 0 ] && [ -z "$(find . -maxdepth 0 -perm /022 -print)" ] ||
    die 'The deployment directory must be owned by root and writable only by its owner.'
stage=$(mktemp -d ./.install.XXXXXXXX)
trap 'rm -rf -- "$stage"' EXIT
trap 'exit 1' HUP INT TERM
state_file=.nodicarta-install-state
state=legacy
if [ -e "$state_file" ] || [ -L "$state_file" ]; then
    [ ! -L "$state_file" ] && [ -f "$state_file" ] || die "Check the deployment marker $dir/$state_file."
    state=$(cat "$state_file")
    case "$state" in pending|ready) ;; *) die "Unrecognized deployment marker $dir/$state_file." ;; esac
fi

# Ignore loopback aliases and container bridges. Prefer a single LAN over a VPN;
# ambiguity falls back to loopback rather than publishing on every interface.
private_address() {
    ip -o -4 addr show up scope global | awk '
        $2 !~ /^(lo$|docker|br-|virbr|veth|cni|podman|lxc|kube|flannel)/ {
            split($4, cidr, "/"); split(cidr[1], a, ".")
            if (a[1]==10 || (a[1]==172 && a[2]>=16 && a[2]<=31) || (a[1]==192 && a[2]==168)) lan[cidr[1]]=1
            else if ($2=="tailscale0" && a[1]==100 && a[2]>=64 && a[2]<=127) vpn[cidr[1]]=1
        }
        END {
            for (ip in lan) { n++; pick=ip }
            if (!n) for (ip in vpn) { n++; pick=ip }
            print n==1 ? pick : "127.0.0.1"
        }'
}

# Pin the deployment bundle separately from Edge releases and verify local copies too.
bundle_files() {
    cat <<'HASHES'
412230f350de9c8630e7bbc739ef8641b9efce6fee113960f8320b871cb5bc11  init.sh
a21e080e1872c546f90f1e315dc5e0c0fc69693073041f7b77975d2995e9a62a  compose.yaml
0f4f361eaaf4d184d6b7920f63ff9df157f9d1f69f982a07ad1b74c9a89ce755  compose.postgres.yaml
7c7e9884a3857d592d8b94277086ccebb2d30d6689f7ee49bd78f64e4f598cdc  compose.external-network.yaml
0259e661d49fad07f5ea3ac05c4aef81814f564face4b9c1f6c41f94bdfd10ce  postgres-init.sql
b25de1b0ce1557c9f8cb382843857a728b4596d68fc0daeed23b100f1dbefd41  backup.sh
HASHES
}

# A stack started previously with compose -p must keep the same project and containers.
workdir_label="label=com.docker.compose.project.working_dir=$dir"
projects=$(docker ps -a --filter "$workdir_label" --format '{{.Label "com.docker.compose.project"}}' | sort -u)
case "$projects" in *'
'*) die "Multiple Docker Compose projects use $dir. Select the intended project manually." ;; esac
if [ -n "$projects" ]; then COMPOSE_PROJECT_NAME=$projects; export COMPOSE_PROJECT_NAME; fi
check_project() {
    project=$(docker compose "$@" config --format json | sed -n 's/^  "name": "\([a-z0-9_-]*\)",*$/\1/p')
    [ -n "$project" ] || die "Cannot read the Nodicarta Compose project in $dir."
    locations=$(docker ps -a --filter "label=com.docker.compose.project=$project" --format '{{.Label "com.docker.compose.project.working_dir"}}' | sort -u)
    [ -z "$locations" ] || [ "$locations" = "$dir" ] || die "Docker Compose project $project already belongs to $locations. Use its existing deployment directory."
}

[ ! -L .env ] || die 'The .env must be a regular file in this deployment directory.'
if [ -f .env ]; then
    grep -q '^NODICARTA_CORE_IMAGE=' .env && [ -f compose.yaml ] ||
        die "An existing configuration was found in $dir. Select the Nodicarta deployment directory."
else
    [ ! -e .env ] && [ -z "$projects" ] && [ "$state" != ready ] || die 'Restore the matching .env for this deployment before continuing.'
    other=$(find . -mindepth 1 -maxdepth 1 ! -name '.install.*' ! -name "$state_file" ! -name data ! -name '.env.example' \
        ! -name 'README*.md' ! -name init.sh ! -name install.sh ! -name backup.sh ! -name compose.yaml \
        ! -name compose.external-network.yaml ! -name compose.postgres.yaml ! -name postgres-init.sql -print -quit)
    [ -z "$other" ] || die "Existing files found in $dir. Choose a dedicated deployment directory."
    if [ -e data ] || [ -L data ]; then
        [ -d data ] && [ -z "$(find data ! -type d -print -quit)" ] || die 'Existing data found. Restore its matching .env before starting Core.'
    fi
    if [ -z "${NODICARTA_INSTALL_DIR:-}" ]; then
        others=$(docker ps -a --filter 'label=org.opencontainers.image.title=Nodicarta Core' --format '{{.Names}}')
        [ -z "$others" ] || die "Existing Nodicarta Core found ($others). Set NODICARTA_INSTALL_DIR to its existing deployment directory."
    fi
    if [ -z "$bind" ]; then
        bind=$(private_address)
        if [ "$bind" = 127.0.0.1 ]; then
            printf '%s\n' 'No unique private IPv4 address found. Using localhost (127.0.0.1). For another device, set NODICARTA_BIND_ADDRESS in .env to the server LAN or Tailscale IPv4 and run docker compose up -d --no-build.' >&2
        fi
    fi
    case "$bind" in localhost) bind=127.0.0.1 ;; esac
    case "$bind" in *[!0-9.]*) die 'NODICARTA_BIND_ADDRESS must be a local IPv4 address.' ;; esac
    ip -o -4 addr show up | awk '{split($4,a,"/");print a[1]}' | grep -Fxq -- "$bind" || die 'Choose a specific IPv4 address assigned to this host (or 127.0.0.1).'
    case "$port" in ''|0*|*[!0-9]*) die 'NODICARTA_PORT must be between 1 and 65535.' ;; esac
    [ "${#port}" -le 5 ] && [ "$port" -le 65535 ] || die 'NODICARTA_PORT must be between 1 and 65535.'
    listeners=$(ss -H -ltn "sport = :$port")
    published=$(docker ps --format '{{.Ports}}')
    if printf '%s\n' "$listeners" | awk -v ip="$bind" -v p="$port" '$4==ip ":" p || $4=="0.0.0.0:" p || $4=="*:" p || $4=="[::]:" p {found=1} END{exit !found}' ||
       printf '%s\n' "$published" | tr ',' '\n' | awk -v ip="$bind" -v p="$port" '{sub(/^ +/,""); split($0,b,"->"); if (b[2] ~ /\/tcp$/ && (b[1]==ip ":" p || b[1]=="0.0.0.0:" p || b[1]=="[::]:" p)) found=1} END{exit !found}'; then
        die "Port $bind:$port is already in use. Set NODICARTA_PORT to a free port and rerun."
    fi
    base=https://raw.githubusercontent.com/Yafeiml/nodikarta/4ed1a5ca4da1da073b4f7bb57c8dc95f3ecbf1af/deploy/public
    bundle_files > "$stage/SHA256SUMS"
    while read -r hash file; do
        case "$src" in
            */deploy/public) cp -- "$src/$file" "$stage/$file" ;;
            *) curl -q -fsSL --proto '=https' --proto-redir '=https' --retry 2 --connect-timeout 10 --max-time 90 "$base/$file" -o "$stage/$file" ;;
        esac
    done < "$stage/SHA256SUMS"
    (cd "$stage" && sha256sum -c --status SHA256SUMS) || die 'Deployment bundle failed verification. Run the installer from a clean download or matching source checkout.'
    while read -r hash file; do
        if [ -e "$file" ] || [ -L "$file" ]; then
            [ ! -L "$file" ] && [ -f "$file" ] && cmp -s "$file" "$stage/$file" || die "Keeping existing $dir/$file. Use its matching .env or select a new directory."
        fi
    done < "$stage/SHA256SUMS"
    sh "$stage/init.sh" "$image" >/dev/null
    sed -e "s/^NODICARTA_BIND_ADDRESS=.*/NODICARTA_BIND_ADDRESS=$bind/" -e "s/^NODICARTA_PORT=.*/NODICARTA_PORT=$port/" "$stage/.env" > "$stage/config"
    while read -r hash file; do
        if [ ! -e "$file" ]; then chmod 0644 "$stage/$file"; ln "$stage/$file" "$file"; fi
    done < "$stage/SHA256SUMS"
    check_project --env-file "$stage/config"
    cp -a "$stage/data" .
    state=pending
    printf '%s\n' "$state" > "$stage/state"
    mv -fT "$stage/state" "$state_file"
    ln "$stage/config" .env
fi

# Execute only the public Compose definitions, including the existing-database modes.
# A custom stack may be inspected when running, but its operator owns its startup.
files=$(sed -n 's/^COMPOSE_FILE=//p' .env)
case "$files" in
    compose.yaml) required='compose.yaml' ;;
    compose.yaml:compose.postgres.yaml) required='compose.yaml compose.postgres.yaml postgres-init.sql' ;;
    compose.yaml:compose.external-network.yaml) required='compose.yaml compose.external-network.yaml' ;;
    *) required='' ;;
esac
if [ -n "$projects" ] && [ -n "$required" ]; then
    used=$(docker ps -a --filter "$workdir_label" --format '{{.Label "com.docker.compose.project.config_files"}}' | sort -u)
    expected="$dir/compose.yaml"
    case "$files" in *:*) expected="$expected,$dir/${files#*:}" ;; esac
    if [ "$used" != "$expected" ]; then required=''; fi
fi
for file in $required; do
    if [ -L "$file" ] || ! bundle_files | awk -v f="$file" '$2==f' | sha256sum -c --status; then required=''; break; fi
done
if [ -n "$required" ]; then
    COMPOSE_FILE=$files; COMPOSE_PATH_SEPARATOR=:; export COMPOSE_FILE COMPOSE_PATH_SEPARATOR
    check_project
    # Compose creates a missing bind mount as an empty directory. Check persisted
    # files first, even after compose down removed all previous containers.
    [ -d data/keys ] || die "Core key storage is missing in $dir/data. Restore or mount the existing data before starting Core."
    if [ "$files" = compose.yaml:compose.postgres.yaml ]; then
        [ -d data/postgres ] || die "PostgreSQL storage is missing in $dir/data. Restore or mount the existing data before starting Core."
    fi
    previous=$(docker ps -a --filter "$workdir_label" --filter 'label=com.docker.compose.service=core' --format '{{.ID}}' | head -n 1)
    if [ -n "$previous" ]; then
        previous=$(docker inspect --format '{{.State.StartedAt}}' "$previous")
        case "$previous" in 0001-*) previous='' ;; esac
    fi
    keys=$(find data/keys -maxdepth 1 -type f -name 'key-*.xml' -print -quit)
    if [ "$state" = ready ] || [ -n "$keys" ] || { [ "$state" = legacy ] && [ -n "$previous" ]; }; then
        [ -n "$keys" ] ||
            die 'Existing Core key files are missing. Restore or mount its matching keys before continuing.'
        if [ "$files" = compose.yaml:compose.postgres.yaml ]; then
            grep -qx 18 data/postgres/18/docker/PG_VERSION 2>/dev/null ||
                die 'Existing PostgreSQL cluster is missing. Restore or mount its matching PostgreSQL 18 data before continuing.'
        fi
    fi
    docker compose up -d --no-build --no-recreate core
else
    printf '%s\n' 'Custom Compose configuration detected. Inspecting the running Core.' >&2
fi
id=$(docker ps --filter "$workdir_label" --filter 'label=com.docker.compose.service=core' --format '{{.ID}}')
case "$id" in ''|*'
'*) die "Expected one running Core in $dir. Start the existing deployment with its usual Compose command and rerun." ;; esac
endpoint=$(docker port "$id" 8080/tcp | head -n 1)
[ -n "$endpoint" ] || die "Core has no published web port. Check the existing configuration in $dir."
specific_ipv4=true
case "$endpoint" in
    0.0.0.0:*|'[::]':*) specific_ipv4=false; endpoint="$(private_address):${endpoint##*:}" ;;
    '['*|127.*) specific_ipv4=false ;;
esac
public=$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$id" | sed -n 's/^ProjectHub__PublicUrl=//p')
url=${public:-http://$endpoint}
url=${url%/}

net_pid=
fetch() {
    if [ -n "$net_pid" ]; then
        nsenter -t "$net_pid" -n curl -q -s --noproxy '*' --max-time 3 "$@"
    else
        curl -q -s --noproxy '*' --max-time 3 "$@"
    fi
}
root_redirect() { fetch -o /dev/null -w '%{http_code} %{redirect_url}' "$url/"; }
page=
attempt=0
while [ "$attempt" -lt 60 ]; do
    net_pid=
    redirect=$(root_redirect) || redirect=
    if [ -z "$redirect" ] && [ -n "$required" ] && [ -z "$public" ] && [ "$specific_ipv4" = true ] &&
       grep -qi microsoft /proc/sys/kernel/osrelease && command -v nsenter >/dev/null; then
        # WSL mirrored networking can reset a request to its own LAN address.
        # Verify the same published host and port from the container's network namespace.
        net_pid=$(docker inspect --format '{{.State.Pid}}' "$id")
        redirect=$(root_redirect) || redirect=
    fi
    case "$redirect" in
        "302 $url/setup") page=setup ;;
        "302 $url/login") page=login ;;
        *) page= ;;
    esac
    if [ -n "$page" ] && fetch -f "$url/$page" -o "$stage/page" &&
        grep -Fq "id=\"$page-title\"" "$stage/page"; then break; fi
    page=
    attempt=$((attempt+1))
    sleep 1
done
[ -n "$page" ] || die "Core did not serve setup or sign-in at $url/. In $dir check docker compose ps and docker compose logs core."
if [ -n "$required" ] && [ "$state" != ready ]; then
    printf 'ready\n' > "$stage/state"
    mv -fT "$stage/state" "$state_file"
fi
printf '\nWeb: %s/\n' "$url"
if [ -n "$net_pid" ]; then printf '%s\n' 'Verified the published address from the Docker network. Open it in your browser.'; fi
if [ "$page" = login ]; then
    printf '%s\n' 'Administrator already configured / 管理员已创建。'
    exit 0
fi

# Docker retains older starts in the same log. Only inspect this process's code and
# its matching expiry. Never restart a live instance to produce another code.
started=$(docker inspect --format '{{.State.StartedAt}}' "$id")
valid=$(docker logs --since "$started" "$id" 2>&1 | awk '
    /^Setup code \/ 首次安装码: / {code=$NF; expiry=""}
    /^失效时间: / && code!="" {expiry=$NF}
    END {if (length(code)==32 && code !~ /[^0-9A-F]/ && expiry!="") print code,expiry}')
code=${valid%% *}
expiry=${valid#* }
expires=$(date -d "$expiry" +%s 2>/dev/null) || expires=0
if [ -n "$code" ] && [ "$expires" -gt "$(date +%s)" ]; then
    printf 'Setup code / 首次安装码: %s\n' "$code"
    printf 'Expires / 有效期至: %s\n' "$expiry"
else
    die "No unexpired setup code. To issue one, run sudo docker compose restart core in $dir and rerun this installer."
fi
