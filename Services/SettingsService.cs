using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroStart.Services;

public enum StartMenuKind { AeroStart, Windows }
public enum KeyboardMonitor { Mouse, Primary, ActiveWindow }
public enum KeyboardIntegrationMode { Stable, Experimental }

public sealed record AppSettings
{
    public bool Enabled { get; init; } = true;
    public StartMenuKind StartMenu { get; init; } = StartMenuKind.AeroStart;
    public KeyboardMonitor Monitor { get; init; } = KeyboardMonitor.Mouse;
    public bool ShowSystemTools { get; init; } = true;
    public bool AnimateMenu { get; init; } = true;
    public bool AcrylicEnabled { get; init; }
    public bool ReplaceStartButtons { get; init; }
    public int StartMenuOpacity { get; init; } = 100;
    public string[] PinnedPrograms { get; init; } = [];
    public KeyboardIntegrationMode KeyboardMode { get; init; } = KeyboardIntegrationMode.Stable;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public SettingsService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroStart", "settings.json")) { }
    internal SettingsService(string filePath) => FilePath = filePath;
    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var info = new FileInfo(FilePath);
            if (info.Length > 64 * 1024) throw new InvalidDataException("Die Einstellungsdatei ist zu groß.");
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                ?? throw new InvalidDataException("Die Einstellungsdatei enthält keine Einstellungen.");
            Validate(settings);
            Current = settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
        {
            LoadWarning = $"Einstellungen konnten nicht geladen werden. Standardwerte werden verwendet. {ex.Message}";
        }
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        var data = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        if (data.Length > 64 * 1024) throw new ArgumentException("Die Einstellungsdatei ist zu groß.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.StartMenuOpacity is < 30 or > 100 || settings.StartMenuOpacity % 5 != 0)
            throw new ArgumentException("Die Startmenü-Deckkraft muss zwischen 30 und 100 Prozent in 5-Prozent-Schritten liegen.");
        if (settings.PinnedPrograms is null || settings.PinnedPrograms.Length > 256
            || settings.PinnedPrograms.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 1024))
            throw new ArgumentException("Ungültige Liste angehefteter Programme (höchstens 256 Einträge).");
        if (!Enum.IsDefined(settings.StartMenu) || !Enum.IsDefined(settings.Monitor) || !Enum.IsDefined(settings.KeyboardMode))
            throw new ArgumentException("Unbekannte Startmenü- oder Monitorauswahl.");
    }
}
