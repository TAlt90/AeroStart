using AeroStart.Models;
using AeroStart.Services;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

internal static class Program
{
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "AeroStart-FeatureChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{\"AnimateMenu\":false,\"StartMenu\":\"AeroStart\"}");
            var settings = new SettingsService(path);
            settings.Load();
            Check(settings.LoadWarning is null && settings.Current.StartMenuOpacity == 100 && settings.Current.PinnedPrograms.Length == 0 && !settings.Current.AnimateMenu, "Legacy settings defaults");
            var exe = Path.Combine(root, "Sample.exe");
            var url = Path.Combine(root, "Site.url");
            File.WriteAllText(exe, "test placeholder, never executed");
            File.WriteAllText(url, "[InternetShortcut]\nURL=https://example.invalid/");
            var id = ProgramActions.Identity(exe)!;
            var pins = ProgramActions.TogglePin([], id);
            var changed = 0;
            settings.Changed += (_, _) => changed++;
            settings.Save(settings.Current with { StartMenuOpacity = 30, PinnedPrograms = pins });
            var loaded = new SettingsService(path);
            loaded.Load();
            Check(changed == 1 && loaded.Current.StartMenuOpacity == 30 && loaded.Current.PinnedPrograms.SequenceEqual(pins), "Persistence/live change event");
            foreach (var invalid in new[] { 29, 31, 101 })
            {
                try { settings.Save(settings.Current with { StartMenuOpacity = invalid }); throw new InvalidOperationException("Invalid opacity accepted"); }
                catch (ArgumentException) { }
            }
            Check(ProgramActions.TogglePin([id, id.ToLowerInvariant()], id).Length == 0, "Remove duplicate pin identities");
            var item = new StartItem("Sample", exe, "Tests");
            var entries = ProgramActions.Entries([item, item], [id, id]);
            Check(entries.Count(entry => entry.Section == "Angeheftet") == 1 && entries.Count(entry => entry.Section == "Alle Programme") == 2, "Pin dedup and ordinary list preserved");
            var capabilities = ProgramActions.Inspect(item);
            Check(capabilities.Identity == id && capabilities.Administrator?.Verb == "runas" && capabilities.Location?.Arguments == "/select,\"" + exe + "\"", "EXE action availability and explorer quoting");
            var admin = ProgramActions.CreateAdministratorInfo(exe, "--sample \"argument value\"", root)!;
            Check(admin.UseShellExecute && admin.Arguments == "--sample \"argument value\"" && admin.WorkingDirectory == root, "Elevation arguments and working directory");
            object? shell = null, shortcut = null;
            var link = Path.Combine(root, "Sample.lnk");
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                shortcut = ((dynamic)shell!).CreateShortcut(link);
                ((dynamic)shortcut).TargetPath = exe;
                ((dynamic)shortcut).Arguments = admin.Arguments;
                ((dynamic)shortcut).WorkingDirectory = root;
                ((dynamic)shortcut).Save();
            }
            finally
            {
                if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            var linkActions = ProgramActions.Inspect(new StartItem("Link", link, ""));
            Check(linkActions.Administrator?.FileName == exe && linkActions.Administrator.Arguments == admin.Arguments
                && linkActions.Administrator.WorkingDirectory == root && linkActions.Location!.Arguments.Contains(link), "Actual LNK target/arguments/directory and preferred link location");
            var web = ProgramActions.Inspect(new StartItem("Site", url, ""));
            Check(web.Administrator is null && web.Location is not null && web.Identity is not null, "URL no elevation but shortcut location");
            Check(ProgramActions.Inspect(new StartItem("App", "shell:AppsFolder\\Package!App", "")).Location is null, "Packaged identity not sent to Explorer");
            Check(ProgramActions.IsCancellation(new Win32Exception(1223)) && !ProgramActions.IsCancellation(new Win32Exception(5)), "UAC cancellation classification");
            File.Delete(exe);
            Check(ProgramActions.Entries([item], pins).All(entry => entry.Section != "Angeheftet") && ProgramActions.Inspect(item).Location is null, "Missing file safely suppressed");
            File.WriteAllText(path, "{\"PinnedPrograms\":null}");
            loaded.Load();
            Check(loaded.LoadWarning is not null, "Malformed pins reported");
            File.WriteAllText(path, "{broken");
            new SettingsService(path).Load();
            var blocked = new SettingsService(Path.Combine(exe, "settings.json"));
            File.WriteAllText(exe, "not a directory");
            try { blocked.Save(new AppSettings()); throw new InvalidOperationException("Write error swallowed"); }
            catch (IOException) { }
            foreach (var highContrast in new[] { false, true })
            {
                var palette = AppearanceService.CreatePalette(true, false, highContrast);
                var foreground = palette["ForegroundBrush"];
                var border = palette["BorderBrush"];
                AppearanceService.ApplyBackgroundOpacity(palette, 30, highContrast);
                Check(ReferenceEquals(foreground, palette["ForegroundBrush"]) && ReferenceEquals(border, palette["BorderBrush"]), "Foreground and borders unchanged");
                Check(Math.Abs(((Brush)palette["PopupBrush"]).Opacity - (highContrast ? 1 : 0.3)) < 0.0001, "Popup opacity and high contrast protection");
                var independent = AppearanceService.CreatePalette(true, false, highContrast);
                Check(((Brush)independent["PanelBrush"]).Opacity == 1, "Other windows isolated");
            }
            var app = new AeroStart.App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var menu = new AeroStart.MainWindow();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var actualItem = new StartItem("Executable", exe, "");
            typeof(AeroStart.MainWindow).GetField("_all", flags)!.SetValue(menu, new List<StartItem> { actualItem });
            menu.ApplySettings(new AppSettings { StartMenuOpacity = 30, PinnedPrograms = pins });
            Check(menu.Opacity == 1 && ((Brush)menu.Resources["PanelBrush"]).Opacity == (System.Windows.SystemParameters.HighContrast ? 1 : 0.3), "Actual WPF window foreground opacity untouched");
            var programs = (System.Windows.Controls.ListBox)menu.FindName("ProgramsList");
            Check(programs.Items.Count == 2 && ((ProgramListEntry)programs.Items[0]).Section == "Angeheftet", "Actual grouped list updated immediately");
            var container = new System.Windows.Controls.ListBoxItem { DataContext = programs.Items[0] };
            var eventArgs = (System.Windows.Controls.ContextMenuEventArgs)Activator.CreateInstance(typeof(System.Windows.Controls.ContextMenuEventArgs), flags, null, [container, true], null)!;
            typeof(AeroStart.MainWindow).GetMethod("Program_ContextMenuOpening", flags)!.Invoke(menu, [container, eventArgs]);
            var context = (System.Windows.Controls.ContextMenu)typeof(AeroStart.MainWindow).GetField("_programContextMenu", flags)!.GetValue(menu)!;
            Check(context.Items.Cast<System.Windows.Controls.MenuItem>().Select(option => (string)option.Header).SequenceEqual(new[] { "Von Start lösen", "Als Administrator ausführen", "Dateispeicherort öffnen" }), "Actual context actions and pin label");
            context.IsOpen = false;
            menu.ApplySettings(new AppSettings());
            Check(programs.Items.Count == 1, "Unpin update retains ordinary entry");
            menu.Close();
            app.Shutdown();
            Console.WriteLine("PASS: legacy settings, opacity/pin persistence, validation/write errors, pin identity/dedup/missing files, context availability, runas arguments/directory/UAC cancellation, Explorer quoting, foreground/brush isolation and high contrast.");
            Console.WriteLine("No programs, Explorer, UAC prompts or shell hooks started.");
        }
        finally { Directory.Delete(root, true); }
    }
}
