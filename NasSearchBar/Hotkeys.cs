using System.Windows.Input;

namespace NasSearchBar;

// "Ctrl+Alt+Space" <-> KeyGesture, using WPF's own converter
static class Hotkeys
{
    static readonly KeyGestureConverter Converter = new();

    public static KeyGesture? Parse(string text)
    {
        try
        {
            return (KeyGesture?)Converter.ConvertFromInvariantString(text);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static string Format(KeyGesture g) => Converter.ConvertToInvariantString(g) ?? "";
}
