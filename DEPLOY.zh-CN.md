# NAS 部署指南

NAS 上跑一个容器建文件名索引，电脑上用 `NasSearch.exe` 搜索，搜到的文件通过网络共享直接打开。下文以 NAS 地址 `192.168.1.10` 为例。

## NAS 端

需要：DSM 7.2+、已安装 **Container Manager**、已开启 SMB 文件服务。建议在路由器里给 NAS 设固定 IP。

1. 从 GitHub 仓库下载 [`nas_search.py`](https://raw.githubusercontent.com/Njza/everythingnas/main/nas_search.py) 和 [`docker-compose.yml`](https://raw.githubusercontent.com/Njza/everythingnas/main/docker-compose.yml)（打开链接后右键另存为），在 File Station 里建文件夹 `docker/nas-search/` 放进去。
2. Container Manager → **项目** → **新增** → 路径选 `docker/nas-search` → 使用现有的 `docker-compose.yml` → 完成。
3. 浏览器打开 `https://192.168.1.10:8765`（浏览器会提示证书不受信任：这是群晖自带的证书，选继续访问），用自己的 NAS 账号登录。看到"以 NAS 账号 … 登录，能搜 …"就完成了（第一次扫描时显示"第一次扫描进行中"，100 万个文件要几分钟）。

## 账号：每人只搜自己的文件夹

不用另外建账号：每人用**自己的 NAS 账号**（就是打开 `\\192.168.1.10` 用的那个）登录，只搜得到群晖里这个账号有权限打开的共享文件夹。权限照常在 DSM 的 **控制面板 → 共享文件夹 → 编辑 → 权限** 里设，改了 30 分钟内生效。

- 想看某人能搜到什么：用他的账号在浏览器打开 `https://192.168.1.10:8765`（换账号要关掉浏览器重开，或用无痕窗口）。
- 权限按共享文件夹算：在某人能读的共享文件夹里，单独对他禁止的子文件夹，他仍然搜得到里面的文件名（打不开）。
- 开了**两步验证**的账号不能用来搜索。
- 账号要有 **File Station** 权限（控制面板 → 用户账号 → 编辑 → 应用程序，默认就有）。
- 同一台电脑 5 分钟内登录失败 5 次，要等 5 分钟才能再试。这是为了不触发群晖的自动封锁：所有登录在群晖看来都来自这个容器，被封了就谁都登不上。
- 不想要登录：`docker-compose.yml` 里写 `DSM_URL: ""`，然后重新构建项目，谁都能搜到全部文件。
- 密码不会明文传：服务器用群晖自带的证书走 HTTPS（`docker-compose.yml` 里挂载了 `/usr/syno/etc/certificate/system/default`）。exe 第一次 **测试连接** 时记住这张证书，之后只认它。

默认会搜存储空间 1 上的所有共享文件夹，但不搜 `homes`（各用户的个人文件夹），也自动跳过群晖系统文件夹（`@` 开头）、回收站和快照。以下情况才需要改 `docker-compose.yml`：

| 需求 | 改法 |
|---|---|
| 有存储空间 2 | 去掉 `# - /volume2:/data/volume2:ro` 前面的 `# `（没有存储空间 2 就别加） |
| 再排除别的共享文件夹 | `SKIP_SHARES` 里用逗号隔开加上，如 `SKIP_SHARES: homes,财务`；写成 `SKIP_SHARES: ""` 则连 `homes` 也搜 |
| 只搜部分共享文件夹 | 删掉 `/volume1` 那行，每个共享文件夹写一行 `- /volume1/文档:/data/文档:ro`，`/data/` 后面的名字必须和共享文件夹名一致 |
| 改索引更新频率 | `CHECK_MINUTES: 1` 是多久查一次新建、删除和改名的文件（只看每个文件夹的修改日期，只重新列出变了的文件夹）；`SCAN_MINUTES: 30` 是多久全部重扫一次（顺带更新改过的文件的大小和日期）。改完重新构建项目，网页上写的频率会跟着变。检查可能让硬盘无法休眠：要休眠就把 `CHECK_MINUTES` 改成和 `SCAN_MINUTES` 一样，比如两个都写 `240` |

> 不要把 8765 端口转发到外网：它只为局域网设计。

## 电脑端

1. 安装运行环境（每台电脑一次）：`winget install Microsoft.DotNet.DesktopRuntime.10`
2. 从 [Releases](https://github.com/Njza/everythingnas/releases/latest) 下载 `NasSearch.exe`，放到固定位置（如 `D:\Tools\`）后运行，UAC 点"是"。
3. 设置里填 **地址** `192.168.1.10:8765` 和这个人的 NAS **账号**、**密码**，点 **测试连接**，显示"可用"后保存。地址不填就只搜本机硬盘。需要 v1.2.0 以上的 exe（旧版只会明文 HTTP，连不上）。
4. **让 Windows 记住 NAS 密码（必做）**：资源管理器地址栏输入 `\\192.168.1.10`，用 NAS 账号登录并勾选"记住我的凭据"。不做这一步，文件搜得到但打不开。

用法：`Ctrl+Alt+Space` 呼出，`Enter` 打开，`Ctrl+Enter` 打开所在文件夹，`Ctrl+D` 置顶，`Esc` 清空。

## 更新

### 网页一键更新 `nas_search.py`

浏览器打开 `https://192.168.1.10:8765`，标题下面写着 **有新版本 v…** 时，点 **更新并重启**。服务器从 GitHub 取新的 `nas_search.py` 换上并重启，页面几秒后自动刷新，显示新版本号；索引会重新扫描。

- 要求 `docker-compose.yml` 里用 `- .:/app` 挂载整个文件夹（v1.2 起的写法）。旧写法是只读挂载 `nas_search.py` 一个文件，点了会提示写不了。
- 只更新 `nas_search.py`。新版本要是也改了 `docker-compose.yml`，还要按下面"改了 `docker-compose.yml`"的步骤重新构建。
- 能登录搜索的人都能点，但只会装上 GitHub 上比现在新的版本。

### 只换 `nas_search.py`：重新启动就行

1. 下载新的 [`nas_search.py`](https://raw.githubusercontent.com/Njza/everythingnas/main/nas_search.py)（打开链接后右键另存为）。
2. File Station → `docker/nas-search/` → **上传** → 选 **覆盖**。
3. Container Manager → **项目** → 选中 `nas-search` → **操作** → **重新启动**。`nas_search.py` 是挂载进容器的，重启就用上新的，不用重新构建。
4. 浏览器打开 `https://192.168.1.10:8765`，标题旁的 **NAS 端 v…** 是新版本号，就换好了。

### 改了 `docker-compose.yml`：要重新构建

只重新启动不会读新的 `docker-compose.yml`。

1. File Station 里把新的 `docker-compose.yml` 上传到 `docker/nas-search/`，选 **覆盖**。
2. Container Manager → **项目** → 选中 `nas-search` → **操作** → **停止**，再 **操作** → **构建**。容器按新设置重建，索引重新扫描（100 万个文件要几分钟）。

也可以用 SSH（控制面板 → 终端机和 SNMP → 启用 SSH 功能，用管理员账号登录）：

```bash
cd /volume1/docker/nas-search
sudo docker compose up -d --force-recreate
```

只换了 `nas_search.py` 时用 `sudo docker compose restart`。提示没有 `docker compose` 就写成 `docker-compose`。

### 从 v1.1 升级到 v1.2

先更新电脑端，再更新 NAS 端：旧版 exe 连不上新的 NAS 端。

1. 等每台电脑的 exe 更新到 v1.2.0：托盘图标 → 设置，底部有版本号。还没更新的，右键托盘图标点 **重启以更新**。
2. 按上面的步骤换 `nas_search.py`（先不用重新启动）。
3. 换 `docker-compose.yml`：直接用新的覆盖。旧文件改过的话（比如加了 `/volume2`），也可以只在旧文件里改四处，并删掉 `AUTH` 那行：

   ```yaml
       environment:
         DSM_URL: https://host.docker.internal:5001                # 加进 environment
       extra_hosts:                                                # 新加一段，和 environment、volumes 对齐
         - host.docker.internal:host-gateway
       volumes:
         - .:/app                                                  # 替换原来的 - ./nas_search.py:/app/nas_search.py:ro
         - /usr/syno/etc/certificate/system/default:/cert:ro       # 加进 volumes
   ```

4. 按上面的步骤 **停止**、**构建**。
5. 浏览器打开 `https://192.168.1.10:8765`，用一个 NAS 账号登录，看列出的共享文件夹对不对。
6. 每台电脑：设置里填这个人的 NAS **账号**、**密码**，点 **测试连接**（这一步会记住 NAS 的证书），显示"可用"后保存。

构建后容器一直停止：看容器日志，写着 `no certificate` 就是第 3 步证书那行没加对。

### 电脑端

自动更新。启动时和每 12 小时检查一次 Releases，有新版就下载好，下次启动生效（托盘菜单里也能立即重启）。等不及就在设置底部点 **检查更新**，下载好后点 **重启以更新**。

## 常见问题

| 现象 | 解决 |
|---|---|
| 测试连接"连不上" | 浏览器先打开 `https://IP:8765` 确认；不通就看容器是否在运行、DSM 防火墙是否放行 8765；exe 要 v1.2.0 以上 |
| "账号或密码不对" | 填的是 NAS 账号密码吗？用浏览器打开 `https://IP:8765` 登录一次，看页面显示的原因 |
| "NAS 的证书变了" | 群晖换了证书（续期或导入新证书）：确认是这台 NAS 后，设置里点 **测试连接** 再保存。群晖换证书后要重启项目 |
| 测试连接"连不上"，但 `:8765` 能打开 | 用这个账号在浏览器登录 `:8765`，页面会写原因：两步验证、没有 File Station 权限、失败太多次等 |
| 页面写"连不上群晖 DSM" | DSM 的 HTTPS 端口不是 5001，或防火墙挡了容器：把 `DSM_URL` 改成 `https://192.168.1.10:端口`，或在防火墙里放行 `172.16.0.0/12` |
| 页面写"群晖封锁了搜索服务的 IP" | 控制面板 → 安全性 → 保护 → 允许/封锁列表，把封锁的 IP 删掉 |
| 搜得到打不开 | 没做电脑端第 4 步，或该 NAS 账号没有这个共享文件夹的权限 |
| 新文件搜不到、删掉的还在 | NAS 端每分钟查一次，等 1 分钟左右再搜。超过几分钟还不行：多半是复制工具把文件夹日期改回了原来的（如 robocopy `/DCOPY:T`），要等下次全部重扫（默认 30 分钟） |
| 改过内容的文件，大小和日期没变 | 要等下次全部重扫（默认 30 分钟） |
| 容器启动就停止 | 看容器日志，多半是 `volumes` 里写了不存在的路径；日志写 "no certificate" 就是证书没挂载上 |
