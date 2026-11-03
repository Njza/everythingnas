using System.IO;
using System.Windows;

namespace NasSearchBar;

public partial class App : Application
{
    static Mutex? single;  // held for the app's lifetime: one instance per Windows session

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\NasSearchBar", out var first);
        if (!first)
        {
            MessageBox.Show("NAS 搜索已经在运行，按快捷键或点右下角图标打开", "NAS 搜索");
            Shutdown();
            return;
        }

        var bar = new MainWindow();
        var hotkeyOk = bar.ApplySavedHotkey();
        if (!hotkeyOk)
            MessageBox.Show($"{bar.SavedHotkey} 被别的程序占用了，请在设置里换一个", "NAS 搜索");
        if (!hotkeyOk || !File.Exists(Settings.FilePath)) bar.OpenSettings();  // first run: ask for the NAS address
        bar.ShowBar();
    }
}
