using AeroStart.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AeroStart;

public partial class MainWindow
{
    private readonly TranslateTransform _menuTranslation = new();
    private bool _isHiding;
    private bool _menuRequested;
    private int _animationRequest;
    private bool _animationEnabled = true;
    private MenuAcrylicService? _acrylic;

    public bool IsMenuOpen => IsVisible && !_isHiding;
    public event EventHandler? MenuOpenStateChanged;
    public void DismissMenu() => HideMenu();

    public void ApplySettings(AppSettings settings)
    {
        _animationEnabled = settings.AnimateMenu;
        _menuSettings = settings;
        ConfigureAcrylic();
        ApplyMenuAppearance();
        ApplyFilter();
        SystemToolsButton.Visibility = settings.ShowSystemTools ? Visibility.Visible : Visibility.Collapsed;
        TaskManagerShortcutButton.Visibility = settings.ShowTaskManagerShortcut ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.ShowSystemTools) CloseSystemTools();
    }

    private void ConfigureAcrylic()
    {
        var enabled = AcrylicAvailable && _menuSettings.AcrylicEnabled && !SystemParameters.HighContrast;
        if (!enabled)
        {
            _acrylic?.Dispose();
            _acrylic = null;
            return;
        }
        if (_source == null) return;
        _acrylic ??= new MenuAcrylicService(this, GlassFrame, _menuTranslation);
        _acrylic.Opacity = _menuSettings.StartMenuOpacity;
        _acrylic.Sync(_menuSettings.StartMenuOpacity);
    }

    private void ShowMenu() => ShowAtScreenPoint(MenuPlacementService.CursorPosition);
    internal static bool AcrylicAvailable => false;

    /// <summary>Opens on the monitor containing this point in physical screen pixels.</summary>
    public void ShowAtScreenPoint(Point screenPoint) => OpenMenu(screenPoint, null);

    /// <summary>Opens adjacent to a start button whose bounds are physical screen pixels.</summary>
    public void ShowAtAnchor(Rect startButtonBounds)
    {
        if (startButtonBounds.IsEmpty || startButtonBounds.Width <= 0 || startButtonBounds.Height <= 0)
            throw new ArgumentException("Der Startbutton-Anker muss eine nichtleere Bildschirmfläche sein.", nameof(startButtonBounds));
        OpenMenu(new Point(startButtonBounds.Left + startButtonBounds.Width / 2,
                           startButtonBounds.Top + startButtonBounds.Height / 2), startButtonBounds);
    }

    private void OpenMenu(Point screenPoint, Rect? anchor)
    {
        Dispatcher.VerifyAccess();

        if (_closed)
            return;

        if (!double.IsFinite(screenPoint.X) ||
            !double.IsFinite(screenPoint.Y) ||
            screenPoint.X < int.MinValue ||
            screenPoint.X > int.MaxValue ||
            screenPoint.Y < int.MinValue ||
            screenPoint.Y > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(screenPoint));
        }

        bool wasVisible = IsVisible;
        bool wasHiding = _isHiding;

        _menuRequested = true;
        _isHiding = false;

        GlassFrame.IsHitTestVisible = true;
        GlassFrame.RenderTransform = _menuTranslation;

        _animationRequest++;

        if (!wasVisible)
        {
            StopMenuAnimation();

            GlassFrame.Opacity = 0;
            _menuTranslation.Y = Height;
        }

        MenuPlacementService.Position(this, screenPoint, anchor);

        if (!wasVisible)
            SetCloaked(true);

        Show();

        _acrylic?.Sync(_menuSettings.StartMenuOpacity);

        MenuOpenStateChanged?.Invoke(this, EventArgs.Empty);

        Activate();
        ApplyFilter();
        FocusSearchBox();

        _ = RefreshWindowsPinsAsync();

        bool shouldAnimate =
            !wasVisible ||
            wasHiding ||
            GlassFrame.Opacity < 1;

        if (shouldAnimate)
            AnimateMenu(true);

        if (!wasVisible)
            UncloakAfterRender();
    }

    private void UncloakAfterRender()
    {
        int frames = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            frames++;
            bool presented = frames >= 4 && GlassFrame.Opacity >= 0.15;
            if (!presented && frames < 30)
                return;
            CompositionTarget.Rendering -= handler;
            SetCloaked(false);
        };
        CompositionTarget.Rendering += handler;
    }

    private void SetCloaked(bool cloaked)
    {
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int noTransitions = 1;
        _ = DwmSetWindowAttribute(handle, 3, ref noTransitions, sizeof(int));
        int value = cloaked ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 13, ref value, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void HideMenu()
    {
        if (_closed || !IsVisible || _isHiding) return;
        _isHiding = true;
        MenuOpenStateChanged?.Invoke(this, EventArgs.Empty);
        GlassFrame.IsHitTestVisible = false;
        _focusRequest++;
        CloseAllFlyouts();
        ResetSearch();
        AnimateMenu(false);
    }

    private Action? AnimateMenu(bool opening)
    {
        int request = ++_animationRequest;

        double opacity = GlassFrame.Opacity;
        double offset = _menuTranslation.Y;

        StopMenuAnimation();

        GlassFrame.Opacity = opacity;
        _menuTranslation.Y = offset;

        if (!_animationEnabled ||
            !SystemParameters.ClientAreaAnimation ||
            SystemParameters.HighContrast)
        {
            CompleteMenuAnimation(opening, request);
            return null;
        }

        var duration = TimeSpan.FromMilliseconds(
            opening ? 180 : 120);

        var ease = new CubicEase
        {
            EasingMode = opening
                ? EasingMode.EaseOut
                : EasingMode.EaseIn
        };

        var slide = new DoubleAnimation(
            offset,
            opening ? 0 : 24,
            duration)
        {
            EasingFunction = ease
        };

        var fade = new DoubleAnimation(
            opacity,
            opening ? 1 : 0,
            duration)
        {
            EasingFunction = ease
        };

        fade.Completed += (_, _) =>
            CompleteMenuAnimation(opening, request);

        _menuTranslation.BeginAnimation(
            TranslateTransform.YProperty,
            slide,
            HandoffBehavior.SnapshotAndReplace);

        GlassFrame.BeginAnimation(
            UIElement.OpacityProperty,
            fade,
            HandoffBehavior.SnapshotAndReplace);

        return null;
    }

    private void CompleteMenuAnimation(bool opening, int request)
    {
        if (_closed || request != _animationRequest)
            return;

        StopMenuAnimation();

        if (opening)
        {
            _menuTranslation.Y = 0;
            GlassFrame.Opacity = 1;
            _isHiding = false;
        }
        else
        {
            _menuTranslation.Y = Height;
            GlassFrame.Opacity = 0;

            Hide();

            _isHiding = false;
        }

        _acrylic?.Sync(_menuSettings.StartMenuOpacity);
    }

    private void StopMenuAnimation()
    {
        _menuTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        GlassFrame.BeginAnimation(UIElement.OpacityProperty, null);
    }

}


