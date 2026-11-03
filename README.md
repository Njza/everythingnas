# nas-search

Everything-style instant file name search for a Synology NAS. One Python file, standard library only.
It runs in Docker on the NAS, rescans the shares on a timer, and serves a search page.

## Deploy (DSM 7, Container Manager)

1. Create `/volume1/docker/nas-search/` and copy `nas_search.py` and `docker-compose.yml` into it.
2. Edit `docker-compose.yml`: set `NAS_NAME`, and list the shares to search under `volumes`.
3. Container Manager → Project → Create → pick that folder → it finds `docker-compose.yml` → Done.
4. Open `http://<nas-ip>:8765`. The first scan of ~1M files takes a few minutes; the page shows "首次扫描中…" until then.

## Search syntax

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

Not supported yet: `size:`, `dm:` and the other functions that need file size or date in the index.

Click a row to copy its `\\NAS\share\...` path; paste it into Win+R or Explorer to open it.

## Windows search bar (`NasSearchBar/`)

A WPF app that pops up a search bar over the desktop and opens results in their default app (Word, Excel, ...),
straight from the NAS share. It talks to the server above through `/search`.

1. Install the .NET 10 Desktop Runtime once per PC; without it the exe offers the download when it starts.
   If no prompt shows up, run this in a terminal:

   ```
   winget install Microsoft.DotNet.DesktopRuntime.10
   ```

   Then copy `dist/NasSearch.exe` (0.3 MB) anywhere and run it. The first run opens the settings window.
2. Settings (tray icon → right-click → 设置), saved in `%APPDATA%\NasSearch\settings.json`:
   - 地址: the NAS's LAN `host:port`, e.g. `192.168.1.10:8765` (plain http; LAN only by design).
     测试连接 checks it. Files open by the same host, `\\192.168.1.10\share\folder\file`, whatever
     `NAS_NAME` the server uses.
   - 账号 / 密码: only when the server sets `AUTH`. The password is encrypted with Windows DPAPI
     (readable only by the same Windows user on the same PC).
   - 开机自动启动: shows the bar on the desktop at Windows login (the current user's `Run` registry key).
   - 快捷键: click the box and press the new combination (needs Ctrl, Alt or Win). It takes effect on save;
     if another app already owns it, the old one stays and the window says so. If the saved one is taken
     at startup, the app asks for another one; the tray icon still opens the bar.
3. The bar stays on the desktop like a widget (not on top of other windows). The hotkey (default `Ctrl+Alt+Space`)
   brings it to the front with the search box focused, or hides it when it is already in front. `Enter` opens the selected file, `Ctrl+Enter` opens its folder,
   `Esc` clears the search. Clicking a row opens it too; right-clicking opens its folder. Tray menu: 设置, 退出.
   The funnel button lists every format the server has indexed (from `/exts`); tick some and every search
   only returns those (it adds `ext:a;b` to the query). The ticks are saved in settings.json.
4. Pin a result with the pin button on its row or `Ctrl+D` (again to unpin). With the search box empty the bar
   lists the pins, newest first; pinned files also come first among search results.
   Saved in `%APPDATA%\NasSearch\pins.json`.

Windows must already be able to open `\\<地址 host>\share` (open it once in Explorer and save the password).

Build (no Windows needed; output in `dist/`, a 0.3 MB exe that runs on the installed .NET Desktop Runtime):

```bash
docker run --rm -v "$PWD":/w -v nas-search-nuget:/root/.nuget/packages -w /w/NasSearchBar \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o /w/dist
```

On Windows with the .NET 10 SDK: `dotnet publish NasSearchBar -c Release -o dist`.

## Notes

- Skips Synology metadata folders (`@eaDir`, `@tmp`, ...) and `#recycle` / `#snapshot`.
- RAM: about 300 MB per million entries, about double during a rescan.
- Plain HTTP, meant for the LAN: do not port-forward it to the internet. Set `AUTH` if others on your LAN should not see the file names.
- Tests: `python3 -m unittest test_nas_search` for the server, `dotnet test NasSearchBar.Tests` for the client logic
  (runs on any OS, e.g. in the same Docker image).
