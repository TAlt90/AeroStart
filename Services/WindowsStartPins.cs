using AeroStart.Models;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AeroStart.Services;

internal sealed record WindowsPinSnapshot(IReadOnlyList<StartItem> Items, string? Error);

internal sealed class WindowsStartPins
{
    private readonly object _gate = new();
    private readonly Func<Task<IReadOnlyList<StartItem>>> _export;
    private readonly TimeSpan _interval;
    private Task<WindowsPinSnapshot>? _pending;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private WindowsPinSnapshot _cached = new([], null);

    internal WindowsStartPins(Func<Task<IReadOnlyList<StartItem>>>? export = null, TimeSpan? interval = null)
    {
        _export = export ?? ExportAsync;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    internal Task<WindowsPinSnapshot> RefreshAsync()
    {
        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) return _pending;
            if (DateTimeOffset.UtcNow - _lastAttempt < _interval) return Task.FromResult(_cached);
            _lastAttempt = DateTimeOffset.UtcNow;
            return _pending = Task.Run(async () =>
            {
                try
                {
                    var items = await _export().ConfigureAwait(false);
                    lock (_gate) return _cached = new(items, null);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    lock (_gate) return _cached = new(_cached.Items, "Windows-Pins konnten nicht aktualisiert werden: " + ex.Message);
                }
            });
        }
    }

    internal static IReadOnlyList<StartItem> Parse(string json, IReadOnlyDictionary<string, string>? names = null)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("pinnedList", out var pins) || pins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Der Windows-Export enthält keine pinnedList.");
        var result = new List<StartItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins.EnumerateArray())
        {
            if (pin.ValueKind != JsonValueKind.Object) continue;
            string? path = null, name = null;
            if (pin.TryGetProperty("desktopAppLink", out var link) && link.ValueKind == JsonValueKind.String)
            {
                var value = Environment.ExpandEnvironmentVariables(link.GetString()!);
                if (ProgramActions.Identity(value) is null || !File.Exists(value)) continue;
                path = Path.GetFullPath(value);
                name = Path.GetFileNameWithoutExtension(path);
            }
            else if (pin.TryGetProperty("packagedAppId", out var app) && app.ValueKind == JsonValueKind.String)
            {
                var id = app.GetString()!;
                if (string.IsNullOrWhiteSpace(id) || !id.Contains('!') || id.Any(char.IsControl) || id.IndexOfAny(['\\', '/', '"']) >= 0) continue;
                path = "shell:AppsFolder\\" + id;
                name = names is not null && names.TryGetValue(id, out var display) ? display : id;
            }
            if (path is not null && seen.Add(path)) result.Add(new StartItem(name!, path, "Windows-Start"));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<StartItem>> ExportAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AeroStart-Pins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var layout = Path.Combine(directory, "layout.json");
        var namesPath = Path.Combine(directory, "names.json");
        var quotedLayout = layout.Replace("'", "''");
        var quotedNames = namesPath.Replace("'", "''");
        var script = "$ErrorActionPreference='Stop'; Export-StartLayout -Path '" + quotedLayout + "'; " +
            "$j=Get-Content -LiteralPath '" + quotedLayout + "' -Raw|ConvertFrom-Json; $names=@{}; $shell=$null; " +
            "try { $shell=New-Object -ComObject Shell.Application; $folder=$shell.NameSpace('shell:AppsFolder'); " +
            "foreach($p in $j.pinnedList) { if($p.packagedAppId) { $i=$folder.ParseName([string]$p.packagedAppId); if($i) { $names[[string]$p.packagedAppId]=[string]$i.Name } } }; " +
            "$names|ConvertTo-Json -Compress|Set-Content -LiteralPath '" + quotedNames + "' -Encoding UTF8 } catch { } finally { if($shell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null } }";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("Exportprozess konnte nicht gestartet werden.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw new TimeoutException("Export-StartLayout hat das Zeitlimit von 10 Sekunden überschritten.");
            }
            if (process.ExitCode != 0 || !File.Exists(layout)) throw new IOException("Export-StartLayout ist fehlgeschlagen (Exitcode " + process.ExitCode + ").");
            if (new FileInfo(layout).Length > 2 * 1024 * 1024) throw new IOException("Windows-Pin-Export ist zu groß.");
            var names = File.Exists(namesPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(namesPath).ConfigureAwait(false)) : null;
            return Parse(await File.ReadAllTextAsync(layout).ConfigureAwait(false), names);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
