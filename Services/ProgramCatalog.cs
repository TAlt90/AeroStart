using AeroStart.Models;
using System.IO;

namespace AeroStart.Services;
public static class ProgramCatalog
{
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
        // Keep identically named shortcuts if their targets or locations differ.
        return items.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
