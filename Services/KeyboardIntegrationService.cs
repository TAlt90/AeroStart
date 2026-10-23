using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AeroStart.Services;

public sealed class KeyboardIntegrationService : IDisposable
{
    private const int HotkeyId = 0xA310;
    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private readonly Action _released;
    private readonly HookCallback _callback;
    private readonly KeyboardGestureState _gestures = new();
    private static readonly UIntPtr InjectionMarker = new(0x41535457u);
    private IntPtr _hook;
    private bool _registered;
    private bool _experimental;
    private bool _disposed;
    private int _generation;
    public string Status { get; private set; } = "Tastaturintegration deaktiviert; Windows-Taste unverändert.";
    public event EventHandler? StatusChanged;

    public KeyboardIntegrationService(Action released)
    {
        _released = released;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = ObserveKeyboard;
        _source = new HwndSource(new HwndSourceParameters("AeroStart.Keyboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WindowProc);
    }

    public void Configure(bool enabled, bool experimental = false)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        if (enabled && _hook != IntPtr.Zero && _experimental == experimental) return;
        Stop();
        _experimental = experimental;
        if (!enabled) { SetStatus("Tastaturintegration deaktiviert; Windows-Taste unverändert."); return; }
        _registered = RegisterHotKey(_source.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x20);
        if (!_registered && !experimental)
        {
            SetStatus("Strg + Alt + Leertaste ist belegt. Bitte das Tray-Menü verwenden.");
            return;
        }
        _gestures.Reset(key => (GetAsyncKeyState(key) & 0x8000) != 0);
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            SetStatus($"Release-Hotkey nicht verfügbar (Win32 {error}); Registrierung freigegeben. Bitte Tray verwenden.");
            return;
        }
        SetStatus((experimental ? "Experimentell: alleinige Windows-Taste öffnet beim Loslassen AeroStart. " : "Stabil: Windows-Taste unverändert. ")
            + (_registered ? "Strg + Alt + Leertaste aktiv (Release)." : "Strg + Alt + Leertaste ist belegt; Tray bleibt verfügbar."));
    }

    private IntPtr ObserveKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !_disposed && _hook != IntPtr.Zero)
        {
            try
            {
                var input = Marshal.PtrToStructure<KeyboardData>(data);
                if (input.Extra == InjectionMarker)
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                var key = (int)input.Key;
                if (key is >= 0 and < 256 && message.ToInt32() is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    var gesture = _gestures.Process(key, message.ToInt32() is 0x0100 or 0x0104,
                        (input.Flags & 0x12) != 0, _experimental);
                    if (gesture == KeyboardGesture.WindowsReleased)
                    {
                        if (MaskWindowsRelease(input))
                        { QueueMenu(); return new IntPtr(1); }
                    }
                    else if (gesture == KeyboardGesture.HotkeyReleased && _registered)
                    {
                        QueueMenu();
                    }
                }
            }
            catch (Exception)
            {
                Stop();
                _dispatcher.BeginInvoke(new Action(() => SetStatus("Tastaturfehler: Hook und Hotkey freigegeben. Bitte Tray verwenden.")));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void QueueMenu()
    {
        var generation = _generation;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_disposed && _hook != IntPtr.Zero && generation == _generation) _released();
        }));
    }

    private bool MaskWindowsRelease(KeyboardData physical)
    {
        // VK_E8 is unassigned. It marks the held Win key as used before its replacement release.
        var inputs = new[] { KeyInput(0xE8, 0, 0), KeyInput(0xE8, 0, 2),
            KeyInput((ushort)physical.Key, (ushort)physical.Scan, 2 | (physical.Flags & 1)) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent == inputs.Length) return true;
        var error = Marshal.GetLastWin32Error();
        if (sent == 1)
            SendInput(1, new[] { KeyInput(0xE8, 0, 2) }, Marshal.SizeOf<NativeInput>());
        Stop();
        _dispatcher.BeginInvoke(new Action(() => SetStatus($"Experimentelle Umleitung abgeschaltet: SendInput {sent}/3 (Win32 {error}, möglicherweise UIPI). Windows-Standard aktiv; Tray bleibt verfügbar.")));
        return false;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Registration reserves the chord; only the observed Space key-up invokes the menu.
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId) handled = true;
        if (message == 0x02B1) _gestures.Cancel();
        return IntPtr.Zero;
    }

    private void Stop()
    {
        _generation++;
        _gestures.Reset();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_registered) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; }
    }

    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(this, EventArgs.Empty); }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    private static NativeInput KeyInput(ushort key, ushort scan, uint flags) => new()
    { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Scan = scan, Flags = flags, Extra = InjectionMarker } } };
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
