using System.Diagnostics;
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
    private readonly bool[] _diagnosticDown = new bool[256];
    private static readonly Queue<WinKeyDiagnostic> _windowsKeyDebugLog = new();
    private WinKeyDiagnostic? _pendingWinDiagnostic, _completedWinDiagnostic;
    private long _lastHookTimestamp;
    private readonly int _hookThread = Environment.CurrentManagedThreadId;
    public static WinKeyDiagnostic[] WindowsKeyDebugLog => _windowsKeyDebugLog.ToArray();

    public sealed class WinKeyDiagnostic
    {
        public DateTimeOffset KeyDown { get; internal set; }
        public DateTimeOffset? KeyUp { get; internal set; }
        public KeyboardGesture DownDecision { get; internal set; }
        public KeyboardGesture UpDecision { get; internal set; }
        public double? DownHookGapMs { get; internal set; }
        public double? UpHookGapMs { get; internal set; }
        public double? DownToUpMs { get; internal set; }
        public uint? DownEventAgeMs { get; internal set; }
        public uint? UpEventAgeMs { get; internal set; }
        public double? MenuQueueDelayMs { get; internal set; }
        public uint? SendInputResult { get; internal set; }
        public uint? CleanupSendInputResult { get; internal set; }
        public bool MenuCallbackExecuted { get; internal set; }
        public DateTimeOffset? MenuDispatchTime { get; internal set; }
        internal long DownTimestamp;
        internal long QueueTimestamp;
    }

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
        ResetWinDiagnostic();
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
                    ObserveWinDiagnostic(key, message.ToInt32() is 0x0100 or 0x0104,
                        (input.Flags & 0x12) != 0, gesture);
                    RecordNativeWinTime(key, message.ToInt32() is 0x0100 or 0x0104, input.Time);
                    if (gesture == KeyboardGesture.WindowsReleased)
                    {
                        if (MaskWindowsRelease(input))
                        { QueueMenu(_completedWinDiagnostic); return new IntPtr(1); }
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

    private void QueueMenu(WinKeyDiagnostic? diagnostic = null)
    {
        var generation = _generation;
        RecordMenuQueued(diagnostic);
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var execute = !_disposed && _hook != IntPtr.Zero && generation == _generation;
            RecordMenuDispatch(diagnostic, execute);
            if (execute) _released();
        }));
    }

    private bool MaskWindowsRelease(KeyboardData physical)
    {
        // VK_E8 is unassigned. It marks the held Win key as used before its replacement release.
        var inputs = new[] { KeyInput(0xE8, 0, 0), KeyInput(0xE8, 0, 2),
            KeyInput((ushort)physical.Key, (ushort)physical.Scan, 2 | (physical.Flags & 1)) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        var error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
        RecordSendInput(sent);
        if (sent == inputs.Length) return true;
        if (sent == 1)
        {
            var cleanup = SendInput(1, new[] { KeyInput(0xE8, 0, 2) }, Marshal.SizeOf<NativeInput>());
            RecordSendInput(cleanup, cleanup: true);
        }
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
        _pendingWinDiagnostic = null;
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (_registered) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; }
    }

    [Conditional("RELEASE")]
    private void ResetWinDiagnostic()
    {
        _pendingWinDiagnostic = _completedWinDiagnostic = null;
        _lastHookTimestamp = 0;
        for (var key = 0; key < _diagnosticDown.Length; key++)
            _diagnosticDown[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    [Conditional("RELEASE")]
    private void ObserveWinDiagnostic(int key, bool pressed, bool injected, KeyboardGesture decision)
    {
        var now = Stopwatch.GetTimestamp();
        double? gap = _lastHookTimestamp == 0 ? null : Stopwatch.GetElapsedTime(_lastHookTimestamp, now).TotalMilliseconds;
        _lastHookTimestamp = now;
        _completedWinDiagnostic = null;
        if (injected) { _pendingWinDiagnostic = null; return; }
        var repeat = pressed && _diagnosticDown[key];
        _diagnosticDown[key] = pressed;
        if (repeat) return;
        if (pressed)
        {
            _pendingWinDiagnostic = null;
            if (key is 0x5B or 0x5C && !_diagnosticDown.Where((down, index) => index != key).Any(down => down))
                _pendingWinDiagnostic = new WinKeyDiagnostic
                {
                    KeyDown = DateTimeOffset.Now, DownTimestamp = now,
                    DownDecision = decision, DownHookGapMs = gap
                };
        }
        else if (key is 0x5B or 0x5C && _pendingWinDiagnostic is { } entry)
        {
            entry.KeyUp = DateTimeOffset.Now;
            entry.UpDecision = decision;
            entry.UpHookGapMs = gap;
            entry.DownToUpMs = Stopwatch.GetElapsedTime(entry.DownTimestamp, now).TotalMilliseconds;
            _pendingWinDiagnostic = null;
            _completedWinDiagnostic = entry;
            if (_windowsKeyDebugLog.Count == 30) _windowsKeyDebugLog.Dequeue();
            _windowsKeyDebugLog.Enqueue(entry);
        }
    }

    [Conditional("RELEASE")]
    private void RecordNativeWinTime(int key, bool pressed, uint nativeTime)
    {
        if (key is not (0x5B or 0x5C)) return;
        var age = unchecked((uint)Environment.TickCount - nativeTime);
        if (pressed && _pendingWinDiagnostic is { } pending) pending.DownEventAgeMs ??= age;
        else if (!pressed && _completedWinDiagnostic is { } completed) completed.UpEventAgeMs = age;
    }

    [Conditional("RELEASE")]
    private static void RecordMenuQueued(WinKeyDiagnostic? entry)
    {
        if (entry is not null) entry.QueueTimestamp = Stopwatch.GetTimestamp();
    }

    [Conditional("RELEASE")]
    private void RecordSendInput(uint sent, bool cleanup = false)
    {
        if (_completedWinDiagnostic is not { } entry) return;
        if (cleanup) entry.CleanupSendInputResult = sent;
        else entry.SendInputResult = sent;
    }

    [Conditional("RELEASE")]
    private static void RecordMenuDispatch(WinKeyDiagnostic? entry, bool execute)
    {
        if (entry is null) return;
        entry.MenuDispatchTime = DateTimeOffset.Now;
        if (entry.QueueTimestamp != 0)
            entry.MenuQueueDelayMs = Stopwatch.GetElapsedTime(entry.QueueTimestamp).TotalMilliseconds;
        entry.MenuCallbackExecuted = execute;
    }

    public string GetWindowsKeyDiagnosticText()
    {
        _dispatcher.VerifyAccess();
        var header = $"Temporäre Windows-Tasten-Diagnose – {DateTimeOffset.Now:O}\n"
            + $"Prozess: {Environment.ProcessId}; Debugger: {Debugger.IsAttached}; Thread: {_hookThread}; "
            + $"Hook-Handle gespeichert: {_hook != IntPtr.Zero} (kein Funktionsnachweis); Generation: {_generation}\n"
            + $"Status: {Status}\nNur Einzel-Win; höchstens 30 abgeschlossene Einträge; keine Datei.\n"
            + "EventAge = Alter des nativen Ereignisses am Hook; QueueDelay = Wartezeit bis Menücallback.\n"
            + "MenuCallbackExecuted bestätigt den Aufruf, nicht die sichtbare Menüöffnung.\n"
            + $"Offener Einzel-Win-Kandidat: {_pendingWinDiagnostic?.KeyDown.ToString("O") ?? "keiner"}\n\n";
        return header + System.Text.Json.JsonSerializer.Serialize(WindowsKeyDebugLog,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
