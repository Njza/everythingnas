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

    public Settings Result { get; private set; }

    // Errors in red, progress and results in grey
    void Say(string text, bool error = true)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Negative" : "Secondary");
    }

    // trySetHotkey registers the global hotkey and reports whether Windows accepted it
    public SettingsWindow(Settings s, Func<KeyGesture, bool> trySetHotkey)
    {
        InitializeComponent();
        this.trySetHotkey = trySetHotkey;
        Result = s;
        HotkeyInput.Text = s.Hotkey;
        AddressInput.Text = s.Address;
        UserInput.Text = s.User;
        PassInput.Password = Secret.Unprotect(s.ProtectedPassword);
        AutoStartBox.IsChecked = AutoStart.Enabled;
        VersionText.Text = $"版本 {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        Loaded += (_, _) => AddressInput.Focus();
        SourceInitialized += (_, _) => Theme.Frame(this);
    }

    Settings Read() => Result with  // keeps what this window does not edit, like the format filter
    {
        Address = AddressInput.Text.Trim(),
        User = UserInput.Text.Trim(),
        ProtectedPassword = Secret.Protect(PassInput.Password),
        Hotkey = HotkeyInput.Text,
    };

    // Returns an error to show, or null when the address is usable; empty = this PC only
    static string? Validate(Settings s) => s.Address != "" && s.Server is null ? "地址不对" : null;

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
            var r = await new NasClient(s.Server!, s.User, PassInput.Password).SearchAsync("", CancellationToken.None);
            Say($"可用，{r.Indexed:N0} 个文件", error: false);
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
