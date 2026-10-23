using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AeroStart.Services;

public sealed class TaskbarIntegrationService : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _rescan;
    private TaskbarWindowEvents? _events;
    private readonly Action<Rect> _clicked;
    private readonly Dictionary<IntPtr, (TaskbarTarget Target, TaskbarButtonWindow Overlay)> _overlays = new();
    private bool _enabled, _disposed, _scanning, _scanAgain, _repairQueued;
    private int _generation;
    private string _lastCause = "Initialisierung";
    private readonly TaskbarDiagnostics _diagnostics = new();
    private DateTimeOffset? _scanStartedUtc;
    private string _scanProgress = "Noch kein Scan";
    private long _scanId;
    private IReadOnlyList<TaskbarDecision> _lastDecisions = Array.Empty<TaskbarDecision>();
    private object? _lastFailure;
    private string? _failureStatus;
    private string? _failureLogError;
    private string _operation = "Initialisierung";
    private MainWindow? _visualMenu;

    public void SetMenuState(MainWindow? menu)
    {
        _dispatcher.VerifyAccess();
        _visualMenu = menu;
        foreach (var entry in _overlays.Values) ApplyMenuState(entry.Target, entry.Overlay);
    }

    private void ApplyMenuState(TaskbarTarget target, TaskbarButtonWindow overlay)
    {
        overlay.SetMenuOpen(_visualMenu is { IsMenuOpen: true } menu && MenuPlacementService.IsOnSameMonitor(menu,
            new Point(target.ButtonBounds.Left + target.ButtonBounds.Width / 2, target.ButtonBounds.Top + target.ButtonBounds.Height / 2)));
    }

    private object ServiceSnapshot(string phase) => new
    {
        Phase = phase, CapturedUtc = DateTimeOffset.UtcNow, Enabled = _enabled, Scanning = _scanning,
        Generation = _generation, OverlayCount = _overlays.Count, Status, LastEvent = _lastCause,
        ScanId = _scanId, ScanStartedUtc = _scanStartedUtc,
        ScanElapsedMs = _scanning && _scanStartedUtc is { } start ? (double?)(DateTimeOffset.UtcNow - start).TotalMilliseconds : null,
        ScanProgress = Volatile.Read(ref _scanProgress), DiscoveryDecisions = _lastDecisions,
        LastFailure = _lastFailure, FailureLogError = _failureLogError, Operation = _operation
    };

    private async Task WatchScanAsync(long id)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (!_disposed && _enabled && _scanning && _scanId == id)
            await RecordServiceAsync(false, "scan-pending-over-five-seconds");
    }

    private async Task RecordServiceAsync(bool manual, string phase)
    {
        if (!_diagnostics.TryBegin(manual))
        {
            if (manual) SetStatus("Eine Taskleisten-Diagnose läuft bereits; bitte nach wenigen Sekunden erneut auslösen.");
            return;
        }
        try
        {
            var inventory = TaskbarDiagnostics.Inventory();
            var covered = FullScreenMonitors();
            var snapshots = new List<object> { ServiceSnapshot(phase), inventory.Snapshot };
            snapshots.AddRange(_overlays.Values.Select(entry => TaskbarDiagnostics.Capture(entry.Target, entry.Overlay,
                phase, _lastCause, StillValid(entry.Target), covered.Contains(TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2)))));
            await _diagnostics.WriteAsync(snapshots.ToArray(), inventory.Taskbars);
            if (manual && !_disposed) SetStatus(_diagnostics.LastError is { } error ? "Taskleisten-Diagnose fehlgeschlagen: " + error
                : "Taskleisten-Diagnose gespeichert: " + TaskbarDiagnostics.LogPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            _diagnostics.Abort();
            if (manual) SetStatus("Taskleisten-Diagnose fehlgeschlagen: " + ex.Message);
        }
    }
    public string Status { get; private set; } = "Startbutton-Overlays deaktiviert.";
    public event EventHandler? StatusChanged;

    public TaskbarIntegrationService(Action<Rect> clicked)
    {
        _clicked = clicked;
        _rescan = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _rescan.Tick += Rescan;
    }

    public void Configure(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _enabled == enabled) return;
        if (enabled)
        {
            _failureStatus = null;
            _failureLogError = null;
        }
        _enabled = enabled;
        _generation++;
        _events?.Dispose();
        _events = null;
        _rescan.Stop();
        _scanAgain = false;
        ClearOverlays();
        if (!enabled)
        {
            SetStatus("Startbutton-Overlays deaktiviert; native Windows-Buttons unverändert.");
            return;
        }
        SetStatus("Taskleisten und Startbuttons werden gesucht …");
        try
        {
            _operation = "Install TaskbarWindowEvents";
            _events = new TaskbarWindowEvents(Changed);
            Scan();
        }
        catch (System.ComponentModel.Win32Exception ex) { Fail(ex); }
    }

    private void Changed(bool layout, string cause)
    {
        if (!_enabled || _disposed) return;
        _lastCause = cause;
        if (layout)
        {
            _scanAgain = true;
            if (!_rescan.IsEnabled) _rescan.Start();
        }
        if (_repairQueued) return;
        _repairQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            _repairQueued = false;
            if (_enabled && !_disposed) Repair();
        }));
    }

    private void Rescan(object? sender, EventArgs e)
    {
        _rescan.Stop();
        Scan();
    }

    private void Repair(string? trigger = null)
    {
        var cause = trigger ?? _lastCause;
        var diagnose = false;
        try
        {
            _operation = "Repair: FullScreenMonitors";
            var covered = FullScreenMonitors();
            diagnose = _overlays.Values.Any(entry => TaskbarDiagnostics.HitMismatch(entry.Target, entry.Overlay))
                && _diagnostics.TryBegin(false);
            var snapshots = new List<object>();
            var taskbars = _overlays.Keys.ToArray();
            if (diagnose)
                foreach (var entry in _overlays.Values)
                    snapshots.Add(TaskbarDiagnostics.Capture(entry.Target, entry.Overlay, "before-existing-repair", cause,
                        StillValid(entry.Target), covered.Contains(TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2))));
            foreach (var entry in _overlays.Values)
            {
                _operation = $"Repair taskbar=0x{entry.Target.Window.ToInt64():X}, overlay=0x{entry.Overlay.Handle.ToInt64():X}: validity/hide/place; cause={cause}";
                if (!StillValid(entry.Target))
                {
                    entry.Overlay.Hide();
                    _scanAgain = true;
                    if (!_rescan.IsEnabled) _rescan.Start();
                }
                else if (covered.Contains(TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2))) entry.Overlay.Hide();
                else if (!entry.Overlay.Place(entry.Target.ButtonBounds, entry.Target.Window, entry.Target.VisualOffsetX + 8,  cause))
                {
                    SetStatus("Overlay-Trefferfläche wird von einem anderen Fenster verdeckt; keine Klickweiterleitung an Windows.");
                    if (!diagnose) diagnose = _diagnostics.TryBegin(false);
                }
            }
            if (diagnose)
            {
                foreach (var entry in _overlays.Values)
                    snapshots.Add(TaskbarDiagnostics.Capture(entry.Target, entry.Overlay, "after-existing-repair", cause,
                        StillValid(entry.Target), covered.Contains(TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2))));
                var inventory = TaskbarDiagnostics.Inventory();
                snapshots.Add(ServiceSnapshot("automatic-service-state"));
                snapshots.Add(inventory.Snapshot);
                _ = _diagnostics.WriteAsync(snapshots.ToArray(), taskbars.Union(inventory.Taskbars).ToArray());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { if (diagnose) _diagnostics.Abort(); Fail(ex); }
    }

    private async void Scan()
    {
        if (!_enabled || _disposed) return;
        if (_scanning) { _scanAgain = true; return; }
        _scanAgain = false;
        _scanning = true;
        _scanStartedUtc = DateTimeOffset.UtcNow;
        var scanId = ++_scanId;
        Volatile.Write(ref _scanProgress, "Task.Run eingeplant");
        _ = WatchScanAsync(scanId);
        var generation = _generation;
        try
        {
            var result = await Task.Run(() => TaskbarDiscovery.Discover(stage => Volatile.Write(ref _scanProgress, stage)));
            if (_disposed || !_enabled || generation != _generation) return;
            _lastDecisions = result.Decisions;
            var present = result.Targets.Select(target => target.Window).ToHashSet();
            foreach (var obsolete in _overlays.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                _operation = $"Scan: remove obsolete taskbar=0x{obsolete.ToInt64():X}";
                var removed = _overlays[obsolete];
                var discoveryDecision = result.Decisions.FirstOrDefault(decision => decision.Hwnd == $"0x{obsolete.ToInt64():X}");
                var removalDetails = $"OverlayHwnd=0x{removed.Overlay.Handle.ToInt64():X}; CachedStartBounds={removed.Target.ButtonBounds}; "
                    + $"CachedTaskbarBounds={removed.Target.TaskbarBounds}; StillValid={StillValid(removed.Target)}; Cause={_lastCause}; "
                    + $"DiscoveryDecision={discoveryDecision?.ToString() ?? "none"}";
                _lastDecisions = _lastDecisions.Append(new TaskbarDecision($"0x{obsolete.ToInt64():X}",
                    "Existing overlay removed: taskbar absent from accepted discovery targets. " + removalDetails,
                    "Service removal", null, null, null, null)).ToArray();
                _overlays[obsolete].Overlay.Close(); _overlays.Remove(obsolete);
            }
            var positionChanges = new List<object>();
            var changedTaskbars = new HashSet<IntPtr>();
            foreach (var target in result.Targets)
            {
                _operation = $"Scan: validate/create taskbar=0x{target.Window.ToInt64():X}";
                if (!StillValid(target))
                {
                    _lastDecisions = _lastDecisions.Append(new TaskbarDecision($"0x{target.Window.ToInt64():X}",
                        "Rejected after discovery: PID/visibility/taskbar rectangle changed", "Service StillValid", null, null, null, null)).ToArray();
                    _scanAgain = true; continue;
                }
                var hadPrevious = _overlays.TryGetValue(target.Window, out var existing);
                var resolved = TaskbarDiscovery.ResolveVisualOffset(target, hadPrevious ? existing.Target : null);
                var changed = !hadPrevious || existing.Target.ButtonBounds != resolved.ButtonBounds
                    || existing.Target.TaskbarBounds != resolved.TaskbarBounds || existing.Target.VisualOffsetX != resolved.VisualOffsetX
                    || System.Text.Json.JsonSerializer.Serialize(existing.Target.Position) != System.Text.Json.JsonSerializer.Serialize(resolved.Position);
                if (changed)
                {
                    changedTaskbars.Add(target.Window);
                    positionChanges.Add(new { Taskbar = $"0x{target.Window.ToInt64():X}", Phase = "position-before-scan-application",
                        CapturedUtc = DateTimeOffset.UtcNow, Cause = _lastCause, ScanId = _scanId,
                        PreviousStartBounds = hadPrevious ? (Rect?)existing.Target.ButtonBounds : null,
                        PreviousOffsetPhysicalX = hadPrevious ? (double?)existing.Target.VisualOffsetX : null,
                        PreviousEvidence = hadPrevious ? existing.Target.Position : null,
                        PreviousPlacement = hadPrevious ? existing.Overlay.LastVisualPlacement : null,
                        CurrentStartBounds = resolved.ButtonBounds, CalculatedOffsetPhysicalX = target.VisualOffsetX,
                        AppliedOffsetPhysicalX = resolved.VisualOffsetX, CurrentEvidence = resolved.Position });
                }
                if (!hadPrevious)
                {
                    var window = target.Window;
                    var overlay = new TaskbarButtonWindow(() => Click(window));
                    existing = (target, overlay);
                }
                _overlays[target.Window] = (resolved, existing.Overlay);
                ApplyMenuState(resolved, existing.Overlay);
            }
            Repair("UIA-Scan abgeschlossen; letzter Auslöser: " + _lastCause);
            if (_disposed || !_enabled || generation != _generation) return;
            if (positionChanges.Count != 0)
            {
                foreach (var window in changedTaskbars)
                {
                    var entry = _overlays[window];
                    positionChanges.Add(TaskbarDiagnostics.Capture(entry.Target, entry.Overlay, "position-after-scan-application",
                        _lastCause, StillValid(entry.Target), FullScreenMonitors().Contains(TaskbarDiscovery.Native.MonitorFromWindow(window, 2))));
                }
                _ = _diagnostics.WriteFailureAsync(new { ScanId = _scanId, Snapshots = positionChanges.ToArray() }, "position-change");
            }
            SetStatus($"{_overlays.Count} AeroStart-Button(s), {result.Taskbars} Taskleiste(n). "
                + (result.Targets.Count < result.Taskbars ? "Nicht eindeutig erkennbare/ausgeblendete Startbuttons werden nicht überdeckt." : "Nur eigene Overlayfenster; Windows-Buttons unverändert."));
            if (_overlays.Count == 0) await RecordServiceAsync(false, "scan-completed-without-overlays");
            else if (_overlays.Count < result.Taskbars) await RecordServiceAsync(false, "scan-completed-with-missing-overlays");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or COMException)
        { if (!_disposed && generation == _generation) Fail(ex); }
        finally
        {
            _scanning = false;
            if (_scanAgain && _enabled && !_disposed && !_rescan.IsEnabled) _rescan.Start();
        }
    }

    private void Click(IntPtr taskbar)
    {
        if (!_enabled || _disposed || !_overlays.TryGetValue(taskbar, out var entry) || !StillValid(entry.Target)
            || FullScreenMonitors().Contains(TaskbarDiscovery.Native.MonitorFromWindow(taskbar, 2))) return;
        _clicked(entry.Target.ButtonBounds);
    }

    public async void Diagnose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        await RecordServiceAsync(true, "manual-no-repair");
    }

    private static bool StillValid(TaskbarTarget target)
    {
        TaskbarDiscovery.Native.GetWindowThreadProcessId(target.Window, out var process);
        return process == target.ProcessId && TaskbarDiscovery.Native.IsWindowVisible(target.Window)
            && TaskbarDiscovery.Native.GetWindowRect(target.Window, out var current) && current.ToRect() == target.TaskbarBounds;
    }

    private HashSet<IntPtr> FullScreenMonitors()
    {
        var covered = new HashSet<IntPtr>();
        var monitors = _overlays.Values.Select(entry => TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2)).Distinct().ToArray();
        TaskbarDiscovery.Native.EnumWindows((window, _) =>
        {
            if (!TaskbarDiscovery.Native.IsWindowVisible(window) || IsIconic(window)) return true;
            TaskbarDiscovery.Native.GetWindowThreadProcessId(window, out var process);
            if (process == (uint)Environment.ProcessId) return true;
            var name = new StringBuilder(128);
            TaskbarDiscovery.Native.GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!TaskbarDiscovery.Native.GetWindowRect(window, out var rectangle)) return true;
            var bounds = rectangle.ToRect();
            foreach (var monitor in monitors)
            {
                var info = new TaskbarDiscovery.Native.MonitorInfo { Size = Marshal.SizeOf<TaskbarDiscovery.Native.MonitorInfo>() };
                if (TaskbarDiscovery.Native.GetMonitorInfo(monitor, ref info) && bounds.Contains(info.Monitor.ToRect())) covered.Add(monitor);
            }
            return true;
        }, IntPtr.Zero);
        return covered;
    }

    private void Fail(Exception error)
    {
        _lastFailure = new
        {
            CapturedUtc = DateTimeOffset.UtcNow, ExceptionType = error.GetType().FullName, error.Message,
            error.HResult, NativeErrorCode = error is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null,
            error.StackTrace, Details = error.ToString(), Operation = _operation, Cause = _lastCause,
            Generation = _generation, ScanId = _scanId
        };
        var snapshots = new List<object> { ServiceSnapshot("integration-failure-before-cleanup") };
        try
        {
            var inventory = TaskbarDiagnostics.Inventory();
            snapshots.Add(inventory.Snapshot);
            var covered = FullScreenMonitors();
            foreach (var entry in _overlays.Values)
                snapshots.Add(TaskbarDiagnostics.Capture(entry.Target, entry.Overlay, "integration-failure-before-cleanup", _lastCause,
                    StillValid(entry.Target), covered.Contains(TaskbarDiscovery.Native.MonitorFromWindow(entry.Target.Window, 2))));
        }
        catch (Exception captureError) when (captureError is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { snapshots.Add(new { Phase = "failure-capture-error", captureError.Message }); }
        var pending = _diagnostics.WriteFailureAsync(new { Failure = _lastFailure, Snapshots = snapshots.ToArray() });
        Configure(false);
        _failureStatus = "Startbutton-Overlays entfernt: " + error.Message + " Erneut aus-/einschalten, um die Erkennung zu wiederholen.";
        SetStatus(_failureStatus);
        _ = ObserveFailureLogAsync(pending, _lastFailure);
    }

    private async Task ObserveFailureLogAsync(Task<string?> pending, object failure)
    {
        var error = await pending;
        if (!ReferenceEquals(_lastFailure, failure) || error == null) return;
        _failureLogError = error;
        if (_failureStatus != null)
        {
            _failureStatus += " Fehlerprotokoll konnte nicht geschrieben werden: " + error;
            SetStatus(_failureStatus);
        }
    }

    private void ClearOverlays()
    {
        foreach (var entry in _overlays.Values) entry.Overlay.Close();
        _overlays.Clear();
    }

    private void SetStatus(string status)
    {
        if (_failureStatus != null) status = _failureStatus;
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Configure(false);
        _disposed = true;
        _generation++;
        _rescan.Tick -= Rescan;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
