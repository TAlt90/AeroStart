using AeroStart.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AeroStart.Services;

internal sealed record ProgramCapabilities(string? Identity, ProcessStartInfo? Administrator, ProcessStartInfo? Location);

internal static class ProgramActions
{
    internal static bool IsPackaged(string path) => path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);

    internal static string? Identity(string path)
    {
        if (IsPackaged(path)) return path.ToUpperInvariant();
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).ToUpperInvariant() : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    internal static ProgramCapabilities Inspect(StartItem item)
    {
        var identity = Identity(item.Path);
        if (identity is not null && IsPackaged(item.Path)) return new(identity, null, null);
        if (identity is null || !File.Exists(item.Path)) return new(null, null, null);
        var location = new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true };
        var extension = Path.GetExtension(item.Path);
        var target = item.Path;
        var arguments = "";
        var directory = Path.GetDirectoryName(target) ?? "";
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, shortcut = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type is null) return new(identity, null, location);
                shell = Activator.CreateInstance(type);
                shortcut = ((dynamic)shell!).CreateShortcut(item.Path);
                target = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
                arguments = (string)((dynamic)shortcut).Arguments;
                directory = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).WorkingDirectory);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or System.Security.SecurityException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { return new(identity, null, location); }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        var admin = CreateAdministratorInfo(target, arguments, directory);
        return new(identity, admin, location);
    }

    internal static ProcessStartInfo? CreateAdministratorInfo(string target, string arguments, string directory)
    {
        if (Identity(target) is null || !File.Exists(target) || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new ProcessStartInfo(target)
        {
            UseShellExecute = true, Verb = "runas", Arguments = arguments,
            WorkingDirectory = Directory.Exists(directory) ? directory : Path.GetDirectoryName(target) ?? ""
        };
    }

    internal static bool IsCancellation(Exception ex) => ex is Win32Exception { NativeErrorCode: 1223 };

    internal static string[] TogglePin(IEnumerable<string> pins, string identity)
    {
        var normalized = pins.Select(Identity).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.RemoveAll(path => StringComparer.OrdinalIgnoreCase.Equals(path, identity)) > 0) return normalized.ToArray();
        normalized.Add(identity);
        if (normalized.Count > 256) throw new ArgumentException("Es können höchstens 256 Programme angeheftet werden.");
        return normalized.ToArray();
    }

    internal static List<ProgramListEntry> Entries(IEnumerable<StartItem> results, IEnumerable<string> pins, IEnumerable<StartItem>? windowsPins = null)
    {
        var items = results.ToList();
        var identities = pins.Select(Identity).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinned = items.Where(item => (IsPackaged(item.Path) || File.Exists(item.Path)) && identities.Contains(Identity(item.Path) ?? ""))
            .DistinctBy(item => Identity(item.Path), StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = (windowsPins ?? []).Where(item => seen.Add(Identity(item.Path) ?? item.Path))
            .Select(item => new ProgramListEntry(item, "Angeheftet", true));
        return imported.Concat(pinned.Where(item => seen.Add(Identity(item.Path) ?? item.Path)).Select(item => new ProgramListEntry(item, "Angeheftet")))
            .Concat(items.Select(item => new ProgramListEntry(item, "Alle Programme"))).ToList();
    }
}

internal sealed record ProgramListEntry(StartItem Item, string Section, bool IsWindowsPin = false)
{
    public string PinSource => Section != "Angeheftet" ? "" : IsWindowsPin ? "Windows-Start (nur gelesen)" : "Lokal in AeroStart";
    public string Name => Item.Name;
    public string Category => Item.Category;
    public System.Windows.Media.ImageSource Icon => Item.Icon;
}
