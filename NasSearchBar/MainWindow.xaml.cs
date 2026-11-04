using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NasSearchBar;

public partial class MainWindow : Window
{
    const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    // this PC answers in 15-100 ms; the pause only groups keystrokes typed in one burst
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly System.Windows.Forms.NotifyIcon tray;
    CancellationTokenSource? inflight;
    Settings settings = new();
    NasClient? nas;  // null when no address is set: then only this PC is searched
    // this PC's drives, always searched; scanned once at start, kept across settings changes
    static readonly LocalIndex local = new(LocalIndex.FixedDrives(), TimeSpan.FromMinutes(30));
    SettingsWindow? settingsWindow;
    readonly Pins pins = new(Pins.FilePath);
    Hit[] found = [];  // the last search results, in server order
    readonly IntPtr hwnd;
    KeyGesture? hotkey;  // the registered one

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        debounce.Tick += async (_, _) => await Search();
        // Alt+F4 only hides: a closed window could never come back on the hotkey. App shutdown ignores the cancel.
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        LoadSettings();
        hwnd = new WindowInteropHelper(this).EnsureHandle();  // the hotkey needs a window handle before the first show
        Theme.Frame(hwnd);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        var round = DWMWCP_ROUND;  // Windows 11 round corners; Windows 10 ignores it (square corners)
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        tray = new() { Icon = TrayIcon(), Text = "NAS 搜索", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowBar(); };
        tray.ContextMenuStrip = new();
        tray.ContextMenuStrip.Items.Add("设置", null, (_, _) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        Closed += (_, _) => tray.Dispose();

        Updater.CleanUp();
        updateCheck.Tick += async (_, _) => await CheckForUpdate();
        updateCheck.Start();
        _ = CheckForUpdate();
    }

    void LoadSettings()
    {
        try
        {
            settings = Settings.Load(Settings.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new();
            SetStatus("设置文件读不了，请重新设置", error: true);
        }
        nas = settings.Server is { } server
            ? new NasClient(server, settings.User, Secret.Unprotect(settings.ProtectedPassword))
            : null;
        ShowFilter();
    }

    // One row of the format filter; the CheckBox writes On back through its binding
    public sealed class ExtChoice(string ext, int count, bool on)
    {
        public string Ext => ext;
        public int Count => count;
        public bool On { get; set; } = on;
    }

    void ShowFilter()
    {
        FilterButton.Tag = settings.Exts == "" ? null : "on";
        FilterButton.ToolTip = settings.Exts == "" ? "只显示勾选的格式" : "只显示：" + settings.Exts.Replace(";", "、");
    }

    const int Rare = 3;  // formats with fewer files than this fold under "其他": mostly odd names like a.1.2.3
    List<ExtChoice> choices = [];  // every format, server order (most files first); the list shows a part
    bool showRare;

    HashSet<string> Ticked() => choices.Where(c => c.On).Select(c => c.Ext).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Ticked first, then the common ones; the rare ones when unfolded; typing searches all of them.
    // A fresh ItemsSource also redraws the ticks after FilterAll_Click (ExtChoice has no change events).
    void ShowChoices()
    {
        var q = FilterSearch.Text.Trim();
        var rare = choices.Count(c => !c.On && c.Count < Rare);
        FilterList.ItemsSource = (q != ""
                ? choices.Where(c => c.Ext.Contains(q, StringComparison.OrdinalIgnoreCase))
                : choices.Where(c => c.On || c.Count >= Rare || showRare))
            .OrderBy(c => !c.On)  // stable: keeps most files first in each group
            .ToList();
        RareButton.Content = showRare ? "收起少见格式" : $"其他 {rare} 种少见格式（少于 {Rare} 个文件）";
        RareButton.Visibility = q == "" && rare > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterHint.Visibility = q == "" ? Visibility.Visible : Visibility.Hidden;
    }

    void FilterSearch_TextChanged(object sender, TextChangedEventArgs e) => ShowChoices();

    void Rare_Click(object sender, RoutedEventArgs e)
    {
        showRare = !showRare;
        ShowChoices();
    }

    // Lists the saved formats at once, then every format this PC and the NAS have indexed
    async void Filter_Click(object sender, RoutedEventArgs e)
    {
        choices = settings.Exts.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => new ExtChoice(x, 0, true)).ToList();
        showRare = false;
        FilterSearch.Text = "";
        ShowChoices();  // TextChanged does not fire when the box was already empty
        FilterStatus.Text = "正在读取格式…";
        FilterPopup.IsOpen = true;
        FilterSearch.Focus();
        var nasExts = nas?.ExtsAsync(CancellationToken.None);
        var mine = (await local.ExtsAsync(CancellationToken.None)).Exts;
        ExtCount[] theirs = [];
        string? note = null;
        if (nasExts is not null)
        {
            try { theirs = (await nasExts).Exts; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                note = "连不上 NAS";
            }
        }
        var all = mine.Concat(theirs)
            .GroupBy(x => x.Ext, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExtCount(g.Key, g.Sum(x => x.Count)))
            .OrderByDescending(x => x.Count)
            .ToArray();
        if (!FilterPopup.IsOpen) return;  // closed while loading; closing already saved the ticks
        var on = Ticked();  // includes any ticked while loading
        var known = all.Select(x => x.Ext).ToHashSet(StringComparer.OrdinalIgnoreCase);
        choices = on.Where(x => !known.Contains(x)).Select(x => new ExtChoice(x, 0, true))  // saved, no longer on this PC or the NAS
            .Concat(all.Select(x => new ExtChoice(x.Ext, x.Count, on.Contains(x.Ext))))
            .ToList();
        ShowChoices();
        FilterStatus.Text = $"{all.Length:N0} 种格式，不勾 = 全部" + (note is null ? "" : $"（{note}）");
    }

