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

    public bool IsMenuOpen => IsVisible && !_isHiding;
    public event EventHandler? MenuOpenStateChanged;
    public void DismissMenu() => HideMenu();

    public void ApplySettings(AppSettings settings)
    {
        _animationEnabled = settings.AnimateMenu;
        _menuSettings = settings;
        ApplyMenuAppearance();
        ApplyFilter();
        SystemToolsButton.Visibility = settings.ShowSystemTools ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.ShowSystemTools) CloseSystemTools();
    }

    private void ShowMenu() => ShowAtScreenPoint(MenuPlacementService.CursorPosition);

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
        if (_closed) return;
        if (!double.IsFinite(screenPoint.X) || !double.IsFinite(screenPoint.Y)
            || screenPoint.X < int.MinValue || screenPoint.X > int.MaxValue
            || screenPoint.Y < int.MinValue || screenPoint.Y > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(screenPoint));
        var wasVisible = IsVisible;
        var wasHiding = _isHiding;
        _menuRequested = true;
        _isHiding = false;
        GlassFrame.IsHitTestVisible = true;
        _animationRequest++;
        GlassFrame.RenderTransform = _menuTranslation;
        if (!wasVisible)
        {
            StopMenuAnimation();
            _menuTranslation.Y = Height;
            GlassFrame.Opacity = 0;
        }
        MenuPlacementService.Position(this, screenPoint, anchor);
        Show();
        MenuOpenStateChanged?.Invoke(this, EventArgs.Empty);
        Activate();
        ApplyFilter();
        FocusSearchBox();
        _ = RefreshWindowsPinsAsync();
        if (!wasVisible || wasHiding || GlassFrame.Opacity < 1)
            AnimateMenu(true);
    }

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

    private void AnimateMenu(bool opening)
    {
        var request = ++_animationRequest;
        var opacity = GlassFrame.Opacity;
        var offset = _menuTranslation.Y;
        StopMenuAnimation();
        GlassFrame.Opacity = opacity;
        _menuTranslation.Y = offset;
        if (!_animationEnabled || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        {
            CompleteMenuAnimation(opening, request);
            return;
        }
        var duration = TimeSpan.FromMilliseconds(opening ? 180 : 120);
        var ease = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };
        var slide = new DoubleAnimation(offset, opening ? 0 : 24, duration) { EasingFunction = ease };
        var fade = new DoubleAnimation(opacity, opening ? 1 : 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => CompleteMenuAnimation(opening, request);
        _menuTranslation.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        GlassFrame.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteMenuAnimation(bool opening, int request)
    {
        if (_closed || request != _animationRequest) return;
        StopMenuAnimation();
        if (opening)
        {
            _menuTranslation.Y = 0;
            GlassFrame.Opacity = 1;
        }
        else
        {
            Hide();
            _isHiding = false;
            _menuTranslation.Y = 0;
            GlassFrame.Opacity = 1;
        }
    }

    private void StopMenuAnimation()
    {
        _menuTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        GlassFrame.BeginAnimation(UIElement.OpacityProperty, null);
    }

}
