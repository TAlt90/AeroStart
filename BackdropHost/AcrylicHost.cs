using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AeroStart.BackdropHost;

public static class AcrylicHost
{
    private static readonly object Gate = new();
    private static readonly TaskCompletionSource<DispatcherQueue> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Thread? _thread;
    private static Application? _application;

    public static AcrylicWindow Create()
    {
        lock (Gate)
        {
            if (_thread is null)
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        WinRT.ComWrappersSupport.InitializeComWrappers();
                        Application.Start(parameters =>
                        {
                            var queue = DispatcherQueue.GetForCurrentThread();
                            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                            _application = new HostApplication { DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown };
                            Ready.TrySetResult(queue);
                        });
                    }
                    catch (Exception ex) { Ready.TrySetException(ex); }
                }) { IsBackground = true, Name = "AeroStart Acrylic" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        var queue = Ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        var created = new TaskCompletionSource<AcrylicWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try { created.SetResult(new AcrylicWindow(queue)); }
            catch (Exception ex) { created.SetException(ex); }
        })) throw new InvalidOperationException("Der Mattglas-Host ist nicht verfügbar.");
        return created.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    public static void Shutdown()
    {
        if (Ready.Task.IsCompletedSuccessfully)
            Ready.Task.Result.TryEnqueue(() => _application?.Exit());
    }

    private sealed class HostApplication : Application { }
}

public sealed class AcrylicWindow : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly Window _window;
    private readonly MenuBackdrop _backdrop;
    private readonly Microsoft.UI.Xaml.Controls.Grid _root;
    private DispatcherQueueTimer? _diagnosticTimer;
    private bool _disposed;
    public nint Handle { get; }

    internal AcrylicWindow(DispatcherQueue queue)
    {
        _queue = queue;
        _backdrop = new MenuBackdrop();
        _window = new Window { Title = "AeroStart Mattglas", SystemBackdrop = _backdrop };
        var root = new Microsoft.UI.Xaml.Controls.Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _root = root;
        _window.Content = root;
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)_window.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _window.AppWindow.IsShownInSwitchers = false;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtr(Handle, -20, GetWindowLongPtr(Handle, -20) | 0x08000080);
        root.Loaded += (_, _) =>
        {
            SetWindowLongPtr(Handle, -20, GetWindowLongPtr(Handle, -20) | 0x00080000);
            if (!SetLayeredWindowAttributes(Handle, 0, 0, 2)) throw new Win32Exception();
            IsReady = true;
            WriteDiagnostic("Loaded");
        };
        _window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        _window.AppWindow.Show(false);
    }

    public bool IsReady { get; private set; }

    public void SetActive(bool active)
    {
        if (!_disposed) _queue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _backdrop.SetActive(active);
            WriteDiagnostic("SetActive");
            _diagnosticTimer?.Stop();
            _diagnosticTimer = _queue.CreateTimer();
            _diagnosticTimer.Interval = TimeSpan.FromSeconds(1);
            _diagnosticTimer.IsRepeating = false;
            _diagnosticTimer.Tick += (_, _) => { if (!_disposed) WriteDiagnostic("Settled"); };
            _diagnosticTimer.Start();
        });
    }

    private void WriteDiagnostic(string reason)
    {
        string text = $"{DateTimeOffset.Now:O} {reason}\nHWND={Handle}; Ready={IsReady}; Thread={Environment.CurrentManagedThreadId}\n" +
            $"XAML Loaded={_root.IsLoaded}; Size={_root.ActualWidth}x{_root.ActualHeight}; HostVisible={_root.XamlRoot?.IsHostVisible}; Scale={_root.XamlRoot?.RasterizationScale}\n" +
            $"AppWindow Visible={_window.AppWindow.IsVisible}; Position={_window.AppWindow.Position}; Size={_window.AppWindow.Size}\n" +
            $"Backdrop Connected={_backdrop.Connected}; Active={_backdrop.InputActive}; Style=0x{GetWindowLongPtr(Handle, -20):X}";
        try
        {
            string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroStart", "Logs");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "Acrylic-state.log"), text);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { System.Diagnostics.Trace.WriteLine(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.TryEnqueue(() => { _diagnosticTimer?.Stop(); _window.Close(); });
    }

    private sealed class MenuBackdrop : DesktopAcrylicBackdrop
    {
        private SystemBackdropConfiguration? _configuration;
        private bool _active;
        internal bool Connected => _configuration is not null;
        internal bool? InputActive => _configuration?.IsInputActive;
        internal void SetActive(bool active)
        {
            _active = active;
            if (_configuration is not null) _configuration.IsInputActive = active;
        }
        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            _configuration = GetDefaultSystemBackdropConfiguration(target, root);
            _configuration.IsInputActive = _active;
        }
        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, root);
            _configuration = GetDefaultSystemBackdropConfiguration(target, root);
            _configuration.IsInputActive = _active;
        }
        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            _configuration = null;
            base.OnTargetDisconnected(target);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
}
