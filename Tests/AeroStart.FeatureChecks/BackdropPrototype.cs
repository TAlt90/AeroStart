using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal static class BackdropPrototype
{
    internal static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var pattern = new Grid();
        for (var i = 0; i < 100; i++)
        {
            pattern.ColumnDefinitions.Add(new ColumnDefinition());
            var stripe = new System.Windows.Shapes.Rectangle { Fill = i % 2 == 0 ? Brushes.White : Brushes.Black };
            Grid.SetColumn(stripe, i);
            pattern.Children.Add(stripe);
        }
        var area = SystemParameters.WorkArea;
        var behind = new Window { Title = "Eigenes Testmuster – kein Desktop-Screenshot", Width = 740, Height = 480,
            Left = area.Left + (area.Width - 740) / 2, Top = area.Top + (area.Height - 480) / 2,
            Content = pattern, ShowInTaskbar = false };
        behind.Show();
        using var background = new HwndSource(new HwndSourceParameters("AeroStart native backdrop only")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            ParentWindow = new WindowInteropHelper(behind).Handle, Width = 500, Height = 410
        });
        background.CompositionTarget.BackgroundColor = Colors.Transparent;
        background.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (m == 0x21) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        });
        var content = new StackPanel { Margin = new Thickness(22) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        content.Children.Add(new TextBlock { Text = "Zwei-Fenster-Blur-Prototyp", FontSize = 22 });
        content.Children.Add(new TextBlock { Text = "Hier ziehen, auch auf andere Monitore.\nEin/Aus muss die Streifen WEICHZEICHNEN, nicht nur tönen.", TextWrapping = TextWrapping.Wrap });
        var on = new Button { Content = "Mattglas Ein", Margin = new Thickness(0, 8, 0, 4) };
        var off = new Button { Content = "Mattglas Aus", Margin = new Thickness(0, 4, 0, 4) };
        var move = new Button { Content = "Gekoppelte Bewegung testen", Margin = new Thickness(0, 4, 0, 4) };
        var reopen = new Button { Content = "Beide ausblenden / nach 1 Sekunde öffnen", Margin = new Thickness(0, 4, 0, 4) };
        var close = new Button { Content = "Beide schließen" };
        content.Children.Add(on); content.Children.Add(off); content.Children.Add(move); content.Children.Add(reopen);
        content.Children.Add(status); content.Children.Add(close);
        var window = new Window { Title = "AeroStart isolierter Zwei-Fenster-Prototyp",
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Width = 500, Height = 410, Left = behind.Left + 100, Top = behind.Top + 35,
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(77, 32, 32, 32)), Child = content },
            Foreground = Brushes.White, ShowInTaskbar = false };
        new WindowInteropHelper(window).Owner = background.Handle;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var disposed = false;
        var behindClosing = false;
        var enabled = true;
        var mismatches = 0;
#if ACRYLIC_SDK
        using var acrylic = new AcrylicBackdrop(background.Handle);
        window.Activated += (_, _) => acrylic.Activate(true);
        window.Deactivated += (_, _) => acrylic.Activate(false);
#endif
        void Sync()
        {
            if (disposed || !window.IsVisible) return;
            if (!GetWindowRect(handle, out var rect)) return;
            if (!SetWindowPos(background.Handle, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x54))
                throw new System.ComponentModel.Win32Exception();
            GetWindowRect(background.Handle, out var actual);
            if (!rect.Equals(actual)) mismatches++;
        }
        void Set(bool value)
        {
            enabled = value;
#if ACRYLIC_SDK
            var detail = acrylic.Set(enabled);
            var hr = 0;
#else
            var material = enabled ? 3 : 1;
            var hr = DwmSetWindowAttribute(background.Handle, 38, ref material, sizeof(int));
#endif
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var frame = DwmExtendFrameIntoClientArea(background.Handle, ref margins);
            var dark = 1;
            DwmSetWindowAttribute(background.Handle, 20, ref dark, sizeof(int));
            var layered = (GetWindowLongPtrW(background.Handle, -20).ToInt64() & 0x80000) != 0;
            var foregroundLayered = (GetWindowLongPtrW(handle, -20).ToInt64() & 0x80000) != 0;
            status.Text = $"Blur {(enabled ? "Ein" : "Aus")}; HRESULT=0x{hr:X8}; Frame=0x{frame:X8}\nHintergrund layered={layered}, Vordergrund layered={foregroundLayered}; Boundsfehler={mismatches}";
#if ACRYLIC_SDK
            status.Text += "\n" + detail;
#endif
            Console.WriteLine(status.Text);
        }
        HwndSource.FromHwnd(handle)!.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (m is 0x47 or 0x2E0) window.Dispatcher.BeginInvoke(new Action(Sync));
            return IntPtr.Zero;
        });
        window.LocationChanged += (_, _) => Sync();
        window.SizeChanged += (_, _) => Sync();
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) Sync(); else ShowWindow(background.Handle, 0); };
        content.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock) window.DragMove(); };
        on.Click += (_, _) => Set(true); off.Click += (_, _) => Set(false);
        move.Click += (_, _) =>
        {
            var start = window.Top;
            var animation = new DoubleAnimation(start, start + 24, TimeSpan.FromMilliseconds(180)) { AutoReverse = true };
            animation.Completed += (_, _) => { window.BeginAnimation(Window.TopProperty, null); Sync(); Set(enabled); };
            window.BeginAnimation(Window.TopProperty, animation);
        };
        reopen.Click += async (_, _) => { window.Hide(); await Task.Delay(1000); if (!disposed) { window.Show(); window.Activate(); Sync(); Set(enabled); } };
        close.Click += (_, _) => window.Close();
        window.Closing += (_, _) => disposed = true;
        behind.Closing += (_, _) => { behindClosing = true; if (!disposed) { disposed = true; window.Close(); } };
        window.Closed += (_, _) => { disposed = true; ShowWindow(background.Handle, 0); if (!behindClosing) behind.Close(); app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown())); };
        window.Show(); Sync(); Set(true);
        app.Run();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
