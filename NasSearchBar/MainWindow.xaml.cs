using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
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
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73));
    static readonly Brush Negative = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly System.Windows.Forms.NotifyIcon tray;
    CancellationTokenSource? inflight;
    Settings settings = new();
    NasClient? client;
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

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    public MainWindow()
    {
        InitializeComponent();
        debounce.Tick += async (_, _) => await Search();
        Deactivated += (_, _) => Hide();
        LoadSettings();
        hwnd = new WindowInteropHelper(this).EnsureHandle();  // the hotkey needs a window handle before the first show
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        var round = DWMWCP_ROUND;  // Windows 11 round corners; Windows 10 ignores it (square corners)
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        tray = new() { Icon = TrayIcon(), Text = "NAS 搜索", Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowBar(); };
        tray.ContextMenuStrip = new();
        tray.ContextMenuStrip.Items.Add("设置", null, (_, _) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        Closed += (_, _) => tray.Dispose();
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
        client = settings.Server is { } server
            ? new NasClient(server, settings.User, Secret.Unprotect(settings.ProtectedPassword))
            : null;
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
            if (IsVisible) Hide(); else ShowBar();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ShowBar()
    {
        Render();
        Opacity = 0;  // hidden while it moves to the right screen
        Show();
        PlaceOnCursorScreen();
        Opacity = 1;
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
        if (client is null)
        {
            found = [];
            Render();
            SetStatus("先在设置里填 NAS 地址", error: true);
            return;
        }
        try
        {
            var data = await client.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;  // a newer query already ran
            found = data.Results;
            Render();
            SetStatus($"{data.Total:N0} 个结果");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            found = [];
            Render();
            SetStatus(e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? "账号或密码不对" : "连不上 NAS", error: true);
        }
    }

    // Empty box: the pins. Otherwise the search results with pinned ones first.
    // `keep` stays selected when it is still listed, e.g. after pinning it.
    void Render(Hit? keep = null)
    {
        var pinsView = Query.Text.Trim() == "";
        var drives = DriveMap();
        var hits = (pinsView ? pins.Items : pins.PinnedFirst(found))
            .Select(h => h with { Pinned = pins.Contains(h), Drive = h.DriveIn(drives) })
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
        Status.Foreground = error ? Negative : Muted;
    }

    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var n = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
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

    void Open(Hit h, bool folder)
    {
        var path = h.OpenPath(DriveMap());  // the same path the row shows
        try
        {
            if (folder) Launch("explorer.exe", $"/select,\"{path}\"");
            else Launch(path);  // default app: Word, Excel, Photos...
            Hide();
        }
        catch (Win32Exception e)
        {
            SetStatus(e.Message, error: true);
        }
    }

    Dictionary<string, string> DriveMap() => settings.UseDriveLetters ? MappedShares() : [];

    // share name -> "Z:" for every mapped network drive, read fresh so new mappings count at once.
    // shortcut: matches by share name only (\\DS920\docs and \\192.168.1.10\docs are the same); fine for one NAS.
    static Dictionary<string, string> MappedShares()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network))
        {
            var drive = d.Name.TrimEnd('\\');
            var remote = new StringBuilder(260);
            var length = remote.Capacity;
            if (WNetGetConnection(drive, remote, ref length) != 0) continue;
            var parts = remote.ToString().TrimStart('\\').TrimEnd('\\').Split('\\');
            if (parts.Length == 2) map.TryAdd(parts[1], drive);  // drives mapped to a subfolder are skipped
        }
        return map;
    }

    static void Launch(string file, string? args = null) =>
        Process.Start(args is null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(file, args))?.Dispose();

    // the size the tray uses at this scale (16 at 100%, 20 at 125%...), picked from app.ico
    static System.Drawing.Icon TrayIcon()
    {
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
