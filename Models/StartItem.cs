using AeroStart.Services;
using System.ComponentModel;
using System.Windows.Media;

namespace AeroStart.Models;
public sealed class StartItem(string name, string path, string category) : INotifyPropertyChanged
{
    private ImageSource _icon = ShellIconService.Fallback;
    private bool _iconRequested;

    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Category { get; } = category;

    public ImageSource Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadIconAsync()
    {
        _icon = await ShellIconService.Instance.GetAsync(Path);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    public override string ToString() => Name;
}
