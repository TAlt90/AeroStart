using AeroStart.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace AeroStart;

public partial class MainWindow
{
    private void ProfileButton_Click(object sender, RoutedEventArgs e) => OpenAccountMenu();

    private void ProfileButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        OpenAccountMenu();
        e.Handled = true;
    }

    private void OpenAccountMenu()
    {
        if (_closed || _isHiding) return;
        _focusRequest++;
        CloseAllFlyouts();
        ProfileButton.Focus();
        var work = MenuPlacementService.GetWorkAreaSize(this);
        AccountMenuContent.MaxHeight = Math.Max(1, work.Height - 16);
        AccountMenuContent.Width = Math.Min(420, Math.Max(1, work.Width - 16));
        AccountMenu.IsOpen = true;
    }

    private void AccountMenu_Opened(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (AccountMenu.IsOpen && !_closed)
                AccountMenuContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }));
    }

    private void AccountMenu_Closed(object sender, EventArgs e)
    {
        if (_closingFlyouts || _closed || _isHiding) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closed || _closingFlyouts || _isHiding || !IsVisible || AccountMenu.IsOpen) return;
            if (!IsActive) HideMenu();
            else if (!IsKeyboardFocusWithin) ProfileButton.Focus();
        }));
    }

    private void AccountMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AccountMenu.IsOpen = false;
        Activate();
        ProfileButton.Focus();
        e.Handled = true;
    }

    private void ChangeProfilePicture_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:yourinfo");
    private void AccountSettings_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:accounts");
    private void ManageAccounts_Click(object sender, RoutedEventArgs e) => Launch(System.IO.Path.Combine(Environment.SystemDirectory, "netplwiz.exe"));

    private void MicrosoftAccount_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Launch("https://account.microsoft.com/");
        e.Handled = true;
    }
}
