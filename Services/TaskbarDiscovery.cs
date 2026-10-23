using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace AeroStart.Services;

internal sealed record TaskbarTarget(IntPtr Window, uint ProcessId, Rect TaskbarBounds, Rect ButtonBounds, double VisualOffsetX,
    TaskbarPositionEvidence? Position = null);
internal sealed record TaskbarNeighbor(string AutomationId, Rect Bounds);
internal sealed record VisualOffsetMeasurement(string Source, double? OffsetPhysicalX, string Reason, IReadOnlyList<TaskbarNeighbor> References);
internal sealed record TaskbarPositionEvidence(string DetectionPath, long Monitor, Rect MonitorBounds, uint Dpi,
    VisualOffsetMeasurement Filtered, VisualOffsetMeasurement? RawView, string AppliedSource, bool Reliable);
internal sealed record TaskbarDecision(string Hwnd, string Outcome, string Stage, int? CandidateCount, string? ExceptionType, string? Error, int? HResult, object? Details = null);
internal sealed record TaskbarDiscoveryResult(int Taskbars, IReadOnlyList<TaskbarTarget> Targets, IReadOnlyList<TaskbarDecision> Decisions);
internal sealed record RawTaskbarElement(int Index, int? ParentIndex, int Depth, string AutomationId, string ClassName,
    int ControlTypeId, string ControlType, int NativeWindowHandle, bool IsOffscreen, bool IsEnabled, Rect? Bounds);
internal sealed record RawTaskbarTree(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    IReadOnlyList<RawTaskbarElement> Entries, bool Truncated, string? Error)
{
    public string View => "RawView";
    public int MaxElements => 128;
    public int MaxDepth => 12;
    public int BudgetMs => 250;
    public string Note => "Zeitbudget nur zwischen Provideraufrufen; einzelne Aufrufe koennen blockieren. Unvollstaendige Traversierung darf keine Eindeutigkeit begruenden.";
}
internal sealed record RawStartSelection(Rect? Bounds, int CandidateCount, string Reason);
internal sealed record TaskbarMeasurement(uint ProcessId, IntPtr Monitor, Rect Bounds, Rect MonitorBounds, uint Dpi = 96);

