using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace AeroStart.Services;

public sealed class ShellIntegrationService
{
    private readonly SettingsService _settings;
    private readonly Func<MainWindow> _getMenu;
    private bool _windowsMenuPending;

    public ShellIntegrationService(SettingsService settings, Func<MainWindow> getMenu)
    {
        _settings = settings;
        _getMenu = getMenu;
    }

    public void OpenMenu(bool toggle = false)
    {
        var settings = _settings.Current;
        if (!settings.Enabled || settings.StartMenu == StartMenuKind.Windows)
        {
            OpenWindowsMenuWhenReleased();
            return;
        }
        var menu = _getMenu();
        if (toggle && menu.IsMenuOpen) { menu.DismissMenu(); return; }
        menu.ShowAtScreenPoint(GetInvocationPoint(settings.Monitor));
    }

    private static Point GetInvocationPoint(KeyboardMonitor choice)
    {
        if (choice == KeyboardMonitor.Primary) return new Point(0, 0);
        if (choice == KeyboardMonitor.ActiveWindow)
        {
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero && GetWindowRect(window, out var bounds))
                return new Point(bounds.Left + (bounds.Right - bounds.Left) / 2.0, bounds.Top + (bounds.Bottom - bounds.Top) / 2.0);
        }
        return MenuPlacementService.CursorPosition;
    }

    private async void OpenWindowsMenuWhenReleased()
    {
        if (_windowsMenuPending) return;
        _windowsMenuPending = true;
        try
        {
            for (var attempt = 0; ModifiersDown(); attempt++)
            {
                if (attempt >= 100) throw new InvalidOperationException("Bitte die Hotkey-Tasten loslassen und erneut versuchen.");
                await Task.Delay(20);
            }
            OpenWindowsMenu();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { MessageBox.Show(ex.Message, "AeroStart", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _windowsMenuPending = false; }
    }

    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static void OpenWindowsMenu()
    {
        var inputs = new[] { Key(0x11, false), Key(0x1B, false), Key(0x1B, true), Key(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows-Startmenü konnte nicht aufgerufen werden.");
    }

    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0 } } };
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
