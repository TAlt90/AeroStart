using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;

namespace AeroStart.Services;

internal sealed class TaskbarDiagnostics
{
    internal static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroStart", "Diagnostics", "taskbar.jsonl");
    private bool _busy;
    private DateTimeOffset _lastAutomatic;
    private Task<object[]>? _uiaPending;
    private readonly string _logPath;
    private readonly Func<IntPtr, object> _readUia;
    private readonly TimeSpan _uiaTimeout;
    private readonly object _fileLock = new();
    internal TaskbarDiagnostics(string? logPath = null, Func<IntPtr, object>? readUia = null, TimeSpan? uiaTimeout = null)
    {
        _logPath = logPath ?? LogPath;
        _readUia = readUia ?? ReadUia;
        _uiaTimeout = uiaTimeout ?? TimeSpan.FromSeconds(2);
    }
    public string? LastError { get; private set; }
    internal void Abort() => _busy = false;
    internal bool TryBegin(bool manual)
    {
        if (_busy || !manual && DateTimeOffset.UtcNow - _lastAutomatic < TimeSpan.FromSeconds(5)) return false;
        _busy = true;
        if (!manual) _lastAutomatic = DateTimeOffset.UtcNow;
        return true;
    }

    internal static bool HitMismatch(TaskbarTarget target, TaskbarButtonWindow overlay)
    {
        if (!TaskbarDiscovery.Native.IsWindowVisible(overlay.Handle)) return false;
        var b = target.ButtonBounds;
        foreach (var point in new[] { new Point(b.Left + b.Width / 2, b.Top + b.Height / 2),
            new Point(b.Left + 1, b.Top + 1), new Point(b.Right - 2, b.Bottom - 2),
            new Point(b.Right - 2, b.Top + 1), new Point(b.Left + 1, b.Bottom - 2) })
        {
            var hit = WindowFromPoint(new NativePoint((int)point.X, (int)point.Y));
            if (hit != overlay.Handle && GetAncestor(hit, 2) != overlay.Handle) return true;
        }
        return false;
    }

    internal static object Capture(TaskbarTarget target, TaskbarButtonWindow overlay, string phase, string cause, bool valid, bool fullscreen)
    {
        try { return CaptureCore(target, overlay, phase, cause, valid, fullscreen); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OverflowException)
        { return new { CapturedUtc = DateTimeOffset.UtcNow, Phase = phase, Cause = cause, Overlay = Hex(overlay.Handle), CaptureError = ex.Message }; }
    }

