using AeroStart.BackdropHost;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AeroStart.Services;

internal sealed class MenuAcrylicService : IDisposable
{
    private readonly Window _menu;
    private readonly FrameworkElement _frame;
    private readonly TranslateTransform _translation;
    private readonly AcrylicWindow _background;
    private readonly nint _handle;
    private readonly nint _previousOwner;
    private bool _disposed, _syncing;
    private bool? _active;
    private int _regionWidth, _regionHeight, _regionRadius, _regionOffset;

    internal MenuAcrylicService(Window menu, FrameworkElement frame, TranslateTransform translation)
    {
        _menu = menu; _frame = frame; _translation = translation;
        _handle = new WindowInteropHelper(menu).EnsureHandle();
        _background = AcrylicHost.Create();
        _previousOwner = GetWindow(_handle, 4);
        SetWindowLongPtr(_handle, -8, _background.Handle);
        _menu.IsVisibleChanged += VisibilityChanged;
        CompositionTarget.Rendering += Rendering;
    }

    internal void Sync(int opacity)
    {
        if (_disposed || _syncing) return;
        _syncing = true;
        try
        {
            bool visible = _menu.IsVisible;
            bool active = visible && GetForegroundWindow() == _handle;
            if (_active != active) { _active = active; _background.SetActive(active); }
            if (!visible) { ShowWindow(_background.Handle, 0); return; }
            if (!_background.IsReady) return;
            Check(GetWindowRect(_handle, out var bounds));
            double scale = GetDpiForWindow(_handle) / 96.0;
            int offset = (int)Math.Round(Math.Max(0, _translation.Y) * scale);
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            byte alpha = AnimationAlpha(_frame.Opacity);
            Check(SetLayeredWindowAttributes(_background.Handle, 0, alpha, 2));
            if (height <= offset || alpha == 0) { ShowWindow(_background.Handle, 0); return; }
            int radius = Math.Max(1, (int)Math.Round(16 * scale));
            if (_regionWidth != width || _regionHeight != height || _regionRadius != radius || _regionOffset != offset)
            {
                nint region = CreateRoundRectRgn(0, offset, width + 1, height + 1, radius, radius);
                if (region == 0) throw new Win32Exception();
                if (SetWindowRgn(_background.Handle, region, true) == 0)
                {
                    DeleteObject(region);
                    throw new Win32Exception();
                }
                _regionWidth = width; _regionHeight = height; _regionRadius = radius;
                _regionOffset = offset;
            }
            Check(SetWindowPos(_background.Handle, _handle, bounds.Left, bounds.Top, width, height, 0x0250));
        }
        finally { _syncing = false; }
    }

    internal int Opacity { get; set; } = 100;
    internal static byte AnimationAlpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
    private void Rendering(object? sender, EventArgs e) => Sync(Opacity);
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Sync(Opacity);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= Rendering;
        _menu.IsVisibleChanged -= VisibilityChanged;
        ShowWindow(_background.Handle, 0);
        SetWindowLongPtr(_handle, -8, _previousOwner);
        _background.Dispose();
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint obj);
}
