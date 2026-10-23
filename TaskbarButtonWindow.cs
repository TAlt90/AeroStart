using AeroStart.Services;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AeroStart;

internal sealed class TaskbarButtonWindow : Window
{
    private static readonly Brush NativeHitTestBackground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    private readonly Button _button;
    private readonly Action _clicked;
    private readonly HwndSource _source;
    private bool _closed;
    private TaskbarButtonVisualState? _visualState;
    private bool _menuOpen;
    public IntPtr Handle => _source.Handle;
    public DateTimeOffset? LastPlacementUtc { get; private set; }
    public string? LastPlacementReason { get; private set; }
    public DateTimeOffset? LastZOrderUtc { get; private set; }
    public string? LastZOrderReason { get; private set; }
    public DateTimeOffset? LastMouseMessageUtc { get; private set; }
    public string? LastMouseMessage { get; private set; }
    public object? LastVisualPlacement { get; private set; }

    private void RecordPlacement(string cause, string operation, bool zOrder)
    {
        LastPlacementUtc = DateTimeOffset.UtcNow;
        LastPlacementReason = cause + "; " + operation;
        if (!zOrder) return;
        LastZOrderUtc = LastPlacementUtc;
        LastZOrderReason = LastPlacementReason;
    }

    public TaskbarButtonWindow(Action clicked)
    {
        _clicked = clicked;
        Title = "AeroStart-Startbutton";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = NativeHitTestBackground;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 1;
        Height = 1;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _button = new Button
        {
            Template = CreateButtonTemplate(),
            Padding = new Thickness(0), Margin = new Thickness(0), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(_button, "AeroStart-Startmenü");
        ToolTip = null;
        _button.ToolTip = null;
        ToolTipService.SetIsEnabled(this, false);
        ToolTipService.SetIsEnabled(_button, false);
        _button.Click += (_, _) => _clicked();
        _button.PreviewMouseLeftButtonDown += (_, _) => SetLogoPressed(true);
        _button.PreviewMouseLeftButtonUp += (_, _) => SetLogoPressed(false);
        _button.MouseLeave += (_, _) => { EnsureVisualState(); _visualState?.SetMouseOver(false); SetLogoPressed(false); };
        _button.MouseEnter += (_, _) => { EnsureVisualState(); _visualState?.SetMouseOver(true); SetLogoPressed(_button.IsMouseCaptured && Mouse.LeftButton == MouseButtonState.Pressed); };
        _button.LostMouseCapture += (_, _) => SetLogoPressed(false);
        Content = _button;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(style | 0x08000000L | 0x00000080L));
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(WindowProc);
        ApplyAppearance();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _visualState?.Reset(); return; }
            EnsureVisualState();
            _visualState?.SetMouseOver(_button.IsMouseOver);
            _visualState?.SetMenuOpen(_menuOpen);
        };
    }

    public void ApplyAppearance()
    {
        AppearanceService.Apply(this, null);
        Background = NativeHitTestBackground;
        if (_source.CompositionTarget != null) _source.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    private static ControlTemplate CreateButtonTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Grid Background="Transparent" SnapsToDevicePixels="True">
            <Grid x:Name="Visuals" IsHitTestVisible="False">
            <Border x:Name="Hover" Width="40" Height="40" HorizontalAlignment="Center" VerticalAlignment="Center" CornerRadius="4" Background="{DynamicResource HoverBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Opacity="0"/>
            <Grid x:Name="Logo" Width="24" Height="24" HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False" RenderTransformOrigin="0.5,0.5" Margin="0,2,0,0">
              <Grid.RenderTransform><ScaleTransform ScaleX="1" ScaleY="1"/></Grid.RenderTransform>
              <Path x:Name="Panes" Data="M0,0 H11.5 V11.5 H0 Z M12.5,0 H24 V11.5 H12.5 Z M0,12.5 H11.5 V24 H0 Z M12.5,12.5 H24 V24 H12.5 Z">
                <Path.Fill>
                  <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                    <GradientStop Color="#70EAF5" Offset="0"/>
                    <GradientStop Color="#26C8F0" Offset="0.55"/>
                    <GradientStop Color="#08A5E9" Offset="1"/>
                  </LinearGradientBrush>
                </Path.Fill>
              </Path>
            </Grid>
            </Grid>
          </Grid>
        </ControlTemplate>
        """);

    private void SetLogoPressed(bool pressed)
    {
        EnsureVisualState();
        _visualState?.SetPressed(pressed);
    }

    public void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        EnsureVisualState();
        _visualState?.SetMenuOpen(open);
    }

    private void EnsureVisualState()
    {
        if (_visualState != null || _closed) return;
        _button.ApplyTemplate();
        if (_button.Template.FindName("Logo", _button) is not FrameworkElement logo
            || _button.Template.FindName("Panes", _button) is not Path panes
            || _button.Template.FindName("Hover", _button) is not Border hover) return;
        _visualState = new TaskbarButtonVisualState(hover, logo, panes);
        _visualState.SetMouseOver(_button.IsMouseOver);
        _visualState.SetMenuOpen(_menuOpen);
    }

    public bool Place(Rect bounds, IntPtr taskbar, double visualOffsetX, string cause)
    {
        if (_closed) return false;
        if (GetWindowLongPtr(Handle, -8) != taskbar)
        {
            SetWindowLongPtr(Handle, -8, taskbar);
            if (GetWindowLongPtr(Handle, -8) != taskbar) throw new Win32Exception("Taskleistenbesitzer konnte nicht gesetzt werden.");
            RecordPlacement(cause, $"Owner=0x{taskbar.ToInt64():X}", true);
        }
        var show = !IsVisible;
        if (show && !SetWindowPos(Handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
            Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)), 0x0010 | 0x0004))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (show) RecordPlacement(cause, "Initialposition ohne Z-Order; Show folgt", false);
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = bounds.Width / dpi.DpiScaleX;
        Height = bounds.Height / dpi.DpiScaleY;
        _button.ApplyTemplate();
        if (_button.Template.FindName("Visuals", _button) is FrameworkElement visuals)
            visuals.RenderTransform = new TranslateTransform(visualOffsetX / dpi.DpiScaleX, 0);
        if (show) Show();
        var taskbarTopmost = (GetWindowLongPtr(taskbar, -20).ToInt64() & 8) != 0;
        var overlayTopmost = (GetWindowLongPtr(Handle, -20).ToInt64() & 8) != 0;
        if (overlayTopmost != taskbarTopmost)
        {
            if (!SetWindowPos(Handle, new IntPtr(taskbarTopmost ? -1 : -2),
                0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            RecordPlacement(cause, $"Topmost-Band={taskbarTopmost}", true);
        }
        var preceding = GetWindow(taskbar, 3);
        if (preceding != Handle || !TaskbarDiscovery.Native.GetWindowRect(Handle, out var current) || current.ToRect() != bounds)
        {
            var insertAfter = preceding == Handle ? GetWindow(Handle, 3) : preceding;
            if (insertAfter == IntPtr.Zero && taskbarTopmost) insertAfter = new IntPtr(-1);
            if (!SetWindowPos(Handle, insertAfter, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)), 0x0010 | 0x0040))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            RecordPlacement(cause, $"SetWindowPos insertAfter=0x{insertAfter.ToInt64():X}, bounds={bounds}", true);
        }
        LastVisualPlacement = new
        {
            CapturedUtc = DateTimeOffset.UtcNow, Cause = cause, UiaBoundsPhysical = bounds,
            DpiScaleX = dpi.DpiScaleX, DpiScaleY = dpi.DpiScaleY,
            OffsetPhysicalX = visualOffsetX, OffsetDipX = visualOffsetX / dpi.DpiScaleX,
            WindowWidthDip = Width, WindowHeightDip = Height,
            OverlayBoundsPhysical = TaskbarDiscovery.Native.GetWindowRect(Handle, out var placed) ? (Rect?)placed.ToRect() : null,
            IntendedVisualCenterPhysicalX = bounds.Left + bounds.Width / 2 + visualOffsetX
        };
        foreach (var point in new[]
        {
            new NativePoint((int)bounds.Left + 1, (int)bounds.Top + 1),
            new NativePoint((int)bounds.Right - 2, (int)bounds.Top + 1),
            new NativePoint((int)bounds.Left + 1, (int)bounds.Bottom - 2),
            new NativePoint((int)bounds.Right - 2, (int)bounds.Bottom - 2),
            new NativePoint((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2))
        })
        {
            var hit = WindowFromPoint(point);
            if (hit != Handle && GetAncestor(hit, 2) != Handle) return false;
        }
        return true;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x0201 or 0x0202 or 0x0203 or 0x0215)
        {
            LastMouseMessageUtc = DateTimeOffset.UtcNow;
            LastMouseMessage = $"Received HWND=0x{hwnd.ToInt64():X}, message=0x{message:X4}, wParam=0x{wParam.ToInt64():X}, lParam=0x{lParam.ToInt64():X}";
        }
        if (message == 0x0084) { handled = true; return new IntPtr(1); }
        if (message == 0x004E && lParam != IntPtr.Zero && Marshal.PtrToStructure<NotificationHeader>(lParam).Code == -521)
        { handled = true; return new IntPtr(1); }
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        if (message is 0x001A or 0x031A or 0x0320)
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) ApplyAppearance(); }));
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _menuOpen = false;
        _visualState?.Reset();
        _source.RemoveHook(WindowProc);
        base.OnClosed(e);
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));
    [StructLayout(LayoutKind.Sequential)] private struct NotificationHeader { public IntPtr Window; public UIntPtr Id; public int Code; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePoint(int x, int y) { public readonly int X = x, Y = y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    private static void SetWindowLongPtr(IntPtr window, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(window, index, value); else SetWindowLong32(window, index, value.ToInt32());
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
