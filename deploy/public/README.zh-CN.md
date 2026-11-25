# 自托管脉络 Core

[English](README.md)

## 首次安装

在持续在线的 Linux x86-64 主机上装好 Docker Engine、Docker Compose 插件和 curl 后，运行下面这一条命令。以普通账号执行时，安装器会通过 sudo 请求管理员权限。

```sh
(installer=$(mktemp) && trap 'rm -f "$installer"' EXIT && curl -fsSL https://raw.githubusercontent.com/Yafeiml/nodikarta/main/deploy/public/install.sh -o "$installer" && sh "$installer")
```

安装器在 `/home/docker/nodicarta` 部署 Core 和 PostgreSQL 18，自动建库，分别生成应用账号和数据库管理员的密码，并把 Core 的连接串写入私有的 `.env` 文件。只有 Core 对宿主机开放端口。如果你想沿用自己的 PostgreSQL，可以直接按[复用已有 PostgreSQL](#复用已有-postgresql) 一节安装。

1. 打开终端显示的**网页地址**。默认端口是 8080。如果主机上只有一个符合条件的可信局域网地址，同一网络的电脑可以直接打开它。如果显示的是 `127.0.0.1`，请在安装 Core 的主机上打开。
2. 输入终端同时显示的**一次性安装码**，设置管理员账号和密码。安装码有效期为 30 分钟。已有管理员时，再次运行安装器会显示登录地址。
3. 登录后点「项目 → 创建项目」，再点「设备 → 添加设备」，授予项目权限并生成配对码。在 Edge 设备上填入这台设备能访问的 Core 地址和刚生成的配对码，选择已授权的项目并绑定本地目录。[下载 Edge](https://github.com/Yafeiml/nodikarta/releases/latest)

项目记录与附件原件在 `data/postgres/`，网页登录密钥在 `data/keys/`。后续维护命令都在 `/home/docker/nodicarta` 目录执行。如果还未创建管理员，安装码就已过期，可以运行 `sudo docker compose restart core`，再用 `sudo docker compose logs --tail=50 core` 查看新码。

如果首次安装提示 8080 已占用，把安装命令末尾的 `sh "$installer"` 改成 `NODICARTA_PORT=18080 sh "$installer"` 即可换端口。已安装后，如果终端显示 `127.0.0.1` 而你需要从另一台电脑访问，在部署目录的 `.env` 中将 `NODICARTA_BIND_ADDRESS` 改为这台服务器的局域网 IPv4，再执行 `sudo docker compose up -d --no-build core`。

## 复用已有 PostgreSQL

如果已有 PostgreSQL，要让新安装的 Core 使用它，先将源码中 `deploy/public/` 的内容复制到一个新的长期部署目录。以下命令在这个新目录执行。

1. 在已有 PostgreSQL 的管理员 `psql` 交互终端创建专用角色和数据库。`\password` 会提示输入密码，密码不会进入 SQL 命令历史。

   ```sql
   CREATE ROLE nodicarta LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
   \password nodicarta
   CREATE DATABASE nodicarta OWNER nodicarta;
   ```

2. 执行 `sudo sh init.sh --external`，编辑私有 `.env` 的 `NODICARTA_DATABASE`。填写 **Core 容器内可访问**的 Npgsql 连接串。例如，同一 Docker 网络可填写 `Host=db-service;Database=nodicarta;Username=nodicarta;Password=你的专库密码`。远程数据库按实际要求配置 TLS 及证书信任。Core 首次启动时会在专库中创建并迁移自己的 schema。
3. 使用已有 PostgreSQL 容器的 Docker 网络时，设置 `COMPOSE_FILE=compose.yaml:compose.external-network.yaml` 并增加 `NODICARTA_DB_NETWORK=已有网络名`。使用可直接访问的数据库服务器时保留 `COMPOSE_FILE=compose.yaml`。
4. 运行 `sudo docker compose pull` 和 `sudo docker compose up -d --no-build`。使用 `init.sh` 的默认端口和地址时，在安装主机上打开 `http://127.0.0.1:8080/`。运行 `sudo docker compose logs --tail=50 core` 查看首次安装码，输入后创建管理员账号。

## 备份、升级和还原

随附的 PostgreSQL 可以用以下命令备份。备份目录放在运行目录以外，限制为管理员访问。

```sh
sudo install -d -m 0700 /home/docker/nodicarta-backups
sudo sh backup.sh /home/docker/nodicarta-backups
```

脚本会暂停运行中的 Core，使用 `pg_dump` 创建一致性专库备份，打包部署配置、密钥及随附的 Compose 文件，随后恢复服务。每次生成独立的时间戳目录和校验清单。备份含凭据，应限制访问并保留异机副本。使用外部 PostgreSQL 时，停止 Core，使用数据库原生 `pg_dump -Fc` 备份专库，同时保存匹配的 `.env`、Compose 文件和 `data/keys/`，再恢复 Core。自定义 Compose 覆盖和 TLS 证书也应纳入备份。

**先在独立的新目录做恢复演练。** 将公开部署文件复制到新目录，准备独立的 Compose 项目名和空闲回环端口。以下命令以 root 执行，两处路径分别指向新运行目录及备份脚本返回的备份目录。

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

在演练主机上打开 `http://127.0.0.1:18080/`，登录并检查项目、设备授权和一份附件原件。

升级前先完成备份与恢复演练，记录原镜像摘要，将 `.env` 的 `NODICARTA_CORE_IMAGE` 改为新版本或摘要，执行 `sudo docker compose pull core` 和 `sudo docker compose up -d --no-build core`。检查 `sudo docker compose ps`、网页登录、已配对设备读取项目。若迁移后需回滚，将升级前的镜像连接到匹配的恢复专库与密钥，保留新库以便核对后续新增数据。

忘记管理员密码时，可以在运行目录交互重设：

```sh
sudo docker compose exec core dotnet ProjectHub.Core.dll reset-password
```
