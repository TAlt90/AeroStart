using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System.Power;
using Windows.UI.ViewManagement;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new CheckApp();
        });
    }
}

internal sealed class CheckApp : Application
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Unabhängiger WinUI-3 DesktopAcrylicBackdrop-Test" };
        _window.SystemBackdrop = new DesktopAcrylicBackdrop();
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20), Width = 390, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "WinUI 3 – Acrylic-Prüfung", FontSize = 24 });
        panel.Children.Add(new TextBlock
        {
            Text = "Kontrastmuster direkt hinter dieses Fenster legen. Rechts bleibt der Prüfbereich frei.\nEin: Sind bekannte Musterkanten sichtbar und gegenüber dem unbedeckten Muster verbreitert?\nAus: deckende Referenz, keine scharfe Durchsicht! Ein reiner Farbwechsel beweist keinen Blur.\nStarke Materialtönung kann die Kanten verdecken: dann ist der Test nicht eindeutig.",
            TextWrapping = TextWrapping.Wrap
        });
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var on = new Button { Content = "Mattglas Ein" };
        var off = new Button { Content = "Mattglas Aus" };
        panel.Children.Add(on); panel.Children.Add(off); panel.Children.Add(state);
        var sample = new Border
        {
            Height = 70, Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Child = new TextBlock { Text = "Teiltransparente Testfläche (Alpha 40/255)", Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap }
        };
        panel.Children.Add(sample);
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(panel);
        _window.Content = root;
        void Report(bool enabled)
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var transparency = personalize?.GetValue("EnableTransparency");
            var ui = new UISettings();
            var contrast = new AccessibilitySettings();
            state.Text = $"SystemBackdrop={(enabled ? "DesktopAcrylicBackdrop" : "null")}\nEnableTransparency={transparency ?? "nicht gesetzt"}; AdvancedEffectsEnabled={ui.AdvancedEffectsEnabled}\nEnergySaverStatus={PowerManager.EnergySaverStatus}; HighContrast={contrast.HighContrast}";
            Console.WriteLine(state.Text);
        }
        on.Click += (_, _) => { _window.SystemBackdrop = new DesktopAcrylicBackdrop(); Report(true); };
        off.Click += (_, _) => { _window.SystemBackdrop = null; Report(false); };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 660));
        _window.Activate();
        Report(true);
    }
}
