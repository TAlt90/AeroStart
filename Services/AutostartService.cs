using Microsoft.Win32;
using System.IO;

namespace AeroStart.Services;

public class AutostartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "AeroStart";
    private readonly string _keyPath;
    private readonly string _executablePath;

    public AutostartService() : this(RunKeyPath, Environment.ProcessPath
        ?? throw new InvalidOperationException("Der AeroStart-Programmpfad konnte nicht ermittelt werden.")) { }

    internal AutostartService(string keyPath, string executablePath)
    {
        _keyPath = keyPath;
        _executablePath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"')
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Für den Autostart wird ein vollständiger EXE-Pfad benötigt.", nameof(executablePath));
    }

    internal string Command => $"\"{_executablePath}\" --background";

    public virtual bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase) == true;
    }

    public virtual void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            if (!string.Equals(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                Command, StringComparison.Ordinal) || key.GetValueKind(ValueName) != RegistryValueKind.String)
                throw new IOException("Der Autostarteintrag konnte nicht bestätigt werden.");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        if (IsEnabled() != enabled)
            throw new IOException("Der Autostartzustand konnte nach der Änderung nicht bestätigt werden.");
    }
}
