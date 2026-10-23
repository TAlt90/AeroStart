using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace AeroStart.Services;
public sealed class ShellIconService : IDisposable
{
    private const int CacheCapacity = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<ImageSource>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly BlockingCollection<Action> _requests = new();
    private bool _disposed;

    public static ShellIconService Instance { get; } = new();
    public static ImageSource Fallback { get; } = CreateFallback();

    private ShellIconService()
    {
        var worker = new Thread(ProcessRequests) { IsBackground = true, Name = "AeroStart Shell icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public Task<ImageSource> GetAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(Fallback);
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(Fallback);
            if (_cache.TryGetValue(path, out var cached)) return cached;
            var completion = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_cache.Count >= CacheCapacity) _cache.Remove(_cacheOrder.Dequeue());
            _cache.Add(path, completion.Task);
            _cacheOrder.Enqueue(path);
            _requests.Add(() => completion.SetResult(Load(path)));
            return completion.Task;
        }
    }

    private void ProcessRequests()
    {
        var initialized = OleInitialize(IntPtr.Zero) >= 0;
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable()) request();
        }
        finally
        {
            if (initialized) OleUninitialize();
            _requests.Dispose();
        }
    }

    private static ImageSource Load(string path)
    {
        var info = new ShellFileInfo();
        try
        {
            if (path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)) return LoadShellItem(path);
            if (Path.GetExtension(path).Equals(".msc", StringComparison.OrdinalIgnoreCase))
            {
                var consoleIcon = LoadConsoleIcon(path);
                if (!ReferenceEquals(consoleIcon, Fallback)) return consoleIcon;
            }
            if (Path.GetExtension(path).Equals(".cpl", StringComparison.OrdinalIgnoreCase))
            {
                var controlPanelIcon = LoadResourceIcon(path, 0);
                if (!ReferenceEquals(controlPanelIcon, Fallback)) return controlPanelIcon;
            }
            // SHGFI_ICON | SHGFI_LARGEICON: the shell also resolves shortcut-specific icons.
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return Fallback;
            return FromIcon(info.Icon);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException or IOException or SecurityException or UnauthorizedAccessException)
        {
            return Fallback;
        }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
        }
    }

    private static ImageSource LoadConsoleIcon(string path)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader);
            var icon = document.Root?.Element("VisualAttributes")?.Element("Icon");
            var binaries = document.Root?.Element("BinaryStorage")?.Elements("Binary").ToArray() ?? Array.Empty<XElement>();
            foreach (var size in new[] { "Large48x", "Large", "Small" })
            {
                var image = icon?.Elements("Image").FirstOrDefault(x => (string?)x.Attribute("Name") == size);
                if (int.TryParse((string?)image?.Attribute("BinaryRefIndex"), out var index) && index >= 0 && index < binaries.Length)
                {
                    var embedded = LoadImageListIcon(binaries[index].Value);
                    if (!ReferenceEquals(embedded, Fallback)) return embedded;
                }
            }
            var resource = (string?)icon?.Attribute("File");
            if (!string.IsNullOrWhiteSpace(resource) && int.TryParse((string?)icon?.Attribute("Index"), out var resourceIndex))
            {
                resource = Environment.ExpandEnvironmentVariables(resource);
                if (!Path.IsPathRooted(resource)) resource = Path.Combine(Path.GetDirectoryName(path)!, resource);
                return LoadResourceIcon(resource, resourceIndex);
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or FormatException or ArgumentException or ExternalException or InvalidOperationException or SecurityException or UnauthorizedAccessException)
        {
        }
        return Fallback;
    }

    private static ImageSource LoadImageListIcon(string encoded)
    {
        var stream = IntPtr.Zero;
        var images = IntPtr.Zero;
        var icon = IntPtr.Zero;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return Fallback;
            stream = SHCreateMemStream(bytes, (uint)bytes.Length);
            if (stream == IntPtr.Zero) return Fallback;
            images = ImageList_Read(stream);
            if (images == IntPtr.Zero) return Fallback;
            icon = ImageList_GetIcon(images, 0, 1);
            return icon == IntPtr.Zero ? Fallback : FromIcon(icon);
        }
        catch (Exception ex) when (ex is FormatException or ExternalException or ArgumentException or InvalidOperationException)
        {
            return Fallback;
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (images != IntPtr.Zero) ImageList_Destroy(images);
            if (stream != IntPtr.Zero) Marshal.Release(stream);
        }
    }

    private static ImageSource LoadResourceIcon(string path, int index)
    {
        var large = IntPtr.Zero;
        var small = IntPtr.Zero;
        try
        {
            if (ExtractIconEx(path, index, out large, out small, 1) == 0) return Fallback;
            var icon = large != IntPtr.Zero ? large : small;
            return icon == IntPtr.Zero ? Fallback : FromIcon(icon);
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero && small != large) DestroyIcon(small);
        }
    }

    private static ImageSource LoadShellItem(string path)
    {
        var pidl = IntPtr.Zero;
        var info = new ShellFileInfo();
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) < 0 || pidl == IntPtr.Zero) return Fallback;
            if (SHGetFileInfoFromIdList(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x108) == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return Fallback;
            return FromIcon(info.Icon);
        }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static ImageSource FromIcon(IntPtr icon)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    private static ImageSource CreateFallback()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(128, 136, 144)), null, new Rect(1, 2, 30, 28), 3, 3);
            context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(5, 8, 22, 18));
            context.DrawRectangle(Brushes.DimGray, null, new Rect(8, 12, 6, 6));
            context.DrawRectangle(Brushes.Silver, null, new Rect(17, 12, 7, 2));
            context.DrawRectangle(Brushes.Silver, null, new Rect(17, 17, 7, 2));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _cacheOrder.Clear();
            _requests.CompleteAdding();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoFromIdList(IntPtr pidl, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributes, out uint foundAttributes);
    [DllImport("shlwapi.dll")] private static extern IntPtr SHCreateMemStream(byte[] data, uint size);
    [DllImport("comctl32.dll")] private static extern IntPtr ImageList_Read(IntPtr stream);
    [DllImport("comctl32.dll")] private static extern IntPtr ImageList_GetIcon(IntPtr images, int index, uint flags);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImageList_Destroy(IntPtr images);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
