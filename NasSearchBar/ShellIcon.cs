using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NasSearchBar;

// The icon Explorer shows for a Hit's file type. Looked up by extension only, so the NAS is never
// touched; .exe/.lnk get their type's generic icon, not their own.
public sealed class ShellIconConverter : IValueConverter
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Hit h ? For(h.Size is null ? null : System.IO.Path.GetExtension(h.Name)) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    static ImageSource? For(string? ext)
    {
        var key = ext ?? "\\";  // no extension can be "\", so it stands for folders
        if (!Cache.TryGetValue(key, out var img)) Cache[key] = img = Load(ext);
        return img;
    }

    static ImageSource? Load(string? ext)
    {
        var info = new SHFILEINFO();
        var attr = ext is null ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if (SHGetFileInfo(ext is null ? "folder" : "x" + ext, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES) == 0)
            return null;

        // the 48px list, so the icon stays sharp up to 150% scaling; 32px is too small past 100%
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");  // IImageList
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) != 0) return null;
        try
        {
            var icon = ImageList_GetIcon(list, info.iIcon, ILD_TRANSPARENT);
            if (icon == 0) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(icon); }
        }
        finally { Marshal.Release(list); }
    }

    const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    const int SHIL_EXTRALARGE = 2;
    const uint ILD_TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int list, ref Guid iid, out nint imageList);

    [DllImport("comctl32.dll")]
    static extern nint ImageList_GetIcon(nint imageList, int index, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint icon);
}
