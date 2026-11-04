using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NasSearchBar;

// Follows Windows' light or dark app theme, also when it changes while the app runs: the brushes in App.xaml are
// swapped, and every DynamicResource that names them repaints (XAML brushes are frozen, so they cannot just be
// recolored, and a StaticResource would keep the old one). Each window's frame is told the theme too.
public static class Theme
{
    // graphite ramp; the only color is the accent, plus red for errors
    static readonly (string Key, string Light, string Dark)[] Ramp =
    [
        ("Surface", "#FFFFFF", "#19191B"),
        ("Hover", "#FAFAFA", "#1F1F21"),
        ("Raised", "#F4F4F5", "#232325"),
        ("Hairline", "#14171717", "#14EDEDED"),
        ("InputLine", "#29171717", "#29EDEDED"),
        ("Primary", "#171717", "#EDEDED"),
        ("PrimaryHover", "#333333", "#D4D4D8"),  // the primary button's fill under the mouse
        ("OnPrimary", "#FFFFFF", "#141415"),  // text and ticks on a Primary fill
        ("Secondary", "#525252", "#A1A1A6"),
        ("Muted", "#737373", "#85858B"),
        ("Accent", "#2563EB", "#60A5FA"),
        ("Negative", "#DC2626", "#E5484D"),
    ];

    static bool dark;

    public static void Follow(Application app)
    {
        Apply(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) app.Dispatcher.BeginInvoke(() => Apply(app));
        };
    }

    static void Apply(Application app)
    {
        dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;
        foreach (var (key, light, darkColor) in Ramp)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
            b.Freeze();
            app.Resources[key] = b;
        }
        foreach (Window w in app.Windows) Frame(w);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Dark title bar and frame on Windows 10 1809+ and 11 (DWMWA_USE_IMMERSIVE_DARK_MODE); ignored elsewhere.
    // Call once the window has a handle, e.g. from SourceInitialized.
    public static void Frame(Window w) => Frame(new WindowInteropHelper(w).Handle);

    public static void Frame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}
