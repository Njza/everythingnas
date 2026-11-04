# nas-search

Everything-style instant file name search for this PC and a Synology NAS. A Windows search bar
(`NasSearch.exe`) indexes the PC's own drives and asks a small server on the NAS for its files; results from both
come in one list and open straight in their default app.

中文部署指南：[DEPLOY.zh-CN.md](DEPLOY.zh-CN.md)

- **This PC**: NTFS drives are read like Everything does, from the master file table (C: with 1.1M entries in
  ~20 s), then kept current from the change journal, so new, renamed and deleted files show up within a second.
- **NAS**: one Python file, standard library only, in Docker. It rescans the shares on a timer and keeps names,
  sizes and dates in memory, so a search never touches the disks.
- **Fast**: a search over a million names takes 10–50 ms on the PC; the PC's results show first and the NAS's
  join them.
- **Updates itself** from this repo's GitHub releases.

## Install

**NAS** (DSM 7.2+, Container Manager, SMB on):

1. Put `nas_search.py` and `docker-compose.yml` from the [latest release](https://github.com/Njza/everythingnas/releases/latest)
   into `/volume1/docker/nas-search/`.
2. Container Manager → Project → Create → that folder → use the existing `docker-compose.yml` → Done.
   It needs no edits: it searches every shared folder on `/volume1`, whose top folders become `\\NAS\share` paths.
   Uncomment the `/volume2` line if that volume exists, set `AUTH` to require a login, or list single shares instead.
3. Open `http://<nas-ip>:8765` to check it runs: one line with the number of indexed entries, or
   "first scan running" (~1M files take a few minutes).

**Windows PC**:

1. Install the .NET 10 Desktop Runtime once: `winget install Microsoft.DotNet.DesktopRuntime.10`.
2. Put `NasSearch.exe` from the [latest release](https://github.com/Njza/everythingnas/releases/latest) somewhere it
   can stay (it updates itself in place) and run it; accept the UAC prompt.
3. In the settings, set 地址 to the NAS's `ip:port`, e.g. `192.168.1.10:8765`, and press 测试连接.
   Leave it empty to search only this PC.
4. Open `\\192.168.1.10` once in Explorer and save the NAS password: results open through that share.

## Using it

The hotkey (default `Ctrl+Alt+Space`) or the tray icon brings the bar up; the hotkey hides it again when it is in
front. It stays on the desktop like a widget, not on top of other windows, and follows Windows' light or dark theme.

| Key | Action |
|---|---|
| `Enter`, click | open in its default app (Word, Excel, ...) |
| `Ctrl+Enter`, right-click | open its folder with the file selected |
| `Ctrl+D`, the pin button | pin; an empty search box lists the pins, and pinned files come first in results |
| `Esc` | clear the search |

The funnel button limits every search to the ticked formats (it adds `ext:a;b`). Each start rebuilds the PC's index
in memory; until it is ready a small window shows the progress instead of the bar.

### Search syntax

The same rules as Everything with its default settings.

| Query | Matches |
|---|---|
| `alien 1979` | names that contain both words (case-insensitive) |
| `jpg\|png` | either word. `\|` binds tighter than a space: `cat jpg\|png` means `cat <jpg\|png>` |
| `!sample` | names that do not contain `sample` |
| `<alien 1979>\|aliens` | `< >` groups terms |
| `"alien 1979"` | the exact text, spaces and operators included |
| `*.mkv`, `IMG_????.jpg` | whole-name wildcards; `[ ]` are plain characters |
| `movies\ alien` | a term with `\` or `/` matches the full path, e.g. `\\NAS\video\*` (`*` stops at a `\`, `**` does not) |
| `ext:pdf;docx` | files with one of these extensions |
| `file:`, `folder:` | files only, folders only, e.g. `folder:合同` |
| `path:`, `case:`, `regex:` | match the full path, match case, regular expression, e.g. `regex:^IMG_\d+` |

Not supported yet: `size:`, `dm:` and Everything's other functions.

### Settings

Tray icon → right-click → 设置; saved in `%APPDATA%\NasSearch\settings.json`.

- **地址**: the NAS's LAN `host:port` (plain http, LAN only by design). Files open by this host,
  `\\192.168.1.10\share\folder\file`, whatever `NAS_NAME` the server uses.
- **账号 / 密码**: only when the server sets `AUTH`. The password is encrypted with Windows DPAPI.
- **快捷键**: click the box and press the new combination (needs Ctrl, Alt or Win).
- **开机自动启动**: a Task Scheduler task with the account's highest rights, so no UAC prompt at login.

### Admin rights

The exe asks for the account's highest rights, because reading the NTFS master file table and change journal needs
them. Files still open as the normal user, through Explorer. Without admin rights, or on FAT/exFAT drives, it walks
the folders instead and walks them again every 30 minutes.

## Updates

The search bar checks [the latest release](https://github.com/Njza/everythingnas/releases/latest) at start and every
12 hours. A newer `NasSearch.exe` is downloaded, checked against GitHub's SHA-256 digest and put in place of the
running one (which Windows allows to be renamed); it runs from the next start, or at once from the tray menu's
重启以更新. Pre-releases are skipped. The NAS side is updated by hand: replace `nas_search.py` and restart the project.

## Releasing

Pushing a version tag does the rest ([`.github/workflows/release.yml`](.github/workflows/release.yml)): both test
suites run, the exe is built with the tag's version, and a release with the exe and the two server files is published.

```bash
git tag v1.1.0
git push origin v1.1.0
```

A tag with a suffix (`v1.1.0-beta`) becomes a pre-release. Every push to `main` and every pull request runs the tests
and builds the exe as a downloadable artifact ([`ci.yml`](.github/workflows/ci.yml)).

## Development

```bash
python -m unittest test_nas_search          # server
dotnet test NasSearchBar.Tests              # search bar logic
dotnet publish NasSearchBar -c Release -o dist   # 0.3 MB exe on the installed .NET runtime
```

The exe builds on macOS or Linux too, e.g. in Docker:

```bash
docker run --rm -v "$PWD":/w -v nas-search-nuget:/root/.nuget/packages -w /w/NasSearchBar \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o /w/dist
```

## Notes

- Skipped on the NAS: Synology's own folders (`@eaDir`, `@appstore`, every `@` folder), `#recycle`, `#snapshot`.
- RAM: about 320 MB per million entries on the NAS (double during a rescan), about 380 MB for C: with 1.1M on the PC.
- A NAS file deleted since the last scan stays listed until the next one (`SCAN_MINUTES`, 30 by default); each scan
  wakes the disks.
- Plain HTTP, meant for the LAN: do not port-forward it to the internet. Everyone who can reach port 8765 can search
  the file names; set `AUTH` if that is not fine.
