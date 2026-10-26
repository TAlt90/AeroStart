using AeroStart.Models;
using AeroStart.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AeroStart;
public partial class MainWindow : Window
{
    private List<StartItem> _all = new();
    private HwndSource? _source;
    private bool _allowHide = true;
    private bool _appearanceUpdatePending;
    private bool _closed;
    private bool _resettingSearch;
    private bool _closingFlyouts;
    private int _focusRequest;
    private readonly DispatcherTimer _systemToolsCloseTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(150) };

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public MainWindow()
    {
        InitializeComponent();
        GlassFrame.RenderTransform = _menuTranslation;
        _menuTranslation.Y = Height;
        GlassFrame.Opacity = 0;
        ApplyMenuAppearance();
        UserNameText.Text = "Benutzerkonto";
        UserProfileImage.Source = UserProfileService.Fallback;
        SystemToolsItems.ItemsSource = SystemToolItem.CreateCatalog();
        SystemToolsMenu.PlacementTarget = GlassFrame;
        SystemToolsMenu.CustomPopupPlacementCallback = PlaceSystemToolsMenu;
        _systemToolsCloseTimer.Tick += SystemToolsCloseTimer_Tick;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _focusRequest++;
            _animationRequest++;
            _isHiding = false;
            StopMenuAnimation();
            CloseAllFlyouts();
            ResetSearch();
        };
        PowerMenu.Closed += (_, _) =>
        {
            if (!_closingFlyouts && _allowHide && !IsActive) HideMenu();
        };
        PowerMenu.PreviewKeyDown += Window_PreviewKeyDown;
        if (SearchBox.ContextMenu is { } searchMenu)
        {
            searchMenu.Closed += (_, _) => { if (!_closingFlyouts && _allowHide && !IsActive) HideMenu(); };
            searchMenu.PreviewKeyDown += Window_PreviewKeyDown;
        }
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            ConfigureAcrylic();
            ApplyMenuAppearance();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _acrylic?.Dispose();
            _acrylic = null;
            _focusRequest++;
            _animationRequest++;
            StopMenuAnimation();
            CloseAllFlyouts();
            ResetSearch();
            _systemToolsCloseTimer.Tick -= SystemToolsCloseTimer_Tick;
            if (_source != null) _source.RemoveHook(WndProc);
        };
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_SETTINGCHANGE, WM_THEMECHANGED, WM_DWMCOMPOSITIONCHANGED, WM_DWMCOLORIZATIONCOLORCHANGED.
        if (msg is 0x001A or 0x031A or 0x031E or 0x0320) QueueAppearanceUpdate();
        if (msg is 0x0047 or 0x02E0) _acrylic?.Sync(_menuSettings.StartMenuOpacity);
        if (msg == 0x001C && wParam == IntPtr.Zero)
        {
            if (_allowHide && _programContextMenu?.IsOpen != true) HideMenu();
        }
        return IntPtr.Zero;
    }
    private void QueueAppearanceUpdate()
    {
        if (_closed || _appearanceUpdatePending) return;
        _appearanceUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _appearanceUpdatePending = false;
            if (!_closed) { ConfigureAcrylic(); ApplyMenuAppearance(); }
        }));
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _all = await ProgramCatalog.GetAsync();
        if (_closed) return;
        if (!_menuRequested) ShowMenu();
        else ApplyFilter();
        var profile = await UserProfileService.LoadCurrentAsync();
        if (_closed) return;
        UserNameText.Text = profile.UserName;
        UserProfileImage.Source = profile.Picture;
        UserProfileImage.ToolTip = profile.HasAccountPicture ? "Windows-Kontobild" : "Kein lesbares Windows-Kontobild vorhanden";
        AccountEmailText.Text = profile.Email ?? string.Empty;
        AccountEmailText.Visibility = string.IsNullOrWhiteSpace(profile.Email) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FocusSearchBox()
    {
        var request = ++_focusRequest;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closed || _isHiding || !IsVisible || request != _focusRequest) return;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }));
    }
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_allowHide && !_closingFlyouts && _programContextMenu?.IsOpen != true && !AccountMenu.IsOpen && !SystemToolsMenu.IsOpen && !PowerMenu.IsOpen && SearchBox.ContextMenu?.IsOpen != true) HideMenu();
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isHiding) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            if (_programContextMenu?.IsOpen == true) { _programContextMenu.IsOpen = false; e.Handled = true; return; }
            if (AccountMenu.IsOpen)
            {
                AccountMenu.IsOpen = false;
                ProfileButton.Focus();
                e.Handled = true;
                return;
            }
            CloseAllFlyouts();
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                FocusSearchBox();
            }
            else HideMenu();
            e.Handled = true;
        }
        else if (SearchBox.IsKeyboardFocusWithin || ProgramsList.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Up or Key.Down)
            {
                NavigateResults(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                LaunchSelectedOrFirst();
                e.Handled = true;
            }
        }
    }
    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!SystemToolsMenu.IsOpen || SystemToolsButton.IsMouseOver || SystemToolsFlyoutContent.IsMouseOver) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != this;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not (ButtonBase or ListBoxItem)) continue;
            CloseSystemTools();
            break;
        }
    }
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SystemToolsMenu.IsOpen && !SystemToolsButton.IsMouseOver && !SystemToolsFlyoutContent.IsMouseOver)
            CloseSystemTools();
    }
    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseAllFlyouts();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_resettingSearch) return;
        CloseAllFlyouts();
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        if (ProgramsList is null || SearchBox is null) return;
        var results = ProgramActions.Entries(ProgramSearch.Filter(_all, SearchBox.Text), _menuSettings.PinnedPrograms,
            string.IsNullOrWhiteSpace(SearchBox.Text) ? _windowsPins : _windowsPins.Where(item => ProgramSearch.Filter([item], SearchBox.Text).Count > 0));
        var view = new System.Windows.Data.ListCollectionView(results);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ProgramListEntry.Section)));
        ProgramsList.ItemsSource = view;
        ProgramsList.SelectedIndex = IsVisible && !_isHiding && results.Count > 0 ? 0 : -1;
        if (results.Count > 0) ProgramsList.ScrollIntoView(results[0]);
    }
    private void ResetSearch()
    {
        _resettingSearch = true;
        try
        {
            SearchBox.Clear();
            SearchBox.Select(0, 0);
            ApplyFilter();
            ProgramsList.SelectedIndex = -1;
        }
        finally { _resettingSearch = false; }
    }
    private void NavigateResults(int direction)
    {
        if (ProgramsList.Items.Count == 0) return;
        var current = ProgramsList.SelectedIndex;
        ProgramsList.SelectedIndex = current < 0 ? 0 : Math.Clamp(current + direction, 0, ProgramsList.Items.Count - 1);
        ProgramsList.ScrollIntoView(ProgramsList.SelectedItem);
        if (ProgramsList.IsKeyboardFocusWithin)
        {
            ProgramsList.UpdateLayout();
            (ProgramsList.ItemContainerGenerator.ContainerFromItem(ProgramsList.SelectedItem) as ListBoxItem)?.Focus();
        }
    }
    private void ProgramsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element != null && element != ProgramsList;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not ListBoxItem) continue;
            LaunchSelectedOrFirst();
            e.Handled = true;
            break;
        }
    }
    private void LaunchSelectedOrFirst()
    {
        var item = ProgramsList.SelectedItem as ProgramListEntry ?? ProgramsList.Items.Cast<ProgramListEntry>().FirstOrDefault();
        if (item != null) Launch(item.Item.Path);
    }
    private void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); HideMenu(); }
        catch (Exception ex) { MessageBox.Show($"Konnte nicht gestartet werden:\n{ex.Message}", "AeroStart", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void LaunchCommand(string executable, string arguments)
    {
        try { Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true }); HideMenu(); }
        catch (Exception ex) { MessageBox.Show($"Konnte nicht gestartet werden:\n{ex.Message}", "AeroStart", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    private void UserFolder_Click(object sender, RoutedEventArgs e) => Launch(Folder(Environment.SpecialFolder.UserProfile));
    private void Documents_Click(object sender, RoutedEventArgs e) => Launch(Folder(Environment.SpecialFolder.MyDocuments));
    private void Pictures_Click(object sender, RoutedEventArgs e) => Launch(Folder(Environment.SpecialFolder.MyPictures));
    private void Computer_Click(object sender, RoutedEventArgs e) => Launch("explorer.exe");
    private void ControlPanel_Click(object sender, RoutedEventArgs e) => Launch("control.exe");
    private void Settings_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:");
    private void UninstallPrograms_Click(object sender, RoutedEventArgs e) => Launch(System.IO.Path.Combine(Environment.SystemDirectory, "appwiz.cpl"));
    private void SystemToolsButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _systemToolsCloseTimer.Stop();
        if (Mouse.LeftButton == MouseButtonState.Released) OpenSystemTools();
    }
    private void SystemToolsFlyout_MouseEnter(object sender, MouseEventArgs e) => _systemToolsCloseTimer.Stop();
    private void SystemToolsRegion_MouseLeave(object sender, MouseEventArgs e) => ScheduleSystemToolsClose();
    private void ScheduleSystemToolsClose()
    {
        _systemToolsCloseTimer.Stop();
        if (SystemToolsMenu.IsOpen && !SystemToolsButton.IsMouseOver && !SystemToolsFlyoutContent.IsMouseOver)
            _systemToolsCloseTimer.Start();
    }
    private void SystemToolsCloseTimer_Tick(object? sender, EventArgs e)
    {
        _systemToolsCloseTimer.Stop();
        if (!SystemToolsButton.IsMouseOver && !SystemToolsFlyoutContent.IsMouseOver) CloseSystemTools();
    }
    private void CloseSystemTools()
    {
        _systemToolsCloseTimer.Stop();
        SystemToolsMenu.IsOpen = false;
    }
    private void CloseAllFlyouts()
    {
        var closing = _closingFlyouts;
        _closingFlyouts = true;
        try
        {
            if (_programContextMenu is { }) _programContextMenu.IsOpen = false;
            if (AccountMenu is { }) AccountMenu.IsOpen = false;
            CloseSystemTools();
            PowerMenu.IsOpen = false;
            if (SearchBox.ContextMenu is { } editMenu) editMenu.IsOpen = false;
        }
        finally { _closingFlyouts = closing; }
    }
    private void SystemToolsButton_Click(object sender, RoutedEventArgs e) => OpenSystemTools(focusFirst: true);
    private void SystemToolsButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Right or Key.Down)) return;
        OpenSystemTools(focusFirst: true);
        e.Handled = true;
    }
    private void OpenSystemTools(bool focusFirst = false)
    {
        if (_closed || _isHiding || !IsVisible) return;
        if (AccountMenu.IsOpen) CloseAllFlyouts();
        _systemToolsCloseTimer.Stop();
        SystemToolsMenu.IsOpen = true;
        ScheduleSystemToolsClose();
        if (focusFirst)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!_closed && SystemToolsMenu.IsOpen)
                    SystemToolsFlyoutContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }));
    }
    private CustomPopupPlacement[] PlaceSystemToolsMenu(Size popupSize, Size targetSize, Point offset)
    {
        var buttonTop = SystemToolsButton.TranslatePoint(new Point(), GlassFrame).Y;
        var top = Math.Clamp(buttonTop, 0, Math.Max(0, targetSize.Height - popupSize.Height)) + offset.Y;
        // WPF chooses the candidate with the most visible screen area and constrains it to the monitor.
        return new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width + 4 + offset.X, top), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(-popupSize.Width - 4 + offset.X, top), PopupPrimaryAxis.Vertical)
        };
    }
    private void SystemToolsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Window_PreviewKeyDown(sender, e);
            return;
        }
        if (e.Key != Key.Left) return;
        CloseSystemTools();
        Activate();
        SystemToolsButton.Focus();
        e.Handled = true;
    }
    private void SystemToolsMenu_Closed(object sender, EventArgs e) => _systemToolsCloseTimer.Stop();
    private void SystemTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SystemToolItem tool }) return;
        CloseSystemTools();
        Launch(tool.Target);
        e.Handled = true;
    }
    private void TaskManagerShortcut_Click(object sender, RoutedEventArgs e) => Launch(System.IO.Path.Combine(Environment.SystemDirectory, "taskmgr.exe"));
    private void Run_Click(object sender, RoutedEventArgs e) => LaunchCommand("explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}");
    private void Lock_Click(object sender, RoutedEventArgs e) { HideMenu(); LockWorkStation(); }
    private bool ConfirmPower(string action)
    {
        var allowHide = _allowHide;
        _allowHide = false;
        try { return MessageBox.Show($"Computer wirklich {action}?", "AeroStart", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes; }
        finally { _allowHide = allowHide; }
    }
    private void Shutdown_Click(object sender, RoutedEventArgs e) { if (ConfirmPower("herunterfahren")) LaunchCommand("shutdown.exe", "/s /t 0"); }
    private void Restart_Click(object sender, RoutedEventArgs e) { if (ConfirmPower("neu starten")) LaunchCommand("shutdown.exe", "/r /t 0"); }
    private void SignOut_Click(object sender, RoutedEventArgs e) { if (ConfirmPower("abmelden")) LaunchCommand("shutdown.exe", "/l"); }
    private void Sleep_Click(object sender, RoutedEventArgs e) { if (ConfirmPower("in den Energiesparmodus versetzen")) { HideMenu(); if (!SetSuspendState(false, false, false)) MessageBox.Show("Energiesparmodus konnte nicht gestartet werden."); } }
    private void PowerDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        { CloseAllFlyouts(); menu.PlacementTarget = button; menu.IsOpen = true; }
    }
}
