using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

internal sealed class AcrylicBackdrop : IDisposable
{
    private readonly object _queue;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private DesktopAcrylicController? _controller;
    private readonly Microsoft.UI.WindowId _windowId;
    private readonly SystemBackdropConfiguration _configuration;
    private bool _attached;

    internal AcrylicBackdrop(IntPtr hwnd)
    {
        var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _queue));
        if (!DesktopAcrylicController.IsSupported()) throw new NotSupportedException("DesktopAcrylicController.IsSupported=false");
        var topLevel = GetAncestor(hwnd, 2);
        if (topLevel == IntPtr.Zero || topLevel != hwnd)
            throw new InvalidOperationException("Das Hintergrund-HWND muss ein Top-Level-Fenster sein.");
        var useHostBackdropBrush = 1;
        var hostBackdropResult = DwmSetWindowAttribute(topLevel, 17, ref useHostBackdropBrush, sizeof(int));
        Console.WriteLine($"DWMWA_USE_HOSTBACKDROPBRUSH: HWND=0x{topLevel.ToInt64():X}; HRESULT=0x{hostBackdropResult:X8}");
        Marshal.ThrowExceptionForHR(hostBackdropResult);
        _windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(topLevel);
        _compositor = new Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget(hwnd, false, out var pointer);
        try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        _target.Root = _compositor.CreateContainerVisual();
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
    }

    internal string Set(bool enabled)
    {
        try
        {
            if (_controller is not null)
            {
                Console.WriteLine($"Controller before disposal: State={_controller.State}");
                _controller.Dispose();
                _controller = null;
            }
            _attached = false;
            if (enabled)
            {
                _controller = new DesktopAcrylicController
                {
                    TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 32), TintOpacity = 0.15f,
                    LuminosityOpacity = 0.15f, FallbackColor = Windows.UI.Color.FromArgb(255, 32, 32, 32)
                };
                _controller.SetSystemBackdropConfiguration(_configuration);
                _controller.StateChanged += (sender, _) => Console.WriteLine($"Controller StateChanged: State={sender.State}");
                _attached = _controller.SetTarget(_windowId, _target);
                Console.WriteLine($"SetTarget(WindowId={_windowId.Value}) returned={_attached}; State={_controller.State}");
            }
            return $"SetTarget attached={_attached}; State={_controller?.State.ToString() ?? "disposed (Aus)"}; input active={_configuration.IsInputActive}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SetTarget prototype exception: HRESULT=0x{ex.HResult:X8}; {ex}");
            return $"SetTarget fehlgeschlagen: {ex.GetType().Name}, HRESULT=0x{ex.HResult:X8}";
        }
    }

    internal void Activate(bool active) => _configuration.IsInputActive = active;

    public void Dispose()
    {
        _controller?.Dispose();
        _target.Dispose();
        _compositor.Dispose();
        GC.KeepAlive(_queue);
    }

    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }
}