internal static class TaskbarDiscovery
{
    public static TaskbarDiscoveryResult Discover(Action<string>? progress = null)
    {
        progress?.Invoke("EnumWindows: Taskleisten suchen");
        var windows = new List<IntPtr>();
        Native.EnumWindows((window, _) =>
        {
            var name = new StringBuilder(128);
            Native.GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") windows.Add(window);
            return true;
        }, IntPtr.Zero);
        var targets = new List<TaskbarTarget>();
        var decisions = new List<TaskbarDecision>();
        foreach (var window in windows)
        {
            var stage = "Native validation";
            int? candidateCount = null;
            var outcome = "No matching visible contained StartButton";
            object? details = null;
            RawTaskbarTree? rawTree = null;
            try
            {
                if (!Native.IsWindowVisible(window)) { outcome = "Taskbar not visible"; continue; }
                if (!Native.GetWindowRect(window, out var rectangle)) { outcome = "GetWindowRect failed"; continue; }
                var taskbar = rectangle.ToRect();
                var measurement = Measure(window);
                if (measurement == null || measurement.Bounds != taskbar) { outcome = "Native measurement changed before UIA"; continue; }
                stage = "UIA FromHandle";
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: UIA FromHandle");
                var root = AutomationElement.FromHandle(window);
                stage = "UIA FindAll StartButtons";
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: UIA FindAll Startbuttons");
                var buttons = root.FindAll(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
                                        new PropertyCondition(AutomationElement.ClassNameProperty, "Start"))));
                Rect? match = null;
                candidateCount = buttons.Count;
                details = new { Source = "Filtered", FilteredCandidateCount = buttons.Count };
                if (UseRawView(buttons.Count))
                {
                    stage = "RawView fallback after zero filtered matches";
                    progress?.Invoke($"HWND=0x{window.ToInt64():X}: validierter RawView-Zweitweg");
                    var tree = CaptureRawTree(root);
                    rawTree = tree;
                    var selection = SelectRawStart(tree, taskbar, measurement.MonitorBounds);
                    details = new { Source = "RawView", FilteredCandidateCount = 0, RawCandidateCount = selection.CandidateCount,
                        SelectionReason = selection.Reason, Tree = tree };
                    match = selection.Bounds;
                    if (match == null) { outcome = "RawView rejected: " + selection.Reason; continue; }
                }
                stage = "UIA candidate properties";
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: UIA Startbutton-Eigenschaften ({buttons.Count})");
                for (var i = 0; i < buttons.Count; i++)
                {
                    var current = buttons[i].Current;
                    var button = current.BoundingRectangle;
                    if (current.IsOffscreen || !ValidBounds(button) || !taskbar.Contains(button)) continue;
                    if (match != null) { match = null; outcome = "Multiple matching visible contained StartButtons"; break; }
                    match = button;
                }
                if (match is not { } bounds) continue;
                stage = "Monitor validation";
                if (!measurement.MonitorBounds.Contains(bounds)) { outcome = "StartButton outside monitor"; continue; }
                stage = "UIA Appid neighbors / visual offset";
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: UIA Appid-Nachbarn/VisualOffset");
                var filteredOffset = GetFilteredVisualOffset(root, taskbar, bounds);
                if (rawTree == null && filteredOffset.OffsetPhysicalX == null) rawTree = CaptureRawTree(root);
                var rawOffset = rawTree == null ? null : CalculateVisualOffset("RawView", taskbar, bounds,
                    rawTree.Entries.Where(entry => entry.ControlTypeId == ControlType.Button.Id && !entry.IsOffscreen && entry.Bounds != null)
                        .Select(entry => new TaskbarNeighbor(entry.AutomationId, entry.Bounds!.Value)), !rawTree.Truncated && rawTree.Error == null);
                var selectedOffset = rawOffset?.OffsetPhysicalX != null ? rawOffset : filteredOffset;
                var path = UseRawView(buttons.Count) ? "RawView" : "Filtered";
                var position = new TaskbarPositionEvidence(path, measurement.Monitor.ToInt64(), measurement.MonitorBounds, measurement.Dpi,
                    filteredOffset, rawOffset, selectedOffset.Source, selectedOffset.OffsetPhysicalX != null);
                details = new { Detection = details, Position = position };
                if (!SameMeasurement(measurement, Measure(window))) { outcome = "Native identity/geometry changed during UIA"; continue; }
                targets.Add(new TaskbarTarget(window, measurement.ProcessId, taskbar, bounds, selectedOffset.OffsetPhysicalX ?? 0, position));
                outcome = "Accepted";
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: Ziel erkannt");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                // Explorer may disappear while its automation tree is being read.
                progress?.Invoke($"HWND=0x{window.ToInt64():X}: {ex.GetType().Name}: {ex.Message}");
                decisions.Add(new TaskbarDecision($"0x{window.ToInt64():X}", "Exception", stage, candidateCount, ex.GetType().FullName, ex.Message, ex.HResult, details));
                outcome = "ExceptionRecorded";
            }
            finally
            {
                if (outcome != "ExceptionRecorded") decisions.Add(new TaskbarDecision($"0x{window.ToInt64():X}", outcome, stage, candidateCount, null, null, null, details));
            }
        }
        progress?.Invoke($"Abgeschlossen: {windows.Count} Taskleisten, {targets.Count} Ziele");
        return new TaskbarDiscoveryResult(windows.Count, targets, decisions);
    }

    internal static RawStartSelection SelectRawStart(RawTaskbarTree tree, Rect taskbar, Rect monitor)
    {
        if (tree.Truncated || tree.Error != null) return new(null, 0, "Incomplete or failed traversal");
        if (!ValidBounds(taskbar) || !ValidBounds(monitor)) return new(null, 0, "Invalid taskbar or monitor bounds");
        var candidates = tree.Entries.Where(entry => entry.Depth > 0 && entry.ControlTypeId == ControlType.Button.Id
            && (entry.AutomationId == "StartButton" || entry.ClassName == "Start") && !entry.IsOffscreen
            && entry.Bounds is { } bounds && ValidBounds(bounds) && taskbar.Contains(bounds) && monitor.Contains(bounds)).ToArray();
        return candidates.Length == 1 ? new(candidates[0].Bounds, 1, "Unique visible contained StartButton")
            : new(null, candidates.Length, candidates.Length == 0 ? "No valid candidate" : "Ambiguous candidates");
    }

    internal static bool SameMeasurement(TaskbarMeasurement before, TaskbarMeasurement? after) => after != null && before == after;
    internal static bool UseRawView(int filteredCount) => filteredCount == 0;

    internal static TaskbarTarget ResolveVisualOffset(TaskbarTarget current, TaskbarTarget? previous)
    {
        if (current.Position is not { Reliable: false } evidence || previous?.Position is not { Reliable: true } old) return current;
        if (current.Window != previous.Window || current.ProcessId != previous.ProcessId
            || current.TaskbarBounds != previous.TaskbarBounds || current.ButtonBounds != previous.ButtonBounds
            || evidence.Monitor != old.Monitor || evidence.MonitorBounds != old.MonitorBounds || evidence.Dpi != old.Dpi) return current;
        return current with { VisualOffsetX = previous.VisualOffsetX,
            Position = evidence with { AppliedSource = "PreviousMeasuredSameGeometry", Reliable = true } };
    }

    internal static TaskbarMeasurement? Measure(IntPtr window)
    {
        if (!Native.IsWindowVisible(window) || !Native.GetWindowRect(window, out var rect)) return null;
        Native.GetWindowThreadProcessId(window, out var process);
        var monitor = Native.MonitorFromWindow(window, 2);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        return process != 0 && Native.GetMonitorInfo(monitor, ref info)
            ? new TaskbarMeasurement(process, monitor, rect.ToRect(), info.Monitor.ToRect(), Native.GetDpiForWindow(window)) : null;
    }

    internal static RawTaskbarTree CaptureRawTree(AutomationElement root)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var entries = new List<RawTaskbarElement>();
        var walker = TreeWalker.RawViewWalker;
        var truncated = false;
        string? error = null;
        bool Budget() => entries.Count < 128 && clock.Elapsed < TimeSpan.FromMilliseconds(250);
        void Visit(AutomationElement element, int depth, int? parentIndex)
        {
            if (!Budget()) { truncated = true; return; }
            var index = entries.Count;
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                entries.Add(new RawTaskbarElement(index, parentIndex, depth,
                    current.AutomationId, current.ClassName, current.ControlType.Id,
                    current.ControlType.ProgrammaticName, current.NativeWindowHandle,
                    current.IsOffscreen, current.IsEnabled, ValidBounds(bounds) ? bounds : null));
                if (!Budget()) { truncated = true; return; }
                var child = walker.GetFirstChild(element);
                if (depth >= 12 && child != null) { truncated = true; return; }
                while (child != null)
                {
                    Visit(child, depth + 1, index);
                    if (!Budget()) { truncated = true; return; }
                    child = walker.GetNextSibling(child);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                error = ex.GetType().FullName + ": " + ex.Message;
                truncated = true;
            }
        }
        Visit(root, 0, null);
        return new RawTaskbarTree(started, DateTimeOffset.UtcNow, entries, truncated, error);
    }

    private static VisualOffsetMeasurement GetFilteredVisualOffset(AutomationElement root, Rect taskbar, Rect start)
    {
        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var neighbors = new List<TaskbarNeighbor>();
        for (var i = 0; i < buttons.Count; i++)
        {
            var current = buttons[i].Current;
            if (!current.IsOffscreen) neighbors.Add(new TaskbarNeighbor(current.AutomationId, current.BoundingRectangle));
        }
        return CalculateVisualOffset("Filtered", taskbar, start, neighbors, true);
    }

    internal static VisualOffsetMeasurement CalculateVisualOffset(string source, Rect taskbar, Rect start,
        IEnumerable<TaskbarNeighbor> neighbors, bool complete)
    {
        if (!complete) return new(source, null, "Incomplete neighbor traversal", Array.Empty<TaskbarNeighbor>());
        if (taskbar.Width <= taskbar.Height) return new(source, 0, "Vertical taskbar: centered", Array.Empty<TaskbarNeighbor>());
        var center = start.Left + start.Width / 2;
        var nearest = neighbors.Where(neighbor => neighbor.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)
            && ValidBounds(neighbor.Bounds) && taskbar.Contains(neighbor.Bounds)
            && neighbor.Bounds.Top < start.Bottom && neighbor.Bounds.Bottom > start.Top
            && (neighbor.Bounds.Left >= start.Right || neighbor.Bounds.Right <= start.Left))
            .DistinctBy(neighbor => neighbor.Bounds)
            .OrderBy(neighbor => Math.Abs(neighbor.Bounds.Left + neighbor.Bounds.Width / 2 - center)).Take(2).ToArray();
        if (nearest.Length != 2) return new(source, null, "Fewer than two Appid references", nearest);
        var first = nearest[0].Bounds.Left + nearest[0].Bounds.Width / 2;
        var second = nearest[1].Bounds.Left + nearest[1].Bounds.Width / 2;
        if (Math.Sign(first - center) != Math.Sign(second - center)) return new(source, null, "References on opposite sides", nearest);
        var visualCenter = 2 * first - second;
        return visualCenter > start.Left && visualCenter < start.Right
            ? new(source, visualCenter - center, "2*firstCenter-secondCenter-startCenter", nearest)
            : new(source, null, "Extrapolated center outside StartButton", nearest);
    }

    private static bool ValidBounds(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
        internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
