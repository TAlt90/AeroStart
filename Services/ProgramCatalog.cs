using AeroStart.Models;
using System.IO;

namespace AeroStart.Services;
public static class ProgramCatalog
{
    private static Task<List<StartItem>>? _preload;
    private static readonly object PreloadLock = new();
    private static DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>Liest den Katalog im Hintergrund neu ein (höchstens alle 30 s). Liefert null, wenn nichts zu tun ist.</summary>
    public static Task<List<StartItem>>? RefreshAsync()
    {
        lock (PreloadLock)
        {
            if (_preload is null || !_preload.IsCompleted || DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(30)) return null;
            return _preload = Start();
        }
    }

    private static Task<List<StartItem>> Start()
    {
        _lastRefresh = DateTime.UtcNow;
        var source = new TaskCompletionSource<List<StartItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { source.SetResult(Load()); }
            catch (Exception) { source.SetResult(new List<StartItem>()); }
        }) { IsBackground = true, Name = "AeroStart.ProgramCatalog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }

    /// <summary>Startet das Einlesen einmalig im Hintergrund (STA, wegen Shell.Application).</summary>
    public static Task<List<StartItem>> GetAsync()
    {
        lock (PreloadLock)
            return _preload ??= Start();
    }

    public static List<StartItem> Load()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };
        var items = new List<StartItem>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var ext = Path.GetExtension(file);
                        if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var category = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                        items.Add(new StartItem(name, file, category == "." ? "" : category));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        AddPackagedApps(items);
        // Keep identically named shortcuts if their targets or locations differ.
        return items.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Store/MSIX apps have no Start Menu shortcut; they are only listed in shell:AppsFolder (AppUserModelID contains '!').
    private static void AddPackagedApps(List<StartItem> items)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return;
            shell = Activator.CreateInstance(type);
            dynamic folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return;
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string id = item.Path;
                    string name = item.Name;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !id.Contains('!')) continue;
                    items.Add(new StartItem(name, "shell:AppsFolder\\" + id, "Microsoft Store"));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally
        {
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
