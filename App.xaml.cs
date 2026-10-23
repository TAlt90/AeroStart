using AeroStart.Services;
using System.Windows;
namespace AeroStart;
public partial class App : Application
{
    private SingleInstanceService? _instance;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private MainWindow? _menu;
    private ShellIntegrationService? _shell;
    private KeyboardIntegrationService? _keyboard;
    private TaskbarIntegrationService? _taskbars;
    public string TaskbarStatus => _taskbars?.Status ?? "Startbutton-Overlays deaktiviert.";
    public event EventHandler? TaskbarStatusChanged;
    public string KeyboardStatus => _keyboard?.Status ?? "Tastaturintegration wird initialisiert.";
    public event EventHandler? KeyboardStatusChanged;
    private Task? _listener;
    public SettingsService Settings { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var command = e.Args.Contains("--diagnose-taskbar", StringComparer.OrdinalIgnoreCase) ? "diagnose-taskbar"
            : e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase) ? "background"
            : e.Args.Contains("--menu", StringComparer.OrdinalIgnoreCase) ? "menu" : "settings";
        try
        {
            _instance = new SingleInstanceService();
            if (!_instance.IsPrimary)
            {
                if (!await _instance.ForwardAsync(command))
                    MessageBox.Show("AeroStart läuft bereits, antwortet aber noch nicht. Bitte erneut versuchen.", "AeroStart");
                Shutdown();
                return;
            }
            Settings.Load();
            _shell = new ShellIntegrationService(Settings, GetMenu);
            _tray = new TrayService(() => InvokeMenu(false), ShowSettings, ToggleEnabled, Shutdown,
                () => Settings.Current.Enabled);
            _keyboard = new KeyboardIntegrationService(() => InvokeMenu(true));
            _keyboard.StatusChanged += KeyboardChanged;
            ConfigureKeyboard();
            _taskbars = new TaskbarIntegrationService(ToggleMenuAtAnchor);
            _taskbars.StatusChanged += TaskbarsChanged;
            ConfigureTaskbars();
            Settings.Changed += SettingsChanged;
            _listener = _instance.ListenAsync(value => Dispatcher.BeginInvoke(new Action(() => HandleCommand(value))));
            HandleCommand(command);
            if (_tray.Warning is { } warning) { ShowSettings(); MessageBox.Show(_settingsWindow!, warning, "AeroStart"); }
        }
        catch (Exception ex)
        {
            MessageBox.Show("AeroStart konnte nicht gestartet werden:\n" + ex.Message, "AeroStart", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private MainWindow GetMenu()
    {
        if (_menu != null) return _menu;
        _menu = new MainWindow();
        _menu.MenuOpenStateChanged += (_, _) => _taskbars?.SetMenuState(_menu);
        _menu.IsVisibleChanged += (_, _) => _taskbars?.SetMenuState(_menu);
        _menu.LocationChanged += (_, _) => _taskbars?.SetMenuState(_menu);
        _menu.Closed += (_, _) => { _menu = null; _taskbars?.SetMenuState(null); };
        _menu.ApplySettings(Settings.Current);
        return _menu;
    }

    private void HandleCommand(string command)
    {
        if (command == "settings") ShowSettings();
        else if (command == "menu") InvokeMenu(false);
        else if (command == "diagnose-taskbar") _taskbars?.Diagnose();
    }

    private void InvokeMenu(bool toggle)
    {
        try { _shell?.OpenMenu(toggle); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show("Startmenü konnte nicht geöffnet werden:\n" + ex.Message, "AeroStart"); }
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(Settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ToggleEnabled()
    {
        try { Settings.Save(Settings.Current with { Enabled = !Settings.Current.Enabled }); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { MessageBox.Show("Einstellung konnte nicht gespeichert werden:\n" + ex.Message, "AeroStart"); }
    }

    private void SettingsChanged(object? sender, EventArgs e)
    {
        ConfigureKeyboard();
        ConfigureTaskbars();
        _menu?.ApplySettings(Settings.Current);
        if (!Settings.Current.Enabled || Settings.Current.StartMenu == StartMenuKind.Windows) _menu?.DismissMenu();
        _tray?.Refresh();
    }

    private void KeyboardChanged(object? sender, EventArgs e) => KeyboardStatusChanged?.Invoke(this, EventArgs.Empty);

    private void TaskbarsChanged(object? sender, EventArgs e) => TaskbarStatusChanged?.Invoke(this, EventArgs.Empty);

    private void ConfigureTaskbars() => _taskbars?.Configure(Settings.Current.Enabled
        && Settings.Current.StartMenu == StartMenuKind.AeroStart && Settings.Current.ReplaceStartButtons);

    private void ToggleMenuAtAnchor(Rect anchor)
    {
        if (!Settings.Current.Enabled || Settings.Current.StartMenu != StartMenuKind.AeroStart) return;
        try
        {
            var menu = GetMenu();
            if (menu.IsMenuOpen && MenuPlacementService.IsOnSameMonitor(menu, new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2)))
                menu.DismissMenu();
            else menu.ShowAtAnchor(anchor);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show("Startmenü konnte nicht geöffnet werden:\n" + ex.Message, "AeroStart"); }
    }

    private void ConfigureKeyboard() => _keyboard?.Configure(Settings.Current.Enabled,
        Settings.Current.Enabled && Settings.Current.StartMenu == StartMenuKind.AeroStart
        && Settings.Current.KeyboardMode == KeyboardIntegrationMode.Experimental);

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Changed -= SettingsChanged;
        if (_taskbars != null) { _taskbars.StatusChanged -= TaskbarsChanged; _taskbars.Dispose(); }
        if (_keyboard != null) { _keyboard.StatusChanged -= KeyboardChanged; _keyboard.Dispose(); }
        _tray?.Dispose();
        _instance?.Dispose();
        ShellIconService.Instance.Dispose();
        base.OnExit(e);
    }
}