    private static object CaptureCore(TaskbarTarget target, TaskbarButtonWindow overlay, string phase, string cause, bool valid, bool fullscreen)
    {
        var captured = DateTimeOffset.UtcNow;
        var monitor = TaskbarDiscovery.Native.MonitorFromWindow(target.Window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        var monitorRead = GetMonitorInfo(monitor, ref info);
        var bounds = TaskbarDiscovery.Native.GetWindowRect(overlay.Handle, out var actual) ? actual.ToRect() : target.ButtonBounds;
        var center = new NativePoint((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        var hit = WindowFromPoint(center);
        var hitRoot = GetAncestor(hit, 2);
        var windows = new List<object>();
        var above = true;
        var overlayIndex = -1;
        var index = 0;
        var visited = new HashSet<IntPtr>();
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero && index < 1024 && visited.Add(window); window = GetWindow(window, 2), index++)
        {
            if (window == overlay.Handle) { overlayIndex = index; above = false; }
            if (window == overlay.Handle || window == target.Window || window == hitRoot ||
                above && TaskbarDiscovery.Native.IsWindowVisible(window) && TaskbarDiscovery.Native.GetWindowRect(window, out var rectangle) && rectangle.ToRect().IntersectsWith(bounds))
                windows.Add(new { ZIndex = index, AboveOverlayInEnumeration = above && window != overlay.Handle, Window = Describe(window) });
        }
        var childPoint = center;
        var converted = hitRoot != IntPtr.Zero && ScreenToClient(hitRoot, ref childPoint);
        var child = converted ? ChildWindowFromPointEx(hitRoot, childPoint, 7) : IntPtr.Zero;
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var guiRead = GetGUIThreadInfo(0, ref gui);
        return new
        {
            CapturedUtc = captured, Phase = phase, Cause = cause,
            Monitor = new { Handle = Hex(monitor), Read = monitorRead, info.Device, Bounds = monitorRead ? Box(info.Monitor.ToRect()) : null, Work = monitorRead ? Box(info.Work.ToRect()) : null },
            Overlay = Describe(overlay.Handle), OverlayZIndex = overlayIndex, OverlayWpfVisible = overlay.IsVisible,
            Taskbar = Describe(target.Window), TaskbarValidAgainstCachedTarget = valid,
            GlobalAppbarState = GetAppbarState(target.Window),
            CachedTaskbarBounds = Box(target.TaskbarBounds), CachedUiaStartBounds = Box(target.ButtonBounds),
            FullscreenDetectedByExistingService = fullscreen,
            Center = new { center.X, center.Y }, WindowFromPoint = Describe(hit), HitRoot = Describe(hitRoot),
            ChildWindowFromPointExSkippingInvisibleDisabledTransparent = Describe(child),
            Foreground = Describe(GetForegroundWindow()), GuiThreadInfoAvailable = guiRead,
            ForegroundThreadCapture = Hex(gui.Capture), ForegroundThreadActive = Hex(gui.Active),
            LastPlacementUtc = overlay.LastPlacementUtc, overlay.LastPlacementReason,
            PositionEvidence = target.Position, target.VisualOffsetX, overlay.LastVisualPlacement,
            LastZOrderUtc = overlay.LastZOrderUtc, overlay.LastZOrderReason,
            overlay.LastMouseMessageUtc, overlay.LastMouseMessage,
            OverlappingAboveCandidatesAndRelevantWindows = windows,
            ZEnumerationLimit = 1024,
            Interpretation = "Nicht atomarer Snapshot. Z-Order/BoundingRects zeigen Kandidaten, keine sichere visuelle Deckung. WindowFromPoint/ChildWindowFromPointEx beweisen keinen Klickempfaenger: Kindfenster, Regionen, layered alpha, HTTRANSPARENT, Capture und zeitliche Rennen beachten. Keine fremden WM_NCHITTEST-Nachrichten gesendet. Nur LastMouseMessage belegt Empfang einer Nachricht am eigenen HWND; fehlt sie, ist ein fremder Empfaenger nicht bewiesen."
        };
    }

    internal static (object Snapshot, IntPtr[] Taskbars) Inventory()
    {
        var taskbars = new List<IntPtr>();
        var windows = new List<object>();
        var index = 0;
        TaskbarDiscovery.Native.EnumWindows((window, _) =>
        {
            var name = new StringBuilder(256);
            TaskbarDiscovery.Native.GetClassName(window, name, name.Capacity);
            TaskbarDiscovery.Native.GetWindowThreadProcessId(window, out var process);
            var taskbar = name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            if (taskbar) taskbars.Add(window);
            if (taskbar || process == (uint)Environment.ProcessId)
            {
                var monitor = TaskbarDiscovery.Native.MonitorFromWindow(window, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
                var read = GetMonitorInfo(monitor, ref info);
                windows.Add(new { ZIndex = index, Window = Describe(window), Monitor = Hex(monitor), MonitorDevice = read ? info.Device : null });
            }
            index++;
            return true;
        }, IntPtr.Zero);
        return (new { Phase = "independent-native-inventory", CapturedUtc = DateTimeOffset.UtcNow, Windows = windows,
            Note = "Unabhaengig vom Overlay-Dictionary; eigene Fenster anderer AeroStart-Prozesse sind ueber Taskleisten-Nachbarn nicht vollstaendig erfasst. Kein UIA-Aufruf." }, taskbars.ToArray());
    }

    private void Append(object record)
    {
        lock (_fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= 2 * 1024 * 1024)
                File.Move(_logPath, _logPath + ".previous", true);
            File.AppendAllText(_logPath, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
        }
    }

    internal async Task<string?> WriteFailureAsync(object snapshot, string recordType = "integration-failure")
    {
        try
        {
            await Task.Run(() => Append(new { Schema = 2, RecordType = recordType, CaptureId = Guid.NewGuid(),
                ProcessId = Environment.ProcessId, WrittenUtc = DateTimeOffset.UtcNow, Snapshot = snapshot }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        { Debug.WriteLine("Taskbar-Fehlerdiagnose: " + ex.Message); return ex.Message; }
    }

    internal async Task WriteAsync(object[] snapshots, IntPtr[] taskbars)
    {
        var id = Guid.NewGuid();
        try
        {
            await Task.Run(() => Append(new { Schema = 2, RecordType = "native", CaptureId = id, ProcessId = Environment.ProcessId,
                WrittenUtc = DateTimeOffset.UtcNow, Snapshots = snapshots, FreshUiaStatus = "pending-separate-record" }));
            var state = "completed";
            object[] uia = Array.Empty<object>();
            if (_uiaPending is { IsCompleted: false }) state = "skipped-previous-query-still-running";
            else if (taskbars.Length != 0)
            {
                _uiaPending = Task.Run(() => taskbars.Select(window =>
                {
                    try { return _readUia(window); }
                    catch (Exception ex) { return (object)new { Taskbar = Hex(window), Error = ex.Message }; }
                }).ToArray());
                if (await Task.WhenAny(_uiaPending, Task.Delay(_uiaTimeout)) == _uiaPending) uia = await _uiaPending;
                else state = "timeout-query-not-cancelled";
            }
            await Task.Run(() => Append(new { Schema = 2, RecordType = "uia-result", CaptureId = id,
                WrittenUtc = DateTimeOffset.UtcNow, Status = state, FreshUia = uia }));
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        { LastError = ex.Message; Debug.WriteLine("Taskbar-Diagnose: " + ex.Message); }
        finally { _busy = false; }
    }

    private static object ReadUia(IntPtr taskbar)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var buttons = root.FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"), new PropertyCondition(AutomationElement.ClassNameProperty, "Start"))));
            var matches = new List<object>();
            for (var i = 0; i < buttons.Count; i++)
            {
                var current = buttons[i].Current;
                matches.Add(new { current.AutomationId, current.ClassName, current.NativeWindowHandle, current.IsOffscreen, Bounds = Box(current.BoundingRectangle) });
            }
            var rawTree = buttons.Count == 0 ? TaskbarDiscovery.CaptureRawTree(root) : null;
            var measurement = TaskbarDiscovery.Measure(taskbar);
            var selection = rawTree != null && measurement != null
                ? TaskbarDiscovery.SelectRawStart(rawTree, measurement.Bounds, measurement.MonitorBounds) : null;
            return new { Taskbar = Hex(taskbar), StartedUtc = started, CompletedUtc = DateTimeOffset.UtcNow, Matches = matches,
                Source = rawTree == null ? "Filtered" : "RawView", ZeroMatchRawTree = rawTree, RawSelection = selection,
                Note = "Spaetere UIA-Messung, nicht zeitgleich mit nativem Snapshot. RawSelection ist Diagnose, keine Service-Annahme." };
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return new { Taskbar = Hex(taskbar), StartedUtc = started, CompletedUtc = DateTimeOffset.UtcNow, Error = ex.Message }; }
    }

    private static object Describe(IntPtr window)
    {
        var name = new StringBuilder(256);
        TaskbarDiscovery.Native.GetClassName(window, name, name.Capacity);
        var thread = TaskbarDiscovery.Native.GetWindowThreadProcessId(window, out var process);
        string? processName = null;
        try { using var p = Process.GetProcessById(checked((int)process)); processName = p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { }
        var style = Style(window, -20);
        var hasRect = TaskbarDiscovery.Native.GetWindowRect(window, out var rect);
        var cloakResult = DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int));
        var layerRead = GetLayeredWindowAttributes(window, out var colorKey, out var alpha, out var layerFlags);
        return new { Hwnd = Hex(window), Class = name.ToString(), ProcessId = process, ProcessName = processName, ThreadId = thread,
            Parent = Hex(GetAncestor(window, 1)), Root = Hex(GetAncestor(window, 2)), Owner = Hex(GetWindow(window, 4)),
            PreviousAbove = Hex(GetWindow(window, 3)), NextBelow = Hex(GetWindow(window, 2)),
            Bounds = hasRect ? Box(rect.ToRect()) : null, Visible = TaskbarDiscovery.Native.IsWindowVisible(window), Enabled = IsWindowEnabled(window),
            Iconic = IsIconic(window), ExStyle = $"0x{style:X}", Topmost = (style & 8) != 0, TransparentStyle = (style & 0x20) != 0,
            Layered = (style & 0x80000) != 0, LayerAttributesAvailable = layerRead, LayerAlpha = alpha, LayerColorKey = colorKey, LayerFlags = layerFlags,
            Cloaked = cloakResult == 0 ? (int?)cloaked : null, Dpi = GetDpiForWindow(window) };
    }

    private static object? Box(Rect rect) => rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)
        ? null : new { rect.X, rect.Y, rect.Width, rect.Height };
    private static string Hex(IntPtr value) => $"0x{value.ToInt64():X}";
    private static object GetAppbarState(IntPtr taskbar)
    {
        var data = new AppbarData { Size = (uint)Marshal.SizeOf<AppbarData>(), Window = taskbar };
        var state = SHAppBarMessage(4, ref data).ToUInt64();
        return new { Raw = state, AutoHide = (state & 1) != 0, AlwaysOnTop = (state & 2) != 0,
            Scope = "ABM_GETSTATE liefert globalen Shellzustand, keinen verifizierten Auto-Hide-Zustand jeder sekundären Taskleiste." };
    }
    private static long Style(IntPtr window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr(window, index).ToInt64() : GetWindowLong(window, index);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public TaskbarDiscovery.Native.NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public TaskbarDiscovery.Native.NativeRect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct AppbarData
    { public uint Size; public IntPtr Window; public uint Callback, Edge; public TaskbarDiscovery.Native.NativeRect Rect; public IntPtr Parameter; }
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppbarData data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr window, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