    // Windows 11 gives the opaque popup small round corners and a frame, like its own menus; Windows 10 ignores it
    void FilterPopup_Opened(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(FilterPopup.Child) is HwndSource src)
        {
            var round = DWMWCP_ROUNDSMALL;
            DwmSetWindowAttribute(src.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            Theme.Frame(src.Handle);
        }
    }

    void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in choices) c.On = false;
        ShowChoices();
    }

    // Saves the ticks when the list closes and searches again with them
    async void FilterPopup_Closed(object? sender, EventArgs e)
    {
        var exts = string.Join(';', choices.Where(c => c.On).Select(c => c.Ext));
        if (exts == settings.Exts) return;
        settings = settings with { Exts = exts };
        ShowFilter();
        try
        {
            settings.Save(Settings.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("格式没保存：" + ex.Message, error: true);
        }
        await Search();
    }

    public void OpenSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(settings, TrySetHotkey);
        if (settingsWindow.ShowDialog() == true)
        {
            LoadSettings();
            SetStatus("");
        }
        settingsWindow = null;
    }

    public string SavedHotkey => settings.Hotkey;

    public bool ApplySavedHotkey() =>
        TrySetHotkey(Hotkeys.Parse(settings.Hotkey) ?? Hotkeys.Parse(new Settings().Hotkey)!);

    // Swaps the global hotkey. If Windows refuses the new one (another app owns it), the old one stays.
    public bool TrySetHotkey(KeyGesture g)
    {
        if (hotkey is not null) UnregisterHotKey(hwnd, HotkeyId);
        if (Register(g))
        {
            hotkey = g;
            tray.Text = "NAS 搜索  " + Hotkeys.Format(g);
            return true;
        }
        if (hotkey is not null) Register(hotkey);
        return false;
    }

    // WPF's ModifierKeys values are the same bits as Win32's MOD_ALT/CONTROL/SHIFT/WIN
    bool Register(KeyGesture g) =>
        RegisterHotKey(hwnd, HotkeyId, (uint)g.Modifiers | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(g.Key));

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam == HotkeyId)
        {
            if (IsActive) Hide(); else ShowBar();  // in front: hide; hidden or behind another app: bring it up
            handled = true;
        }
        return IntPtr.Zero;
    }

    IndexingWindow? indexing;

    readonly DispatcherTimer updateCheck = new() { Interval = TimeSpan.FromHours(12) };

    // Puts a newer GitHub release in place for the next start; offers to restart now from the tray
    async Task CheckForUpdate()
    {
        Version? latest;
        try
        {
            latest = await Updater.UpdateAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidDataException)
        {
            return;  // offline, GitHub down, a bad download or a read-only folder: try again next time
        }
        if (latest is null) return;
        updateCheck.Stop();  // in place; one update per run is enough
        var v = latest.ToString(3);
        tray.ContextMenuStrip!.Items.Insert(0, new System.Windows.Forms.ToolStripMenuItem($"重启以更新到 {v}", null, (_, _) =>
        {
            // the new exe waits for this one's single-instance lock (see App), then starts as usual
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--updated") { UseShellExecute = false })?.Dispose();
            Application.Current.Shutdown();
        }));
        tray.ShowBalloonTip(8000, "NAS 搜索", $"新版本 {v} 已下载，下次启动时生效。右键托盘图标可以立即重启", System.Windows.Forms.ToolTipIcon.Info);
    }

    public void ShowBar()
    {
        if (!local.Ready)  // the bar is usable once this PC's index is built; until then a dialog shows the progress
        {
            if (indexing is null)
            {
                indexing = new IndexingWindow(local);
                indexing.Closed += (_, _) =>
                {
                    indexing = null;
                    if (local.Ready) ShowBar();  // it closed itself because the index is ready
                };
                indexing.Show();
            }
            indexing.Activate();
            return;
        }
        Render();
        if (!IsVisible)  // it lives on the desktop: placed once, then left where it is
        {
            Opacity = 0;  // hidden while it moves to the right screen
            Show();
            PlaceOnCursorScreen();
            Opacity = 1;
        }
        Activate();
        Query.Focus();
        Query.SelectAll();
    }

    // Centered on the screen under the mouse, in physical pixels: WPF's Left/Top are in DIPs and go wrong
    // when the screens have different scales.
    void PlaceOnCursorScreen()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        // first onto that screen, so Windows switches the window to its DPI, then center at the new size
        SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        var w = (int)Math.Round(Width * VisualTreeHelper.GetDpi(this).DpiScaleX);
        SetWindowPos(hwnd, IntPtr.Zero, area.Left + (area.Width - w) / 2, area.Top + area.Height / 5, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text == "" ? Visibility.Visible : Visibility.Hidden;
        debounce.Stop();
        debounce.Start();
    }

    async Task Search()
    {
        debounce.Stop();
        inflight?.Cancel();
        var cts = inflight = new CancellationTokenSource();
        var q = Query.Text.Trim();
        if (q == "")
        {
            found = [];
            Render();  // the pins
            return;
        }
        q = settings.Filtered(q);
        var nasTask = nas?.SearchAsync(q, cts.Token);  // asked first, so it runs while this PC is searched
        SearchResponse mine;
        try
        {
            mine = await local.SearchAsync(q, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        if (cts.IsCancellationRequested) return;  // a newer query already ran
        // This PC's results show at once; the NAS's join them when they come (a slow or unreachable NAS takes up
        // to the 5 s timeout). Not awaited here, so Enter opens what is listed now instead of waiting for the NAS.
        found = mine.Results;
        Render();
        SetStatus($"{mine.Total:N0} 个结果" + (nasTask is null ? "" : " · 正在搜 NAS…"));
        if (nasTask is not null) _ = AddNasResults(nasTask, mine, cts);
    }

    async Task AddNasResults(Task<SearchResponse> nasTask, SearchResponse mine, CancellationTokenSource cts)
    {
        SearchResponse? theirs = null;
        string? nasError = null;
        try { theirs = await nasTask; }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            nasError = e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? "NAS 账号或密码不对" : $"连不上 NAS {settings.Address}";
        }
        if (cts.IsCancellationRequested) return;  // a newer query already ran
        if (theirs is not null)
        {
            found = mine.Results.Concat(theirs.Results)
                .OrderBy(h => h.Name.ToLowerInvariant(), StringComparer.Ordinal)  // both lists come in name order
                .ToArray();
            Render(Results.SelectedItem as Hit);  // the selected row stays selected as the NAS's rows slot in
        }
        SetStatus($"{mine.Total + (theirs?.Total ?? 0):N0} 个结果" + (nasError is null ? "" : "，" + nasError), error: nasError is not null);
    }

    // Empty box: the pins. Otherwise the search results with pinned ones first.
    // `keep` stays selected when it is still listed, e.g. after pinning it.
    void Render(Hit? keep = null)
    {
        var pinsView = Query.Text.Trim() == "";
        var hits = (pinsView ? pins.Items : pins.PinnedFirst(found))
            .Select(h => h with { Pinned = pins.Contains(h), Host = settings.Server?.Host })
            .ToArray();
        var old = Results.SelectedIndex;
        Results.Tag = pinsView ? "pins" : null;
        Results.ItemsSource = hits;
        if (hits.Length > 0)
        {
            var i = Array.FindIndex(hits, h => h.FullPath == keep?.FullPath);
            Select(i >= 0 ? i : Math.Clamp(old, 0, hits.Length - 1));
        }
        ResultsPanel.Visibility = hits.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pinsView) SetStatus(hits.Length > 0 ? $"{hits.Length} 个置顶" : "");
    }

    void TogglePin(Hit h)
    {
        try
        {
            pins.Toggle(h);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus("置顶没保存：" + e.Message, error: true);
            return;
        }
        Render(keep: h);
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Hit h }) TogglePin(h);
    }

    void Select(int i)
    {
        Results.SelectedIndex = i;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    void SetStatus(string text, bool error = false)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Negative" : "Muted");  // follows a theme switch
    }

    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FilterPopup.IsOpen)  // typing in the format search: keep Enter and the arrows away from the results
        {
            if (e.Key == Key.Escape)
            {
                FilterPopup.IsOpen = false;
                e.Handled = true;
            }
            return;
        }
        var n = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                Query.Clear();  // back to the pins; the bar stays on the desktop
                break;
            case Key.Down when n > 0:
                Select((Results.SelectedIndex + 1) % n);
                break;
            case Key.Up when n > 0:
                Select((Results.SelectedIndex + n - 1) % n);
                break;
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control && Results.SelectedItem is Hit pick:
                TogglePin(pick);
                break;
            case Key.Enter:
                e.Handled = true;
                if (debounce.IsEnabled) await Search();  // open what was typed, not the previous query's hit
                if (Results.SelectedItem is Hit h) Open(h, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                return;
            default:
                return;
        }
        e.Handled = true;
    }

    void Results_Click(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null and not ListBoxItem; d = VisualTreeHelper.GetParent(d))
            if (d is Button) return;  // the pin button handles its own click
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Hit h })
            Open(h, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    // Right-click a row: open its folder, like Ctrl+Enter (left-click opens the file)
    void Results_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: Hit h })
            return;
        Select(Results.Items.IndexOf(h));
        Open(h, folder: true);
        e.Handled = true;
    }

    void Open(Hit h, bool folder)
    {
        var path = h.OpenPath;  // the same path the row shows
        try
        {
            // through explorer, which hands it to the desktop's own Explorer: the app it starts (Word, Excel,
            // Photos...) runs as the normal user, not with this app's admin rights
            Launch("explorer.exe", folder ? $"/select,\"{path}\"" : $"\"{path}\"");
        }
        catch (Win32Exception e)
        {
            SetStatus(e.Message, error: true);
        }
    }

    static void Launch(string file, string args) => Process.Start(new ProcessStartInfo(file, args))?.Dispose();

    // the size the tray uses at this scale (16 at 100%, 20 at 125%...), picked from app.ico
    static System.Drawing.Icon TrayIcon()
    {
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
