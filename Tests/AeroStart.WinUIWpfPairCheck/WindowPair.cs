using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinWindow = Microsoft.UI.Xaml.Window;

internal sealed class WindowPair
{
    private readonly WinWindow _behind;
    private readonly PairBackdrop _backdrop;
    private readonly Window _front;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Native.SubclassProc _backgroundProc;
    private readonly nint _backHandle;
    private nint _frontHandle;
    private bool _syncing, _closing, _hidden;
    private int _checks, _failures, _dpiEvents, _steps;
    private DispatcherTimer? _returnTimer, _testTimer;
    private Native.Rect _original;
    private string _testResult = "Selbsttest ausstehend";
    private readonly System.Diagnostics.Stopwatch _fadeClock = new();
    private bool _fadeRunning, _slowFade;
    private double _fadeAlpha = 1;

    internal WindowPair(WinWindow behind, PairBackdrop backdrop)
    {
        _behind = behind;
        _backdrop = backdrop;
        _backHandle = WinRT.Interop.WindowNative.GetWindowHandle(behind);
        Native.SetWindowLongPtr(_backHandle, -20, Native.GetWindowLongPtr(_backHandle, -20) | 0x08000080);
        _backgroundProc = BackgroundMessage;
        if (!Native.SetWindowSubclass(_backHandle, _backgroundProc, 1, 0))
            throw new Win32Exception();
        _front = new Window
        {
            Title = "Isolierter WinUI + WPF Acrylic-Test",
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Width = 820, Height = 600, Left = 180, Top = 140,
            ResizeMode = ResizeMode.NoResize, Foreground = Brushes.White
        };
        var root = new Grid();
        var panel = new StackPanel { Margin = new Thickness(24), Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        var heading = new TextBlock { Text = "WPF scharf vor WinUI Acrylic", FontSize = 24, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.SizeAll };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) _front.DragMove(); };
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "Am Titel ziehen; auch auf einen Monitor mit anderer DPI. Rechts bleibt die WPF-Fläche nahezu transparent (Alpha 1/255).\nBrowser-Streifen dahinter: Kanten müssen weich, diese Texte scharf bleiben.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBox { Text = "Fokus / Tastatur / Tab hier prüfen", Margin = new Thickness(0, 0, 0, 10), FontSize = 17 });
        AddButton(panel, "Acrylic Ein / Aus (Aus ist deckend)", () =>
        {
            _behind.SystemBackdrop = _behind.SystemBackdrop is null ? _backdrop : null;
            UpdateFocus();
        });
        AddButton(panel, "Größe wechseln", () =>
        {
            Native.Check(Native.GetWindowRect(_frontHandle, out var r));
            bool large = r.Right - r.Left > 850;
            Native.Check(Native.SetWindowPos(_frontHandle, 0, 0, 0, large ? 820 : 940, large ? 600 : 690, 0x16));
            Sync();
        });
        AddButton(panel, "Beide ausblenden / nach 1 Sekunde zurück", HideAndReturn);
        AddButton(panel, "Fade prüfen: 120 ms Aus / 180 ms Ein", () => StartFade(false));
        AddButton(panel, "Fade prüfen: langsame Alpha-Zwischenstände", () => StartFade(true));
        AddButton(panel, "Bestätigten Acrylic-Zustand wiederherstellen", ResetFade);
        AddButton(panel, "Prüfstatus protokollieren", () => Verify(true));
        AddButton(panel, "Beide schließen", _front.Close);
        panel.Children.Add(_status);
        root.Children.Add(panel);
        _front.Content = root;
        _front.SourceInitialized += (_, _) =>
        {
            _frontHandle = new WindowInteropHelper(_front).Handle;
            Native.SetWindowLongPtr(_frontHandle, -8, _backHandle);
            HwndSource.FromHwnd(_frontHandle)!.AddHook(FrontMessage);
        };
        _front.Activated += (_, _) => { UpdateFocus(); Sync(); };
        _front.Deactivated += (_, _) => Post(() => UpdateFocus());
        _front.IsVisibleChanged += (_, _) =>
        {
            if (_closing) return;
            if (_front.IsVisible) { _hidden = false; Sync(); }
            else { _hidden = true; Native.ShowWindow(_backHandle, 0); }
        };
        _front.Closed += (_, _) => Close();
        _behind.Closed += (_, _) => { if (!_closing) _front.Close(); };
        _monitor.Tick += (_, _) => { UpdateFocus(); Verify(false); };
    }

    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 8) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    internal void Show()
    {
        _front.Show();
        Sync();
        _front.Activate();
        _monitor.Start();
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _testTimer.Tick += (_, _) => SelfTestStep();
        _testTimer.Start();
        Console.WriteLine("PAIR START: official DesktopAcrylicBackdrop, owned transparent WPF, PerMonitorV2; no screen capture.");
    }

    private void StartFade(bool slow)
    {
        if (_fadeRunning) return;
        _testTimer?.Stop();
        _slowFade = slow;
        Native.SetWindowLongPtr(_backHandle, -20, Native.GetWindowLongPtr(_backHandle, -20) | 0x00080000);
        try
        {
            ApplyFade(1);
            _fadeRunning = true;
            _fadeClock.Restart();
            CompositionTarget.Rendering += FadeFrame;
            Console.WriteLine($"FADE START slow={slow}; WinUI WS_EX_LAYERED candidate; identical alpha on both layers; visual blur NOT established.");
        }
        catch (Win32Exception ex)
        {
            ResetFade();
            _testResult = "Nativer Fade nicht verfügbar: " + ex.Message;
            Verify(true);
        }
    }

    private void FadeFrame(object? sender, EventArgs e)
    {
        double ms = _fadeClock.Elapsed.TotalMilliseconds;
        if (_slowFade)
        {
            double[] levels = [1, 0.75, 0.5, 0.25, 0, 0.25, 0.5, 0.75, 1];
            int index = Math.Min((int)(ms / 900), levels.Length - 1);
            ApplyFade(levels[index]);
            if (ms < levels.Length * 900) return;
        }
        else
        {
            double alpha;
            if (ms < 120) alpha = 1 - Math.Pow(ms / 120, 3);
            else if (ms < 620) alpha = 0;
            else alpha = 1 - Math.Pow(1 - Math.Clamp((ms - 620) / 180, 0, 1), 3);
            ApplyFade(alpha);
            if (ms < 800) return;
        }
        CompositionTarget.Rendering -= FadeFrame;
        _fadeRunning = false;
        _fadeClock.Stop();
        ApplyFade(1);
        _testResult = "Fade beendet; Layered-Kandidat aktiv. Blur/Artefakte visuell prüfen, dann Referenz wiederherstellen.";
        Verify(true);
    }

    private void ApplyFade(double alpha)
    {
        byte nativeAlpha = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        Native.Check(Native.SetLayeredWindowAttributes(_backHandle, 0, nativeAlpha, 2));
        ((UIElement)_front.Content).Opacity = nativeAlpha / 255.0;
        if (_fadeAlpha != nativeAlpha / 255.0 && _slowFade)
            Console.WriteLine($"FADE alpha={nativeAlpha}/255; front and back requested equally; visual verification required.");
        _fadeAlpha = nativeAlpha / 255.0;
    }

    private void ResetFade()
    {
        CompositionTarget.Rendering -= FadeFrame;
        _fadeRunning = false;
        _fadeClock.Stop();
        ((UIElement)_front.Content).Opacity = 1;
        _fadeAlpha = 1;
        Native.SetWindowLongPtr(_backHandle, -20, Native.GetWindowLongPtr(_backHandle, -20) & ~0x00080000);
        Native.Check(Native.SetWindowPos(_backHandle, 0, 0, 0, 0, 0, 0x0037));
        Native.RedrawWindow(_backHandle, 0, 0, 0x0185);
        _testResult = "Nicht-layered Acrylic-Referenz wiederhergestellt; visuell vergleichen.";
        Sync();
        Verify(true);
    }

    private void Post(Action action) => _front.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_closing) action(); }));

    private nint FrontMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0047 && !_syncing) Sync();
        if (message == 0x02E0)
        {
            _dpiEvents++;
            Post(() => { Sync(); Verify(true); });
        }
        if (message == 0x0006) Post(UpdateFocus);
        return 0;
    }

    private nint BackgroundMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        var result = Native.DefSubclassProc(hwnd, message, wParam, lParam);
        if (!_closing && !_syncing && (message == 0x0047 || message == 0x02E0)) Post(Sync);
        if (!_closing && message == 0x0006)
            Post(() => { if (Native.GetForegroundWindow() == _backHandle) _front.Activate(); UpdateFocus(); });
        return result;
    }

    private void Sync()
    {
        if (_closing || _syncing || _frontHandle == 0) return;
        _syncing = true;
        try
        {
            if (_hidden || !_front.IsVisible) { Native.ShowWindow(_backHandle, 0); return; }
            Native.Check(Native.GetWindowRect(_frontHandle, out var r));
            Native.Check(Native.SetWindowPos(_backHandle, _frontHandle, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0010 | 0x0040 | 0x0200));
        }
        finally { _syncing = false; }
    }

    private void UpdateFocus()
    {
        nint active = Native.GetForegroundWindow();
        _backdrop.SetPairActive(!_hidden && (active == _frontHandle || active == _backHandle));
    }

    private bool Verify(bool log)
    {
        if (_closing || _frontHandle == 0) return false;
        Native.Check(Native.GetWindowRect(_frontHandle, out var f));
        Native.Check(Native.GetWindowRect(_backHandle, out var b));
        uint fdpi = Native.GetDpiForWindow(_frontHandle), bdpi = Native.GetDpiForWindow(_backHandle);
        bool visible = Native.IsWindowVisible(_frontHandle), backVisible = Native.IsWindowVisible(_backHandle);
        bool order = !visible || Native.GetWindow(_frontHandle, 2) == _backHandle;
        bool owner = Native.GetWindow(_frontHandle, 4) == _backHandle;
        bool ok = f.Equals(b) && fdpi == bdpi && visible == backVisible && order && owner;
        _checks++;
        if (!ok) _failures++;
        string line = $"Prüfungen={_checks}; Abweichungen={_failures}\nPixelrechtecke gleich={f.Equals(b)}; DPI={fdpi}/{bdpi}; DPI-Ereignisse={_dpiEvents}\nSichtbarkeit={visible}/{backVisible}; direkt dahinter={order}; Owner={owner}\nFokus={(Native.GetForegroundWindow() == _frontHandle ? "WPF" : Native.GetForegroundWindow() == _backHandle ? "WinUI" : "extern")}\n{_testResult}";
        _status.Text = line;
        if (log || !ok) Console.WriteLine(line);
        return ok;
    }

    private void SelfTestStep()
    {
        switch (_steps++)
        {
            case 0:
                Native.Check(Native.GetWindowRect(_frontHandle, out _original));
                Native.Check(Native.SetWindowPos(_frontHandle, 0, _original.Left + 60, _original.Top + 30, 900, 640, 0x14));
                Sync(); Verify(true);
                break;
            case 1:
                _hidden = true; _front.Hide(); Native.ShowWindow(_backHandle, 0); Verify(true);
                break;
            case 2:
                _hidden = false; _front.Show(); Sync(); _front.Activate(); Verify(true);
                break;
            default:
                Native.Check(Native.SetWindowPos(_frontHandle, 0, _original.Left, _original.Top, _original.Right - _original.Left, _original.Bottom - _original.Top, 0x14));
                Sync();
                _testTimer!.Stop();
                _testResult = _failures == 0 ? "Geometrie-/HideShow-Selbsttest bestanden; visuelle Prüfung offen" : "Selbsttest: Abweichungen – siehe Konsole";
                Verify(true);
                break;
        }
    }

    private void HideAndReturn()
    {
        _hidden = true;
        _front.Hide();
        Native.ShowWindow(_backHandle, 0);
        UpdateFocus();
        _returnTimer?.Stop();
        _returnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _returnTimer.Tick += (_, _) =>
        {
            _returnTimer.Stop();
            if (_closing) return;
            _hidden = false; _front.Show(); Sync(); _front.Activate(); Verify(true);
        };
        _returnTimer.Start();
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        CompositionTarget.Rendering -= FadeFrame;
        _monitor.Stop(); _returnTimer?.Stop(); _testTimer?.Stop();
        Native.RemoveWindowSubclass(_backHandle, _backgroundProc, 1);
        _behind.Close();
        Console.WriteLine($"PAIR CLOSED checks={_checks}, failures={_failures}, dpiEvents={_dpiEvents}");
        Microsoft.UI.Xaml.Application.Current.Exit();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }
    internal delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    internal static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
