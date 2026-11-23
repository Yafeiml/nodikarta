# 自托管脉络 Core

[English](README.md)

Core 1.0 提供 Linux x86-64 镜像 `ghcr.io/yafeiml/nodikarta-core:1.0.0`。部署主机需要 Docker Engine 和 Docker Compose 插件。全新安装会在独立的 Docker 网络中启动 PostgreSQL 18；已有数据库可选用[外部 PostgreSQL](#复用已有-postgresql)。

## 首次安装

在发行版源码根目录执行以下命令。示例使用新的 `/home/docker/nodicarta` 长期存放配置和数据。后续 Compose 命令都在这个运行目录执行。

```sh
sudo install -d -m 0755 /home/docker/nodicarta
sudo cp -a deploy/public/. /home/docker/nodicarta/
cd /home/docker/nodicarta
sudo sh init.sh
sudo docker compose pull
sudo docker compose up -d --no-build
sudo docker compose logs --tail=50 core
```

在这台主机的浏览器打开 **http://127.0.0.1:8080/**，输入日志中的一次性安装码，创建管理员账号和密码。安装码有效 30 分钟。创建管理员前，可运行 `sudo docker compose restart core` 并查看最新日志重新取码。登录后先建项目，再到「设备 → 添加设备」为 Edge 生成独立的配对码。

默认 HTTP 端口仅监听宿主机回环地址。其他电脑上的 Edge 需要能从该电脑访问的 Core 地址，设置见下一节。如 8080 已占用，编辑 `.env` 的 `NODICARTA_PORT` 并再次执行 `sudo docker compose up -d --no-build`。

`init.sh` 创建仅管理员可读的 `.env`，并分别为应用账号和数据库管理员生成随机密码。应用账号拥有专库，使用普通 PostgreSQL 角色运行。数据库、知识、附件版本及原件存放于 `data/postgres/`；网页登录密钥存放于 `data/keys/`。备份时一并保留。使用其他镜像仓库时，可向 `init.sh` 传入明确的版本标签或摘要。

## 从其他设备访问

**HTTPS。** 将 TLS 反向代理转发至 Core。在 `.env` 设置 `NODICARTA_LOCAL_HTTP=false`，`NODICARTA_PUBLIC_URL` 填写公开 HTTPS 根地址，`NODICARTA_TRUSTED_PROXY` 填写 Core 容器实际看到的直接代理 IP。让代理设置 `X-Forwarded-Proto: https`。宿主机上的代理可以访问回环端口；其他容器内的代理需要共享网络并能路由到 Core 服务。修改后重新执行 `sudo docker compose up -d --no-build`。

**可信局域网测试。** 将 `NODICARTA_BIND_ADDRESS` 改为宿主机的局域网或 Tailscale 网卡 IP，保留 `NODICARTA_LOCAL_HTTP=true`，再执行 `sudo docker compose up -d --no-build`。先在运行 Edge 的电脑上用浏览器打开 `http://宿主机IP:8080/`，确认可达后配对。HTTP 会明文传输管理员凭据和配对令牌。此部署仅发布 Core 端口，PostgreSQL 在 Docker 网络内提供服务。

## 复用已有 PostgreSQL

1. 在已有 PostgreSQL 的管理员 `psql` 交互终端创建专用角色和数据库。`\password` 会提示输入密码，密码不会进入 SQL 命令历史。

   ```sql
   CREATE ROLE nodicarta LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
   \password nodicarta
   CREATE DATABASE nodicarta OWNER nodicarta;
   ```

2. 在新运行目录执行 `sudo sh init.sh --external`，编辑私有 `.env` 的 `NODICARTA_DATABASE`。填写 **Core 容器内可访问**的 Npgsql 连接串。例如，同一 Docker 网络可填写 `Host=db-service;Database=nodicarta;Username=nodicarta;Password=你的专库密码`。远程数据库按实际要求配置 TLS 及证书信任。Core 首次启动时会在专库中创建并迁移自己的 schema。
3. 使用已有 PostgreSQL 容器的 Docker 网络时，设置 `COMPOSE_FILE=compose.yaml:compose.external-network.yaml` 并增加 `NODICARTA_DB_NETWORK=已有网络名`。使用可直接访问的数据库服务器时保留 `COMPOSE_FILE=compose.yaml`。
4. 运行 `sudo docker compose pull` 和 `sudo docker compose up -d --no-build`。此外部模式仅启动 Core，随后按相同步骤创建管理员。

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
sed -i 's/^NODICARTA_PORT=.*/NODICARTA_PORT=18080/;s/^NODICARTA_BIND_ADDRESS=.*/NODICARTA_BIND_ADDRESS=127.0.0.1/' .env
docker compose -p nodicarta-restore up -d --wait db
docker compose -p nodicarta-restore exec -T db pg_restore --exit-on-error --no-owner --role=nodicarta -U postgres -d nodicarta < "$backup/database.dump"
docker compose -p nodicarta-restore up -d --no-build
```

登录恢复实例，检查项目、设备授权和一份附件原件。若备份来自 HTTPS 环境，为演练实例配置独立代理，或只在**演练副本**的 `.env` 中开启回环 HTTP 并清空公开地址。

升级前先完成备份与恢复演练，记录原镜像摘要，将 `.env` 的 `NODICARTA_CORE_IMAGE` 改为新版本或摘要，执行 `sudo docker compose pull core` 和 `sudo docker compose up -d --no-build core`。检查 `sudo docker compose ps`、网页登录、已配对设备读取项目。若迁移后需回滚，将升级前的镜像连接到匹配的恢复专库与密钥，保留新库以便核对后续新增数据。

忘记管理员密码时，可以在运行目录交互重设：

```sh
sudo docker compose exec core dotnet ProjectHub.Core.dll reset-password
```
