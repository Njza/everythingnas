using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace NasSearchBar;

public partial class SettingsWindow : Window
{
    readonly Func<KeyGesture, bool> trySetHotkey;

    public Settings Result { get; private set; }

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
        UseDrives.IsChecked = s.UseDriveLetters;
        Loaded += (_, _) => AddressInput.Focus();
    }

    Settings Read() => new()
    {
        Address = AddressInput.Text.Trim(),
        User = UserInput.Text.Trim(),
        ProtectedPassword = Secret.Protect(PassInput.Password),
        UseDriveLetters = UseDrives.IsChecked == true,
        Hotkey = HotkeyInput.Text,
    };

    // Returns an error to show, or null when the address is usable
    static string? Validate(Settings s) =>
        s.Address == "" ? "请填 NAS 地址" : s.Server is null ? "地址不对" : null;

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (Validate(s) is { } error)
        {
            Status.Text = error;
            return;
        }
        if (Hotkeys.Parse(s.Hotkey) is not { } hotkey)
        {
            Status.Text = "快捷键不对，点输入框重新按一次";
            return;
        }
        if (!trySetHotkey(hotkey))
        {
            Status.Text = $"{s.Hotkey} 被别的程序占用了，换一个";
            return;
        }
        try
        {
            s.Save(Settings.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "保存失败：" + ex.Message;
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
            Status.Text = "快捷键要带 Ctrl、Alt 或 Win";  // a bare key would swallow normal typing
            return;
        }
        try
        {
            HotkeyInput.Text = Hotkeys.Format(new KeyGesture(key, mods));
            Status.Text = "";
        }
        catch (NotSupportedException)
        {
            Status.Text = "这个键不能当快捷键";
        }
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = Read();
        if (Validate(s) is { } error)
        {
            Status.Text = error;
            return;
        }
        TestButton.IsEnabled = false;
        Status.Text = "正在连接…";
        try
        {
            var r = await new NasClient(s.Server!, s.User, PassInput.Password).SearchAsync("", CancellationToken.None);
            Status.Text = $"可用，{r.Indexed:N0} 个文件";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Status.Text = "账号或密码不对";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status.Text = "连不上";
        }
        TestButton.IsEnabled = true;
    }
}
