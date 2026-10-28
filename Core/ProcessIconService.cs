using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HttpFreeKit.Core;

public static class ProcessIconService
{
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint ShgsiIcon = 0x100;
    private const int StockIconApplication = 2;
    private const int StockIconServices = 56;
    private static readonly ConcurrentDictionary<string, Lazy<ImageSource>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ImageSource FallbackIcon = CreateFallbackIcon();
    public static ImageSource PlaceholderIcon => FallbackIcon;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetStockIconInfo(int iconId, uint flags, ref StockIconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource GetIcon(string processName, string? processPath)
    {
        string cacheKey = !string.IsNullOrWhiteSpace(processPath)
            ? processPath
            : processName.Equals("System", StringComparison.OrdinalIgnoreCase)
                ? "stock:services"
                : $"extension:{Path.GetExtension(processName)}";

        return Cache.GetOrAdd(cacheKey, _ => new Lazy<ImageSource>(
            () => LoadIcon(processName, processPath) ?? FallbackIcon,
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ImageSource? LoadIcon(string processName, string? processPath)
    {
        try
        {
            if (string.Equals(processName, "System", StringComparison.OrdinalIgnoreCase))
                return GetStockIcon(StockIconServices) ?? GetStockIcon(StockIconApplication);

            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var icon = GetFileIcon(processPath, useFileAttributes: false);
                if (icon != null) return icon;
            }

            string extension = Path.GetExtension(
                string.IsNullOrWhiteSpace(processPath) ? processName : processPath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".exe";

            return GetFileIcon($"HttpFreeKit-placeholder{extension}", useFileAttributes: true)
                   ?? GetStockIcon(StockIconApplication);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to load icon for {processName}: {ex.Message}");
            return null;
        }
    }

    private static ImageSource? GetFileIcon(string path, bool useFileAttributes)
    {
        var info = new ShellFileInfo();
        uint flags = ShgfiIcon;
        if (useFileAttributes) flags |= ShgfiUseFileAttributes;
        _ = SHGetFileInfo(path, useFileAttributes ? FileAttributeNormal : 0,
            ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        return ConvertAndDestroy(info.Icon);
    }

    private static ImageSource? GetStockIcon(int iconId)
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        try
        {
            SHGetStockIconInfo(iconId, ShgsiIcon, ref info);
            return ConvertAndDestroy(info.Icon);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to load Windows stock icon {iconId}: {ex.Message}");
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            return null;
        }
    }

    private static ImageSource? ConvertAndDestroy(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static ImageSource CreateFallbackIcon()
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new RectangleGeometry(new Rect(3, 3, 18, 18), 4, 4));
        geometry.Children.Add(new RectangleGeometry(new Rect(7, 8, 10, 2)));
        geometry.Children.Add(new RectangleGeometry(new Rect(7, 12, 7, 2)));
        var drawing = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x55, 0x60, 0x70)),
            null,
            geometry);
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
