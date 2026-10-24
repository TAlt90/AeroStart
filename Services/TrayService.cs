using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AeroStart.Services;

public sealed class TrayService : IDisposable
{
    private const int Callback = 0x8001;
    private readonly HwndSource _source;
    private readonly Action _open, _settings, _toggle, _exit;
    private readonly Action? _keyboardDiagnostic;
    private readonly Func<bool> _enabled;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;
    public string? Warning { get; private set; }

    public TrayService(Action open, Action settings, Action toggle, Action exit, Func<bool> enabled, Action? keyboardDiagnostic = null)
    {
        (_open, _settings, _toggle, _exit, _enabled) = (open, settings, toggle, exit, enabled);
        _keyboardDiagnostic = keyboardDiagnostic;
        _source = new HwndSource(new HwndSourceParameters("AeroStart.Tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = CreateTrayIcon();
        AddIcon();
    }

    public void Refresh()
    {
        if (_disposed) return;
        var data = Data();
        data.Tip = _enabled() ? "AeroStart – aktiv" : "AeroStart – deaktiviert (Windows-Standard)";
        ShellNotifyIcon(1, ref data);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = 1,
        Flags = 1 | 2 | 4, CallbackMessage = Callback, Icon = _icon, Tip = "AeroStart",
        Info = string.Empty, InfoTitle = string.Empty
    };

    private void AddIcon()
    {
        var data = Data();
        _added = ShellNotifyIcon(0, ref data);
        if (!_added) Warning = "Das Tray-Icon konnte nicht angelegt werden. Einstellungen bleiben geöffnet.";
        else Refresh();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated || message is 0x02E0 or 0x007E or 0x001A)
        {
            var icon = CreateTrayIcon();
            var old = _icon;
            _icon = icon;
            if ((uint)message == _taskbarCreated) AddIcon(); else Refresh();
            if (old != IntPtr.Zero) DestroyIcon(old);
            return IntPtr.Zero;
        }
        if (message == Callback)
        {
            if (lParam.ToInt32() == 0x0203) _settings();
            if (lParam.ToInt32() is 0x0205 or 0x007B) ShowContextMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, 1, "Startmenü öffnen");
            AppendMenu(menu, 0, 2, "Einstellungen");
            AppendMenu(menu, 0, 3, _enabled() ? "AeroStart deaktivieren" : "AeroStart aktivieren");
            if (_keyboardDiagnostic is not null) AppendMenu(menu, 0, 5, "Windows-Tasten-Diagnose (temporär)");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, "Beenden");
            GetCursorPos(out var point);
            SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            switch (command) { case 1: _open(); break; case 2: _settings(); break; case 3: _toggle(); break; case 4: _exit(); break; case 5: _keyboardDiagnostic?.Invoke(); break; }
        }
        finally { DestroyMenu(menu); }
    }

    private IntPtr CreateTrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/AeroStart;component/Assets/AeroStart.ico"))?.Stream
            ?? throw new FileNotFoundException("Eingebettetes AeroStart-Icon fehlt.");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) throw new InvalidDataException("Ungültige ICO-Ressource.");
        var count = reader.ReadUInt16();
        var dpi = GetDpiForWindow(_source.Handle);
        var target = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi);
        var bestDistance = int.MaxValue;
        uint length = 0, offset = 0;
        var selectedSize = 0;
        for (var i = 0; i < count; i++)
        {
            var size = reader.ReadByte();
            var dimension = size == 0 ? 256 : size;
            reader.ReadBytes(7);
            var frameLength = reader.ReadUInt32();
            var frameOffset = reader.ReadUInt32();
            var distance = Math.Abs(dimension - target);
            if (distance < bestDistance || distance == bestDistance && dimension > selectedSize)
            { bestDistance = distance; length = frameLength; offset = frameOffset; selectedSize = dimension; }
        }
        stream.Position = offset;
        var bytes = reader.ReadBytes(checked((int)length));
        var handle = CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, target, target, 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added) { var data = Data(); ShellNotifyIcon(2, ref data); }
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
