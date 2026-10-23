using AeroStart.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AeroStart;

public partial class MainWindow
{
    private AppSettings _menuSettings = new();
    private ContextMenu? _programContextMenu;

    private void ApplyMenuAppearance()
    {
        AppearanceService.Apply(this, _source, _menuSettings.StartMenuOpacity);
        var contrastProtection = !SystemParameters.HighContrast && _menuSettings.StartMenuOpacity < 100;
        var textColor = ((SolidColorBrush)Resources["ForegroundBrush"]).Color;
        var effect = contrastProtection ? new DropShadowEffect
        {
            Color = textColor.R + textColor.G + textColor.B > 384 ? Colors.Black : Colors.White,
            BlurRadius = 3, ShadowDepth = 0, Opacity = 1
        } : null;
        Resources[typeof(TextBlock)] = new Style(typeof(TextBlock))
        {
            Setters = { new Setter(TextBlock.EffectProperty, effect) }
        };
        if (_programContextMenu is { } menu)
            foreach (var key in Resources.Keys.Cast<object>().Where(key => key is string).ToArray()) menu.Resources[key] = Resources[key];
    }

    private void Program_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBoxItem container || container.DataContext is not ProgramListEntry entry) return;
        var capabilities = ProgramActions.Inspect(entry.Item);
        if (capabilities.Identity is null && capabilities.Location is null && capabilities.Administrator is null) return;
        CloseAllFlyouts();
        ProgramsList.SelectedItem = entry;
        var menu = new ContextMenu { PlacementTarget = container, Style = (Style)FindResource(typeof(ContextMenu)) };
        foreach (var key in Resources.Keys.Cast<object>().Where(key => key is string)) menu.Resources[key] = Resources[key];
        menu.Resources[typeof(MenuItem)] = FindResource(typeof(MenuItem));
        menu.Resources[typeof(TextBlock)] = Resources[typeof(TextBlock)];
        void Add(string header, Action action)
        {
            var option = new MenuItem { Header = header };
            option.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) when (ProgramActions.IsCancellation(ex)) { }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
                { MessageBox.Show(this, "Aktion konnte nicht ausgeführt werden:\n" + ex.Message, "AeroStart", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            menu.Items.Add(option);
        }
        if (!entry.IsWindowsPin && capabilities.Identity is { } identity)
        {
            var pinned = _menuSettings.PinnedPrograms.Any(path => StringComparer.OrdinalIgnoreCase.Equals(ProgramActions.Identity(path), identity));
            Add(pinned ? "Von Start lösen" : "An Start anheften", () =>
            {
                var settings = ((App)Application.Current).Settings;
                settings.Save(settings.Current with { PinnedPrograms = ProgramActions.TogglePin(settings.Current.PinnedPrograms, identity) });
            });
        }
        if (capabilities.Administrator is not null)
            Add("Als Administrator ausführen", () =>
            {
                var current = ProgramActions.Inspect(entry.Item).Administrator ?? throw new IOException("Das Programmziel ist nicht mehr verfügbar.");
                Process.Start(current);
                HideMenu();
            });
        if (capabilities.Location is not null)
            Add("Dateispeicherort öffnen", () =>
            {
                var current = ProgramActions.Inspect(entry.Item).Location ?? throw new IOException("Die Datei ist nicht mehr vorhanden.");
                Process.Start(current);
                HideMenu();
            });
        _programContextMenu = menu;
        menu.PreviewKeyDown += Window_PreviewKeyDown;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_programContextMenu, menu)) _programContextMenu = null;
            if (!_closingFlyouts && _allowHide && !IsActive) HideMenu();
        };
        menu.IsOpen = true;
    }
}
