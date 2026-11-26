using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NasSearchBar;

public partial class SettingsWindow : Window
{
    readonly Func<KeyGesture, bool> trySetHotkey;
    readonly Func<Task<Version?>> update;
    Version? downloaded;  // once known, the update button restarts into it
    static readonly string Current = $"版本 {Updater.Current.ToString(3)}";
    (string Address, string Pin)? tested;  // the address the last 测试连接 reached, and the certificate it saw there

    public Settings Result { get; private set; }

    // Errors in red, progress and results in grey
    void Say(string text, bool error = true)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Negative" : "Secondary");
    }

    // trySetHotkey registers the global hotkey and reports whether Windows accepted it;
    // update puts a newer release in place and returns its version, or null when this one is the latest
    public SettingsWindow(Settings s, Func<KeyGesture, bool> trySetHotkey, Func<Task<Version?>> update)
    {
        InitializeComponent();
        this.trySetHotkey = trySetHotkey;
        this.update = update;
        Result = s;
        HotkeyInput.Text = s.Hotkey;
        AddressInput.Text = s.Address;
        UserInput.Text = s.User;
        PassInput.Password = Secret.Unprotect(s.ProtectedPassword);
        AutoStartBox.IsChecked = AutoStart.Enabled;
        SearchNasBox.IsChecked = s.SearchNas;
        SearchLocalBox.IsChecked = s.SearchLocal;
        VersionText.Text = Current;
        Loaded += (_, _) => AddressInput.Focus();
        SourceInitialized += (_, _) => Theme.Frame(this);
    }

    Settings Read() => Result with  // keeps what this window does not edit, like the format filter
    {
        Address = AddressInput.Text.Trim(),
        // 测试连接 trusts the certificate it sees; another address is another NAS, whose certificate is not known yet
        CertPin = AddressInput.Text.Trim() is var a && tested?.Address == a ? tested.Value.Pin
            : a == Result.Address ? Result.CertPin : "",
        User = UserInput.Text.Trim(),
        ProtectedPassword = Secret.Protect(PassInput.Password),
        Hotkey = HotkeyInput.Text,
        SearchNas = SearchNasBox.IsChecked == true,
        SearchLocal = SearchLocalBox.IsChecked == true,
    };

    // Returns an error to show, or null when the settings are usable; no address = this PC only
    static string? Validate(Settings s) =>
        s.Address != "" && s.Server is null ? "地址不对"
        : !s.SearchNas && !s.SearchLocal ? "NAS 和本机硬盘至少勾一个"
        : !s.SearchLocal && s.Server is null ? "只搜 NAS 要先填地址"
        : null;

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (Validate(s) is { } error)
        {
            Say(error);
            return;
        }
        if (Hotkeys.Parse(s.Hotkey) is not { } hotkey)
        {
            Say("快捷键不对，点输入框重新按一次");
            return;
        }
        if (!trySetHotkey(hotkey))
        {
            Say($"{s.Hotkey} 被别的程序占用了，换一个");
            return;
        }
        try
        {
            s.Save(Settings.FilePath);
            AutoStart.Enabled = AutoStartBox.IsChecked == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Say("保存失败：" + ex.Message);
            return;
        }
        Result = s;
        DialogResult = true;
    }

    // Records the pressed combination instead of typing it
    void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;  // Alt combinations arrive as Key.System
        var mods = Keyboard.Modifiers;
        if (key == Key.Tab && mods == ModifierKeys.None) return;  // Tab still moves focus
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed) return;  // wait for the main key
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            Say("快捷键要带 Ctrl、Alt 或 Win");  // a bare key would swallow normal typing
            return;
        }
        try
        {
            HotkeyInput.Text = Hotkeys.Format(new KeyGesture(key, mods));
            Say("", error: false);
        }
        catch (NotSupportedException)
        {
            Say("这个键不能当快捷键");
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (downloaded is not null)
        {
            MainWindow.RestartToUpdate();
            return;
        }
        UpdateButton.IsEnabled = false;
        VersionText.Text = Current + "，正在检查更新…";
        try
        {
            downloaded = await update();
            VersionText.Text = downloaded is null ? Current + "，已是最新" : $"新版本 {downloaded.ToString(3)} 已下载";
            if (downloaded is not null) UpdateButton.Content = "重启以更新";
        }
        catch (Exception ex) when (Updater.Failed(ex))
        {
            VersionText.Text = Current + "，检查更新失败：" + ex.Message;
        }
        UpdateButton.IsEnabled = true;
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (s.Server is null)
        {
            Say(Validate(s) ?? "地址空着 = 只搜本机硬盘，不用测试", error: Validate(s) is not null);
            return;
        }
        TestButton.IsEnabled = false;
        Say("正在连接…", error: false);
        try
        {
            var nas = new NasClient(s.Server!, s.User, PassInput.Password);  // no pin: a test trusts what it sees
            var r = await nas.SearchAsync("", CancellationToken.None);
            tested = (s.Address, nas.Pin);
            Say($"可用，{r.Indexed:N0} 个文件，{r.IndexNote(DateTime.Now)}", error: false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Say("账号或密码不对");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Say("连不上");
        }
        TestButton.IsEnabled = true;
    }
}
