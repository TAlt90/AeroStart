using AeroStart.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AeroStart;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly AutostartService _autostart;
    private HwndSource? _source;
    private bool _readingSettings;

    public SettingsWindow(SettingsService settings) : this(settings, new AutostartService()) { }

    internal SettingsWindow(SettingsService settings, AutostartService autostart)
    {
        _settings = settings;
        _autostart = autostart;
        InitializeComponent();
        AppearanceService.Apply(this, null);
        ReadSettings();
        IsVisibleChanged += (_, _) => { if (IsVisible) ReadAutostart(); };
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
        _readingSettings = true;
        try
        {
        var current = _settings.Current;
        MenuOpacitySlider.Value = current.StartMenuOpacity;
        MenuOpacitySlider.IsEnabled = true;
        AcrylicToggle.IsChecked = false;
        AeroMenu.IsChecked = current.StartMenu == StartMenuKind.AeroStart;
        WindowsMenu.IsChecked = current.StartMenu == StartMenuKind.Windows;
        MouseMonitor.IsChecked = current.Monitor == KeyboardMonitor.Mouse;
        PrimaryMonitor.IsChecked = current.Monitor == KeyboardMonitor.Primary;
        ActiveMonitor.IsChecked = current.Monitor == KeyboardMonitor.ActiveWindow;
        AnimationToggle.IsChecked = current.AnimateMenu;
        SystemToolsToggle.IsChecked = current.ShowSystemTools;
        TaskManagerShortcutToggle.IsChecked = current.ShowTaskManagerShortcut;
        StableKeyboard.IsChecked = current.KeyboardMode == KeyboardIntegrationMode.Stable;
        ExperimentalKeyboard.IsChecked = current.KeyboardMode == KeyboardIntegrationMode.Experimental;
        TaskbarButtonsOn.IsChecked = current.ReplaceStartButtons;
        TaskbarButtonsOff.IsChecked = !current.ReplaceStartButtons;
        }
        finally { _readingSettings = false; }
    }

    private void MenuOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_readingSettings || !IsLoaded) return;
        SaveSettingsFromControls();
    }

    private void Acrylic_Changed(object sender, RoutedEventArgs e)
    {
        if (!MainWindow.AcrylicAvailable || _readingSettings || !IsLoaded) return;
        try { _settings.Save(_settings.Current with { AcrylicEnabled = AcrylicToggle.IsChecked == true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { StatusText.Text = "Speichern fehlgeschlagen: " + ex.Message; ReadSettings(); }
    }

    private bool ReadAutostart()
    {
        try
        {
            AutostartToggle.IsChecked = _autostart.IsEnabled();
            AutostartToggle.IsEnabled = true;
            AutostartStatusText.Text = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AutostartToggle.IsChecked = null;
            AutostartToggle.IsEnabled = false;
            AutostartStatusText.Text = "Autostartzustand konnte nicht gelesen werden: " + ex.Message;
            return false;
        }
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _autostart.SetEnabled(AutostartToggle.IsChecked == true);
            ReadAutostart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var error = "Autostart konnte nicht geändert werden: " + ex.Message;
            ReadAutostart();
            AutostartStatusText.Text = error + " " + AutostartStatusText.Text;
        }
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        var category = (sender as Button)?.Tag as string;
        GeneralPage.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationPage.Visibility = category == "Integration" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = category == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPage.Visibility = category == "Features" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsControl_Click(object sender, RoutedEventArgs e)
    {
        if (_readingSettings || !IsLoaded) return;
        SaveSettingsFromControls();
    }

    private void SaveSettingsFromControls()
    {
        try
        {
            var updated = _settings.Current with
            {
                StartMenu = WindowsMenu.IsChecked == true ? StartMenuKind.Windows : StartMenuKind.AeroStart,
                Monitor = PrimaryMonitor.IsChecked == true ? KeyboardMonitor.Primary : ActiveMonitor.IsChecked == true ? KeyboardMonitor.ActiveWindow : KeyboardMonitor.Mouse,
                AnimateMenu = AnimationToggle.IsChecked == true,
                ShowSystemTools = SystemToolsToggle.IsChecked == true,
                ShowTaskManagerShortcut = TaskManagerShortcutToggle.IsChecked == true,
                StartMenuOpacity = (int)(Math.Round(MenuOpacitySlider.Value / 5) * 5),
                KeyboardMode = ExperimentalKeyboard.IsChecked == true ? KeyboardIntegrationMode.Experimental : KeyboardIntegrationMode.Stable,
                ReplaceStartButtons = TaskbarButtonsOn.IsChecked == true
            };
            if (updated == _settings.Current) return;
            _settings.Save(updated);
            StatusText.Text = "Gespeichert. " + ((App)Application.Current).KeyboardStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ReadSettings();
            StatusText.Text = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }
}
