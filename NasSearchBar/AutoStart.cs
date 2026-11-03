using Microsoft.Win32;

namespace NasSearchBar;

// Start with Windows via the current user's Run key; the key itself is the setting, so turning
// it off in Task Manager's Startup apps or anywhere else is never out of sync with a saved copy
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "NasSearch";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
