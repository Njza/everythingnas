# NAS 部署指南

NAS 上跑一个容器建文件名索引，电脑上用 `NasSearch.exe` 搜索，搜到的文件通过网络共享直接打开。下文以 NAS 地址 `192.168.1.10` 为例。

## NAS 端

需要：DSM 7.2+、已安装 **Container Manager**、已开启 SMB 文件服务。建议在路由器里给 NAS 设固定 IP。

1. 从 GitHub 仓库下载 [`nas_search.py`](https://raw.githubusercontent.com/Njza/everythingnas/main/nas_search.py) 和 [`docker-compose.yml`](https://raw.githubusercontent.com/Njza/everythingnas/main/docker-compose.yml)（打开链接后右键另存为），在 File Station 里建文件夹 `docker/nas-search/` 放进去。
2. Container Manager → **项目** → **新增** → 路径选 `docker/nas-search` → 使用现有的 `docker-compose.yml` → 完成。
3. 浏览器打开 `http://192.168.1.10:8765`，看到 `entries indexed` 就完成了（第一次扫描时显示 `first scan running`，100 万个文件要几分钟）。

默认会搜存储空间 1 上的所有共享文件夹，但不搜 `homes`（各用户的个人文件夹），也自动跳过群晖系统文件夹（`@` 开头）、回收站和快照。以下情况才需要改 `docker-compose.yml`：

| 需求 | 改法 |
|---|---|
| 有存储空间 2 | 去掉 `# - /volume2:/data/volume2:ro` 前面的 `# `（没有存储空间 2 就别加） |
| 再排除别的共享文件夹 | `SKIP_SHARES` 里用逗号隔开加上，如 `SKIP_SHARES: homes,财务`；写成 `SKIP_SHARES: ""` 则连 `homes` 也搜 |
| 搜索要密码 | 去掉 `AUTH` 前面的 `# `，写成 `AUTH: "用户名:密码"`，电脑端设置里填同样的 |
| 只搜部分共享文件夹 | 删掉 `/volume1` 那行，每个共享文件夹写一行 `- /volume1/文档:/data/文档:ro`，`/data/` 后面的名字必须和共享文件夹名一致 |
| 硬盘要休眠 | 把 `SCAN_MINUTES` 调大，比如 `240`（每次扫描都会唤醒硬盘） |

> 局域网里谁都能搜到这些文件**名**（打开文件仍需 NAS 权限）。介意就开 `AUTH`。不要把 8765 端口转发到外网。

## 电脑端

1. 安装运行环境（每台电脑一次）：`winget install Microsoft.DotNet.DesktopRuntime.10`
2. 从 [Releases](https://github.com/Njza/everythingnas/releases/latest) 下载 `NasSearch.exe`，放到固定位置（如 `D:\Tools\`）后运行，UAC 点"是"。
3. 设置里填 **地址** `192.168.1.10:8765`，点 **测试连接**，显示"可用"后保存。地址不填就只搜本机硬盘。
4. **让 Windows 记住 NAS 密码（必做）**：资源管理器地址栏输入 `\\192.168.1.10`，用 NAS 账号登录并勾选"记住我的凭据"。不做这一步，文件搜得到但打不开。

用法：`Ctrl+Alt+Space` 呼出，`Enter` 打开，`Ctrl+Enter` 打开所在文件夹，`Ctrl+D` 置顶，`Esc` 清空。

## 更新

- **NAS 端程序**：从仓库下载新的 `nas_search.py` 覆盖旧的，在 Container Manager 里重新启动项目。
- **`docker-compose.yml`**：改完要重新构建项目，只重新启动不会生效。
- **电脑端**：自动更新。启动时和每 12 小时检查一次 Releases，有新版就下载好，下次启动生效（托盘菜单里也能立即重启）。

## 常见问题

| 现象 | 解决 |
|---|---|
| 测试连接"连不上" | 浏览器先打开 `http://IP:8765` 确认；不通就看容器是否在运行、DSM 防火墙是否放行 8765 |
| "账号或密码不对" | 设置里的账号密码要和 `AUTH` 一致 |
| 搜得到打不开 | 没做电脑端第 4 步，或该 NAS 账号没有这个共享文件夹的权限 |
| 新文件搜不到、删掉的还在 | NAS 端要等下次扫描（默认 30 分钟） |
| 容器启动就停止 | 看容器日志，多半是 `volumes` 里写了不存在的路径 |
