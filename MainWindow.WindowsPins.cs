using AeroStart.Models;
using AeroStart.Services;

namespace AeroStart;

public partial class MainWindow
{
    private readonly WindowsStartPins _windowsPinImporter = new();
    private IReadOnlyList<StartItem> _windowsPins = [];
    private bool _refreshingWindowsPins;

    private async Task RefreshWindowsPinsAsync()
    {
        if (_refreshingWindowsPins || _closed) return;
        _refreshingWindowsPins = true;
        try
        {
            var snapshot = await _windowsPinImporter.RefreshAsync();
            if (_closed) return;
            WindowsPinsStatus.Text = snapshot.Error ?? "";
            WindowsPinsStatus.Visibility = snapshot.Error is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            if (ReferenceEquals(_windowsPins, snapshot.Items)) return;
            var selected = ProgramsList.SelectedItem as ProgramListEntry;
            _windowsPins = snapshot.Items;
            if (_programContextMenu?.IsOpen == true) return;
            ApplyFilter();
            if (selected is not null)
            {
                var restored = ProgramsList.Items.Cast<ProgramListEntry>().FirstOrDefault(entry => entry.Section == selected.Section
                    && StringComparer.OrdinalIgnoreCase.Equals(entry.Item.Path, selected.Item.Path));
                if (restored is not null) ProgramsList.SelectedItem = restored;
            }
        }
        finally { _refreshingWindowsPins = false; }
    }
}
