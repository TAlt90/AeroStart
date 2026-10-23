using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace AeroStart.Services;

internal sealed class TaskbarWindowEvents : IDisposable
{
    private readonly Action<bool, string> _changed;
    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private bool _disposed;

    public TaskbarWindowEvents(Action<bool, string> changed)
    {
        _changed = changed;
        _callback = OnEvent;
        _source = new HwndSource(new HwndSourceParameters("AeroStart.TaskbarEvents")
        { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WindowProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        try
        {
            Hook(0x0003, 0x0003); // Foreground
            Hook(0x000A, 0x000B); // Move/size start/end
            Hook(0x0016, 0x0017); // Minimize start/end
            Hook(0x8000, 0x8004); // Create/destroy/show/hide/reorder
            Hook(0x800B, 0x800B); // Location change
        }
        catch { Dispose(); throw; }
    }

    private void Hook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, IntPtr.Zero, _callback, 0, 0, 2);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _hooks.Add(hook);
    }

    private void OnEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || window == IntPtr.Zero) return;
        var root = GetAncestor(window, 2);
        var name = new StringBuilder(128);
        GetClassName(root == IntPtr.Zero ? window : root, name, name.Capacity);
        var taskbar = name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        var reason = $"WinEvent=0x{eventId:X4}, HWND=0x{window.ToInt64():X}, root=0x{root.ToInt64():X}, class={name}, object={objectId}, child={childId}, eventTick={time}";
        if (taskbar) { _changed(true, reason); return; }
        if (eventId < 0x8000 || objectId == 0 && childId == 0)
            _changed(false, reason);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated || message is 0x007E or 0x001A or 0x02E0 or 0x0219)
            _changed(true, $"ShellMessage=0x{message:X4}, wParam=0x{wParam.ToInt64():X}, lParam=0x{lParam.ToInt64():X}");
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
