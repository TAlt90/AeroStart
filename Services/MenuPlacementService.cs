using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace AeroStart.Services;

public static class MenuPlacementService
{
    private enum Edge { Left, Top, Right, Bottom }

    public static bool IsOnSameMonitor(Window window, Point screenPoint) =>
        MonitorFromWindow(new WindowInteropHelper(window).EnsureHandle(), 2)
        == MonitorFromPoint(new NativePoint { X = (int)screenPoint.X, Y = (int)screenPoint.Y }, 2);

    public static Point CursorPosition
    {
        get
        {
            if (!GetCursorPos(out var point)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new Point(point.X, point.Y);
        }
    }

    // All inputs and native rectangles use physical screen pixels, including negative monitor coordinates.
    public static Size GetWorkAreaSize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        return new Size((info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    // The HWND is moved first so WPF receives WM_DPICHANGED before final placement.
    public static void Position(Window window, Point screenPoint, Rect? startButtonBounds = null)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = (int)screenPoint.X, Y = (int)screenPoint.Y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var work = info.Work.ToRect();
        var bounds = info.Monitor.ToRect();
        var taskbar = FindTaskbar(monitor);
        var edge = GetEdge(bounds, taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var bar) ? null : bar.ToRect(), work);
        var anchor = startButtonBounds ?? FindStartButton(taskbar);
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var currentMonitor = MonitorFromWindow(hwnd, 2);
        if (currentMonitor != monitor)
            Move(hwnd, (int)work.Left, (int)work.Top);
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        var width = window.Width * scale;
        var height = window.Height * scale;
        var gap = 8 * scale;
        var left = anchor?.Left ?? work.Left + gap;
        var top = work.Bottom - height - gap;
        switch (edge)
        {
            case Edge.Top:
                top = Math.Max(work.Top, anchor?.Bottom ?? work.Top) + gap;
                break;
            case Edge.Left:
                left = Math.Max(work.Left, anchor?.Right ?? work.Left) + gap;
                top = anchor?.Top ?? work.Top + gap;
                break;
            case Edge.Right:
                left = Math.Min(work.Right, anchor?.Left ?? work.Right) - width - gap;
                top = anchor?.Top ?? work.Top + gap;
                break;
            default:
                top = Math.Min(work.Bottom, anchor?.Top ?? work.Bottom) - height - gap;
                break;
        }
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        Move(hwnd, (int)Math.Round(left), (int)Math.Round(top));
    }

    private static Rect? FindStartButton(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return null;
        try
        {
            var button = AutomationElement.FromHandle(taskbar)?.FindFirst(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
                                new PropertyCondition(AutomationElement.ClassNameProperty, "Start")));
            var rectangle = button?.Current.BoundingRectangle;
            return rectangle is { IsEmpty: false, Width: > 0, Height: > 0 } ? rectangle : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IntPtr FindTaskbar(IntPtr monitor)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && MonitorFromWindow(hwnd, 2) == monitor)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static Edge GetEdge(Rect monitor, Rect? taskbar, Rect work)
    {
        if (taskbar is { } bar)
        {
            if (bar.Width > bar.Height)
                return Math.Abs(bar.Top - monitor.Top) < Math.Abs(bar.Bottom - monitor.Bottom) ? Edge.Top : Edge.Bottom;
            return Math.Abs(bar.Left - monitor.Left) < Math.Abs(bar.Right - monitor.Right) ? Edge.Left : Edge.Right;
        }
        var gaps = new[] { work.Left - monitor.Left, work.Top - monitor.Top, monitor.Right - work.Right, monitor.Bottom - work.Bottom };
        var largest = gaps.Max();
        return largest > 0 ? (Edge)Array.IndexOf(gaps, largest) : Edge.Bottom;
    }

    private static void Move(IntPtr hwnd, int x, int y)
    {
        if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
}
