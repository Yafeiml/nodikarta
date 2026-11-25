# Self-host Nodicarta Core

[简体中文](README.zh-CN.md)

## First installation

On an always-on Linux x86-64 host with Docker Engine, the Docker Compose plugin and curl installed, run the following command. The installer requests sudo access when you run it as a regular user.

```sh
(installer=$(mktemp) && trap 'rm -f "$installer"' EXIT && curl -fsSL https://raw.githubusercontent.com/Yafeiml/nodikarta/main/deploy/public/install.sh -o "$installer" && sh "$installer")
```

The installer starts Core and PostgreSQL 18 under `/home/docker/nodicarta`. It creates the database, generates separate passwords for the application and database administrator, and writes Core's connection string to the private `.env` file. Only Core publishes a host port. If you want to supply your own PostgreSQL, follow [Use an existing database](#use-an-existing-database) before running the installer.

1. Open the **web address** printed in the terminal. The installer uses port 8080 by default. If it finds one trusted LAN address, you can open that address from a device on the same network. If it prints `127.0.0.1`, open the page on the installation host.
2. Enter the **one-time setup code** printed alongside the address and choose your administrator name and password. The code lasts 30 minutes. Once the administrator exists, subsequent runs show the address for sign-in.
3. Sign in, choose **Projects → Create project**, then **Devices → Add device**. Grant access to the project and generate a pairing code. On your Edge device, enter the Core address reachable from that device and the new pairing code. Select the authorized project and bind its local folder. [Download Edge](https://github.com/Yafeiml/nodikarta/releases/latest)

Project records and attachments live in `data/postgres/`, and web authentication keys live in `data/keys/`. Run the maintenance commands below from `/home/docker/nodicarta`. If the setup code expires before you create the administrator, run `sudo docker compose restart core` and read the new code with `sudo docker compose logs --tail=50 core`.

If port 8080 is busy on first installation, replace the final `sh "$installer"` in the command with `NODICARTA_PORT=18080 sh "$installer"`. If an installed instance shows `127.0.0.1` and you need access from another computer, set `NODICARTA_BIND_ADDRESS` in the deployment's `.env` to the server's LAN IPv4 address, then run `sudo docker compose up -d --no-build core` in the deployment directory.

## Use an existing database

For a new Core backed by a PostgreSQL server you already operate, copy the contents of this source directory (`deploy/public/`) into a fresh permanent deployment directory. Run the commands below from that copy.

1. In PostgreSQL, create a dedicated login and database owned by that login. The following example runs in an administrator's interactive `psql` shell. `\password` prompts without putting the secret in SQL history.

   ```sql
   CREATE ROLE nodicarta LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
   \password nodicarta
   CREATE DATABASE nodicarta OWNER nodicarta;
   ```

2. Run `sudo sh init.sh --external`. Edit the private `.env` and put your Npgsql connection string in `NODICARTA_DATABASE`. Use a database address reachable **from the Core container**. For example, for a PostgreSQL container on a shared Docker network set `Host=db-service;Database=nodicarta;Username=nodicarta;Password=YOUR_DB_PASSWORD`. For a remote PostgreSQL service configure its required TLS options and certificate trust. The application creates and migrates its schema on startup.
3. If PostgreSQL runs on an existing Docker network, set `COMPOSE_FILE=compose.yaml:compose.external-network.yaml` and add `NODICARTA_DB_NETWORK` with the network's actual name. For a separately reachable database server leave `COMPOSE_FILE=compose.yaml`.
4. Run `sudo docker compose pull`, then `sudo docker compose up -d --no-build`. Open `http://127.0.0.1:8080/` on the installation host with the default settings from `init.sh`. Get the setup code with `sudo docker compose logs --tail=50 core` and create the administrator account.

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
sed -i -e 's/^NODICARTA_PORT=.*/NODICARTA_PORT=18080/' \
       -e 's/^NODICARTA_BIND_ADDRESS=.*/NODICARTA_BIND_ADDRESS=127.0.0.1/' \
       -e 's/^NODICARTA_LOCAL_HTTP=.*/NODICARTA_LOCAL_HTTP=true/' \
       -e 's/^NODICARTA_PUBLIC_URL=.*/NODICARTA_PUBLIC_URL=/' .env
docker compose -p nodicarta-restore up -d --wait db
docker compose -p nodicarta-restore exec -T db pg_restore --exit-on-error --no-owner --role=nodicarta -U postgres -d nodicarta < "$backup/database.dump"
docker compose -p nodicarta-restore up -d --no-build
```

On the restore host, open `http://127.0.0.1:18080/`. Sign in and verify a project, its permissions and an original attachment.

Before upgrading, complete the backup and restore drill, record the current image digest, then change `NODICARTA_CORE_IMAGE` in `.env` to the new release or digest. Run `sudo docker compose pull core` and `sudo docker compose up -d --no-build core`. Check `sudo docker compose ps`, sign in and check the same project from a paired Edge. If a migration requires a rollback, use the previous Core image with the matching restored database and keys, retaining the newer database for reconciliation.

To reset the administrator password interactively in the running instance:

```sh
sudo docker compose exec core dotnet ProjectHub.Core.dll reset-password
```
