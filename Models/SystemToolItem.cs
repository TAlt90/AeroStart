using AeroStart.Services;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace AeroStart.Models;
public sealed class SystemToolItem(string name, string target, params string[] iconPaths) : INotifyPropertyChanged
{
    private ImageSource _icon = ShellIconService.Fallback;
    private bool _iconRequested;

    public string Name { get; } = name;
    public string Target { get; } = target;
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
        foreach (var path in iconPaths)
        {
            var icon = await ShellIconService.Instance.GetAsync(path);
            if (ReferenceEquals(icon, ShellIconService.Fallback)) continue;
            _icon = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            return;
        }
    }

    public static IReadOnlyList<SystemToolItem> CreateCatalog()
    {
        var system = Environment.SystemDirectory;
        SystemToolItem Tool(string title, string file) => new(title, Path.Combine(system, file), Path.Combine(system, file));
        return new[]
        {
            Tool("Task-Manager", "taskmgr.exe"),
            Tool("Geräte-Manager", "devmgmt.msc"),
            Tool("Datenträgerverwaltung", "diskmgmt.msc"),
            Tool("Computerverwaltung", "compmgmt.msc"),
            Tool("Dienste", "services.msc"),
            Tool("Aufgabenplanung", "taskschd.msc"),
            Tool("Ereignisanzeige", "eventvwr.msc"),
            Tool("Leistungsüberwachung", "perfmon.exe"),
            Tool("Firewall", "wf.msc"),
            new SystemToolItem("Terminal", "wt.exe", @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe")),
            Tool("Netzwerkverbindungen", "ncpa.cpl")
        };
    }
}
