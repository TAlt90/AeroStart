using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new PairApp();
        });
    }
}

internal sealed class PairApp : Application
{
    private WindowPair? _pair;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var backdrop = new PairBackdrop();
        var behind = new Window { Title = "WinUI Desktop Acrylic – Hintergrund", SystemBackdrop = backdrop };
        behind.Content = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)behind.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        behind.AppWindow.IsShownInSwitchers = false;
        _pair = new WindowPair(behind, backdrop);
        _pair.Show();
    }
}

internal sealed class PairBackdrop : DesktopAcrylicBackdrop
{
    private SystemBackdropConfiguration? _configuration;
    private bool _inputActive;

    internal void SetPairActive(bool active)
    {
        _inputActive = active;
        if (_configuration is not null)
            _configuration.IsInputActive = active;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _configuration.IsInputActive = _inputActive;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _configuration.IsInputActive = _inputActive;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _configuration = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
