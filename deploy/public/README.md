# Self-host Nodicarta Core

[简体中文](README.zh-CN.md)

Nodicarta Core 1.0 runs on Linux x86-64 with Docker Engine and the Docker Compose plugin. This directory deploys the published image `ghcr.io/yafeiml/nodikarta-core:1.0.0`. A fresh installation includes PostgreSQL 18 on a private Docker network. If you already operate PostgreSQL, see [Use an existing database](#use-an-existing-database).

## First installation

From the release source directory, copy these files to a permanent **new** deployment directory. The following commands use `/home/docker/nodicarta` on the Linux host. Run subsequent Compose commands from that directory.

```sh
sudo install -d -m 0755 /home/docker/nodicarta
sudo cp -a deploy/public/. /home/docker/nodicarta/
cd /home/docker/nodicarta
sudo sh init.sh
sudo docker compose pull
sudo docker compose up -d --no-build
sudo docker compose logs --tail=50 core
```

Open **http://127.0.0.1:8080/** on this host. Enter the one-time setup code from the Core log and choose your administrator name and password. The code expires after 30 minutes. Before an owner exists, `sudo docker compose restart core` issues a fresh code; read the latest log entry. Sign in, create a project, then open **Devices → Add device** to issue a separate pairing code for Edge.

The default HTTP port listens on the host loopback address. To pair another machine, configure a reachable address first; see the next section. If port 8080 is in use, change `NODICARTA_PORT` in `.env` and run `sudo docker compose up -d --no-build`.

`init.sh` creates a private `.env` and independent random application and database-administrator passwords. The application role owns its database and has no PostgreSQL superuser privileges. Database files live under `data/postgres/`, including versions and attachments. Web authentication keys live under `data/keys/`. Preserve these together. To use a mirror, pass a versioned image or digest to `init.sh`.

## Access from another device

For **HTTPS**, point a TLS reverse proxy to Core. In `.env` set `NODICARTA_LOCAL_HTTP=false`, `NODICARTA_PUBLIC_URL` to the public HTTPS root URL and `NODICARTA_TRUSTED_PROXY` to the proxy's exact source IP as seen by the Core container. Set `X-Forwarded-Proto: https` at the proxy. A host proxy can use the loopback port. A proxy in another container needs a shared network and a route to the Core service. Recreate Core after editing these settings.

For **trusted LAN testing**, set `NODICARTA_BIND_ADDRESS` to the host's LAN or Tailscale interface IP and keep `NODICARTA_LOCAL_HTTP=true`. Run `sudo docker compose up -d --no-build`. Open `http://HOST-IP:8080/` in a browser on the Edge machine before pairing. Plain HTTP transports administrator credentials and pairing tokens without TLS. PostgreSQL has no published host port.

## Use an existing database

1. In PostgreSQL, create a dedicated login and database owned by that login. The following example runs in an administrator's interactive `psql` shell. `\password` prompts without putting the secret in SQL history.

   ```sql
   CREATE ROLE nodicarta LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
   \password nodicarta
   CREATE DATABASE nodicarta OWNER nodicarta;
   ```

2. In a fresh deployment directory run `sudo sh init.sh --external`. Edit the private `.env` and put your Npgsql connection string in `NODICARTA_DATABASE`. Use a database address reachable **from the Core container**. For example, for a PostgreSQL container on a shared Docker network set `Host=db-service;Database=nodicarta;Username=nodicarta;Password=YOUR_DB_PASSWORD`. For a remote PostgreSQL service configure its required TLS options and certificate trust. The application creates and migrates its schema on startup.
3. If PostgreSQL runs on an existing Docker network, set `COMPOSE_FILE=compose.yaml:compose.external-network.yaml` and add `NODICARTA_DB_NETWORK` with the network's actual name. For a separately reachable database server leave `COMPOSE_FILE=compose.yaml`.
4. Run `sudo docker compose pull`, then `sudo docker compose up -d --no-build`. The external deployment starts only Core. Complete the same first-owner setup.

## Backup, upgrade and restore

For the bundled database, create a restricted backup destination outside the deployment directory and run:

```sh
sudo install -d -m 0700 /home/docker/nodicarta-backups
sudo sh backup.sh /home/docker/nodicarta-backups
```

The script stops a running Core while taking a PostgreSQL custom-format dump and archiving its private configuration, keys and bundled Compose files, then starts Core again. It creates a new timestamped backup directory with checksums and leaves earlier backups intact. Protect backup contents like passwords and keep an off-host copy. With external PostgreSQL, stop Core, capture an independent `pg_dump -Fc` of its dedicated database plus the matching `.env`, Compose files and `data/keys/`, then start Core. Include any custom overrides or TLS certificate files in your backup.

Test a bundled backup in **a separate new deployment directory** using a unique Compose project and an unused loopback port. Copy the public deployment files there. Replace the two paths below with the new deployment directory and the backup directory printed by the script. Run as root:

```sh
cd /path/to/new-deployment
backup=/path/to/backup
(cd "$backup" && sha256sum -c SHA256SUMS)
tar -xf "$backup/runtime.tar"
install -d -m 0700 data
install -d -m 0755 data/postgres
sed -i 's/^NODICARTA_PORT=.*/NODICARTA_PORT=18080/;s/^NODICARTA_BIND_ADDRESS=.*/NODICARTA_BIND_ADDRESS=127.0.0.1/' .env
docker compose -p nodicarta-restore up -d --wait db
docker compose -p nodicarta-restore exec -T db pg_restore --exit-on-error --no-owner --role=nodicarta -U postgres -d nodicarta < "$backup/database.dump"
docker compose -p nodicarta-restore up -d --no-build
```

Sign in to the restored instance and verify a project, its permissions and an original attachment. For a restore drill of an HTTPS deployment, use an isolated HTTPS proxy or explicitly enable loopback HTTP and clear the external URL in the **restore copy** of `.env`.

Before upgrading, complete the backup and restore drill, record the current image digest, then change `NODICARTA_CORE_IMAGE` in `.env` to the new release or digest. Run `sudo docker compose pull core` and `sudo docker compose up -d --no-build core`. Check `sudo docker compose ps`, sign in and check the same project from a paired Edge. If a migration requires a rollback, use the previous Core image with the matching restored database and keys, retaining the newer database for reconciliation.

To reset the administrator password interactively in the running instance:

```sh
sudo docker compose exec core dotnet ProjectHub.Core.dll reset-password
```
