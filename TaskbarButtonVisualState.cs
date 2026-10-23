using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AeroStart;

internal sealed class TaskbarButtonVisualState
{
    private readonly Border _hover;
    private readonly ScaleTransform _scale;
    private readonly LinearGradientBrush _fill;
    private readonly Color[] _normal;
    private bool _mouseOver, _menuOpen, _pressed;
    public bool HighlightVisible => _mouseOver || _menuOpen;

    public TaskbarButtonVisualState(Border hover, FrameworkElement logo, Path panes)
    {
        _hover = hover;
        _scale = ((ScaleTransform)logo.RenderTransform).Clone();
        logo.RenderTransform = _scale;
        _fill = ((LinearGradientBrush)panes.Fill).Clone();
        panes.Fill = _fill;
        _normal = _fill.GradientStops.Select(stop => stop.Color).ToArray();
        Reset();
    }

    public void SetMouseOver(bool value)
    {
        var before = HighlightVisible;
        _mouseOver = value;
        if (before != HighlightVisible) UpdateHighlight();
    }

    public void SetMenuOpen(bool value)
    {
        var before = HighlightVisible;
        _menuOpen = value;
        if (before != HighlightVisible) UpdateHighlight();
    }

    public void SetPressed(bool value)
    {
        if (_pressed == value) return;
        _pressed = value;
        var milliseconds = value ? 200 : 280;
        Animate(_scale, ScaleTransform.ScaleXProperty, value ? 0.8 : 1, milliseconds);
        Animate(_scale, ScaleTransform.ScaleYProperty, value ? 0.8 : 1, milliseconds);
        for (var i = 0; i < _normal.Length; i++)
        {
            var normal = _normal[i];
            var target = value ? Color.FromRgb(0, 158, 215) : normal;
            var stop = _fill.GradientStops[i];
            var current = stop.Color;
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = target;
            if (AnimationsEnabled)
                stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
                { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private void UpdateHighlight() => Animate(_hover, UIElement.OpacityProperty, HighlightVisible ? 1 : 0, HighlightVisible ? 80 : 140);

    private static void Animate(Animatable target, DependencyProperty property, double value, int milliseconds)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (AnimationsEnabled)
            target.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Animate(Border target, DependencyProperty property, double value, int milliseconds)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (AnimationsEnabled)
            target.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
            { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
    }

    public void Reset()
    {
        _mouseOver = _menuOpen = _pressed = false;
        _hover.BeginAnimation(UIElement.OpacityProperty, null);
        _hover.Opacity = 0;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        for (var i = 0; i < _normal.Length; i++)
        {
            _fill.GradientStops[i].BeginAnimation(GradientStop.ColorProperty, null);
            _fill.GradientStops[i].Color = _normal[i];
        }
    }
}
