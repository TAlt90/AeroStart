using AeroStart.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AeroStart;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private HwndSource? _source;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        AppearanceService.Apply(this, null);
        ReadSettings();
        StatusText.Text = settings.LoadWarning ?? ((App)Application.Current).KeyboardStatus;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            AppearanceService.Apply(this, _source);
        };
        _settings.Changed += SettingsChanged; ((App)Application.Current).KeyboardStatusChanged += KeyboardChanged;
        ((App)Application.Current).TaskbarStatusChanged += TaskbarChanged;
        TaskbarChanged(this, EventArgs.Empty);
        Closed += (_, _) => { ((App)Application.Current).TaskbarStatusChanged -= TaskbarChanged; _settings.Changed -= SettingsChanged; ((App)Application.Current).KeyboardStatusChanged -= KeyboardChanged; _source?.RemoveHook(WndProc); };
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x031A or 0x031E or 0x0320)
            Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) AppearanceService.Apply(this, _source); }));
        return IntPtr.Zero;
    }

    private void SettingsChanged(object? sender, EventArgs e) => ReadSettings();

    private void KeyboardChanged(object? sender, EventArgs e) => StatusText.Text = ((App)Application.Current).KeyboardStatus;

    private void TaskbarChanged(object? sender, EventArgs e) => TaskbarStatusText.Text = ((App)Application.Current).TaskbarStatus;

    private void ReadSettings()
    {
        var current = _settings.Current;
        AeroMenu.IsChecked = current.StartMenu == StartMenuKind.AeroStart;
        WindowsMenu.IsChecked = current.StartMenu == StartMenuKind.Windows;
        MouseMonitor.IsChecked = current.Monitor == KeyboardMonitor.Mouse;
        PrimaryMonitor.IsChecked = current.Monitor == KeyboardMonitor.Primary;
        ActiveMonitor.IsChecked = current.Monitor == KeyboardMonitor.ActiveWindow;
        AnimationOn.IsChecked = current.AnimateMenu;
        AnimationOff.IsChecked = !current.AnimateMenu;
        ToolsOn.IsChecked = current.ShowSystemTools;
        ToolsOff.IsChecked = !current.ShowSystemTools;
        StableKeyboard.IsChecked = current.KeyboardMode == KeyboardIntegrationMode.Stable;
        ExperimentalKeyboard.IsChecked = current.KeyboardMode == KeyboardIntegrationMode.Experimental;
        TaskbarButtonsOn.IsChecked = current.ReplaceStartButtons;
        TaskbarButtonsOff.IsChecked = !current.ReplaceStartButtons;
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        var category = (sender as Button)?.Tag as string;
        GeneralPage.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationPage.Visibility = category == "Integration" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = category == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPage.Visibility = category == "Features" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Save(_settings.Current with
            {
                StartMenu = WindowsMenu.IsChecked == true ? StartMenuKind.Windows : StartMenuKind.AeroStart,
                Monitor = PrimaryMonitor.IsChecked == true ? KeyboardMonitor.Primary : ActiveMonitor.IsChecked == true ? KeyboardMonitor.ActiveWindow : KeyboardMonitor.Mouse,
                AnimateMenu = AnimationOn.IsChecked == true,
                ShowSystemTools = ToolsOn.IsChecked == true,
                KeyboardMode = ExperimentalKeyboard.IsChecked == true ? KeyboardIntegrationMode.Experimental : KeyboardIntegrationMode.Stable,
                ReplaceStartButtons = TaskbarButtonsOn.IsChecked == true
            });
            StatusText.Text = "Gespeichert. " + ((App)Application.Current).KeyboardStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusText.Text = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }
}
