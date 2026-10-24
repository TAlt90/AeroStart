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

    private sealed class FailingAutostart : AutostartService
    {
        public bool FailRead { get; set; }
        public override bool IsEnabled() => FailRead ? throw new UnauthorizedAccessException("Test: Zugriff verweigert") : false;
        public override void SetEnabled(bool enabled) => throw new UnauthorizedAccessException("Test: Zugriff verweigert");
    }

    private static void CheckAutostart(string keyPath, string executable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
        var existed = key.GetValueNames().Contains("AeroStart", StringComparer.OrdinalIgnoreCase);
        var oldValue = key.GetValue("AeroStart", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
        var oldKind = existed ? key.GetValueKind("AeroStart") : Microsoft.Win32.RegistryValueKind.String;
        var others = key.GetValueNames().Where(name => !name.Equals("AeroStart", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(name => name, name => System.Text.Json.JsonSerializer.Serialize(new
            {
                Kind = key.GetValueKind(name), Value = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
            }));
        try
        {
            var service = new AutostartService(keyPath, executable);
            service.SetEnabled(false);
            Check(!service.IsEnabled(), "Autostart off when own value absent");
            service.SetEnabled(true);
            service.SetEnabled(true);
            Check(service.IsEnabled() && key.GetValueNames().Count(name => name.Equals("AeroStart", StringComparison.OrdinalIgnoreCase)) == 1,
                "Repeated enable creates exactly one autostart value");
            Check((string?)key.GetValue("AeroStart") == $"\"{Path.GetFullPath(executable)}\" --background"
                && key.GetValueKind("AeroStart") == Microsoft.Win32.RegistryValueKind.String, "Autostart executable quoted in REG_SZ");
            key.SetValue("AeroStart", "outdated command");
            service.SetEnabled(true);
            Check((string?)key.GetValue("AeroStart") == service.Command, "Enable updates only own stale value");
            service.SetEnabled(false);
            service.SetEnabled(false);
            Check(!service.IsEnabled(), "Repeated disable removes only own value");
            Check(others.All(pair => pair.Value == System.Text.Json.JsonSerializer.Serialize(new
            {
                Kind = key.GetValueKind(pair.Key), Value = key.GetValue(pair.Key, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
            })), "Other Run values unchanged");
        }
        finally
        {
            if (existed) key.SetValue("AeroStart", oldValue!, oldKind);
            else key.DeleteValue("AeroStart", false);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--backdrop-prototype"))
        {
            BackdropPrototype.Run();
            return;
        }
        if (args.Contains("--windows-pins"))
        {
            var snapshot = new WindowsStartPins().RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine("Windows export: " + (snapshot.Error ?? "OK") + "; pins=" + snapshot.Items.Count);
            var arcadia = snapshot.Items.FirstOrDefault(item => item.Name.Contains("Chronicles of Arcadia", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("Arcadia imported=" + (arcadia is not null));
            if (arcadia is not null)
            {
                Console.WriteLine("Arcadia path exists=" + File.Exists(arcadia.Path));
                var resolved = ProgramActions.Inspect(arcadia);
                Console.WriteLine("Arcadia existing EXE target resolved=" + (resolved.Administrator is not null));
                Check(ProgramActions.Entries([], [], snapshot.Items).Any(entry => entry.Item == arcadia && entry.IsWindowsPin), "Arcadia outside catalog");
                Console.WriteLine("Arcadia appears under Angeheftet without catalog; launch uses existing ShellExecute LNK path. Game not launched.");
            }
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "AeroStart-FeatureChecks-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, "{\"AnimateMenu\":false,\"StartMenu\":\"AeroStart\"}");
            var settings = new SettingsService(path);
            settings.Load();
            Check(!settings.Current.AcrylicEnabled, "Legacy settings keep acrylic disabled");
            Check(!settings.Current.ShowTaskManagerShortcut, "Legacy settings default task manager shortcut off");
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
            settings.Save(settings.Current with { AcrylicEnabled = true });
            loaded.Load();
            Check(loaded.Current.AcrylicEnabled && loaded.Current.StartMenuOpacity == 30, "Acrylic persistence preserves opacity");
            settings.Save(settings.Current with { AcrylicEnabled = false });
            loaded.Load();
            Check(!loaded.Current.AcrylicEnabled && loaded.Current.PinnedPrograms.SequenceEqual(pins), "Acrylic off persists without losing pins");
            foreach (var invalid in new[] { 29, 31, 101 })
            {
                try { settings.Save(settings.Current with { StartMenuOpacity = invalid }); throw new InvalidOperationException("Invalid opacity accepted"); }
                catch (ArgumentException) { }
            }
            Check(ProgramActions.TogglePin([id, id.ToLowerInvariant()], id).Length == 0, "Remove duplicate pin identities");
            var item = new StartItem("Sample", exe, "Tests");
            var importJson = System.Text.Json.JsonSerializer.Serialize(new { pinnedList = new object[] { new { desktopAppLink = exe }, new { packagedAppId = "Package_123!App" }, new { desktopAppLink = exe }, new { desktopAppLink = Path.Combine(root, "missing.lnk") } } });
            var imported = WindowsStartPins.Parse(importJson, new Dictionary<string, string> { ["Package_123!App"] = "Packaged sample" });
            Check(imported.Count == 2 && imported[0].Path == exe && imported[1].Name == "Packaged sample" && imported[1].Path == "shell:AppsFolder\\Package_123!App", "Import order/names/missing/dedup");
            Environment.SetEnvironmentVariable("AEROSTART_PIN_TEST", root);
            try
            {
                var expanded = WindowsStartPins.Parse("{\"pinnedList\":[{\"desktopAppLink\":\"%AEROSTART_PIN_TEST%\\\\Sample.exe\"}]}");
                Check(expanded.Count == 1 && expanded[0].Path == exe, "Environment variable expansion");
            }
            finally { Environment.SetEnvironmentVariable("AEROSTART_PIN_TEST", null); }
            var merged = ProgramActions.Entries([item], [id], imported);
            Check(merged.Count(entry => entry.Section == "Angeheftet") == 2 && merged.Count(entry => entry.Section == "Alle Programme") == 1 && merged[0].IsWindowsPin, "Windows/local merge retains alphabetical duplicate");
            var calls = 0;
            var completion = new TaskCompletionSource<IReadOnlyList<StartItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = new WindowsStartPins(() => { Interlocked.Increment(ref calls); return completion.Task; });
            var first = cache.RefreshAsync();
            Check(ReferenceEquals(first, cache.RefreshAsync()), "Single flight");
            completion.SetResult(imported);
            Check(first.GetAwaiter().GetResult().Items.Count == 2 && cache.RefreshAsync().GetAwaiter().GetResult().Items.Count == 2 && calls == 1, "Refresh cache");
            var attempts = 0;
            var fallback = new WindowsStartPins(() => ++attempts == 1 ? Task.FromResult(imported) : Task.FromException<IReadOnlyList<StartItem>>(new TimeoutException("test")), TimeSpan.Zero);
            fallback.RefreshAsync().GetAwaiter().GetResult();
            var failed = fallback.RefreshAsync().GetAwaiter().GetResult();
            Check(failed.Error is not null && failed.Items.Count == 2, "Last good fallback");
            Console.WriteLine("PASS: Windows pin parser, environment variables, package names, export order, merge/dedup, single-flight/cache and last-good timeout fallback.");
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
            var immediatePath = Path.Combine(root, "immediate.json");
            var immediate = new SettingsService(immediatePath);
            immediate.Save(new AppSettings { PinnedPrograms = pins, AcrylicEnabled = true });
            var originalData = File.ReadAllText(immediatePath);
            var saves = 0;
            immediate.Changed += (_, _) => saves++;
            var immediateWindow = new AeroStart.SettingsWindow(immediate, new FailingAutostart());
            immediateWindow.Show();
            Check(saves == 0 && File.ReadAllText(immediatePath) == originalData, "Loading/showing settings never writes");
            var liveMenu = new AeroStart.MainWindow();
            immediate.Changed += (_, _) => liveMenu.ApplySettings(immediate.Current);
            void ClickSetting(string name, bool value)
            {
                var control = (System.Windows.Controls.Primitives.ToggleButton)immediateWindow.FindName(name);
                control.IsChecked = value;
                control.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            ClickSetting("WindowsMenu", true);
            Check(immediate.Current.StartMenu == StartMenuKind.Windows, "General saves immediately");
            ClickSetting("AeroMenu", true);
            ClickSetting("PrimaryMonitor", true);
            Check(immediate.Current.Monitor == KeyboardMonitor.Primary, "Monitor saves immediately");
            ClickSetting("ActiveMonitor", true);
            ClickSetting("MouseMonitor", true);
            Check(immediate.Current.Monitor == KeyboardMonitor.Mouse, "All monitor choices save");
            ClickSetting("ExperimentalKeyboard", true);
            Check(immediate.Current.KeyboardMode == KeyboardIntegrationMode.Experimental, "Keyboard selection saves immediately");
            ClickSetting("StableKeyboard", true);
            ClickSetting("TaskbarButtonsOn", true);
            Check(immediate.Current.ReplaceStartButtons, "Taskbar setting saves without modifying placement code");
            ClickSetting("TaskbarButtonsOff", true);
            ClickSetting("AnimationToggle", false);
            Check(!immediate.Current.AnimateMenu, "Animation toggle preserves existing value semantics");
            ClickSetting("SystemToolsToggle", false);
            ClickSetting("TaskManagerShortcutToggle", true);
            Check(!immediate.Current.ShowSystemTools && immediate.Current.ShowTaskManagerShortcut, "Shortcut is independent of tools submenu");
            var quick = (System.Windows.Controls.Button)liveMenu.FindName("TaskManagerShortcutButton");
            Check(quick.Visibility == System.Windows.Visibility.Visible
                && ((System.Windows.Controls.Button)liveMenu.FindName("SystemToolsButton")).Visibility == System.Windows.Visibility.Collapsed,
                "Settings changes apply immediately to existing menu");
            var profile = (System.Windows.Controls.Button)liveMenu.FindName("ProfileButton");
            var header = (System.Windows.Controls.Grid)VisualTreeHelper.GetParent(profile);
            header.Measure(new System.Windows.Size(632, 592));
            header.Arrange(new System.Windows.Rect(0, 0, 632, 592));
            var profileBounds = profile.TransformToAncestor(header).TransformBounds(new System.Windows.Rect(profile.RenderSize));
            var quickBounds = quick.TransformToAncestor(header).TransformBounds(new System.Windows.Rect(quick.RenderSize));
            Check(!profileBounds.IntersectsWith(quickBounds) && liveMenu.Width == 650 && liveMenu.Height == 610, "Shortcut never overlaps profile or changes fixed window size");
            Check(SystemToolItem.CreateCatalog().Count(tool => tool.Name == "Task-Manager"
                && tool.Target == Path.Combine(Environment.SystemDirectory, "taskmgr.exe")) == 1, "Task manager always exists once in system tools catalog");
            ClickSetting("TaskManagerShortcutToggle", false);
            Check(quick.Visibility == System.Windows.Visibility.Collapsed, "Shortcut off hides icon");
            ((System.Windows.Controls.Slider)immediateWindow.FindName("MenuOpacitySlider")).Value = 45;
            var reread = new SettingsService(immediatePath);
            reread.Load();
            Check(reread.Current.StartMenuOpacity == 45 && !reread.Current.AnimateMenu
                && !reread.Current.ShowSystemTools && !reread.Current.ShowTaskManagerShortcut
                && reread.Current.PinnedPrograms.SequenceEqual(pins) && reread.Current.AcrylicEnabled, "All settings persist and unrelated saved values survive");
            var savedCount = saves;
            ClickSetting("StableKeyboard", true);
            immediateWindow.Hide();
            immediateWindow.Show();
            Check(saves == savedCount, "Unchanged clicks/reopening do not write");
            var appearance = (System.Windows.Controls.StackPanel)immediateWindow.FindName("AppearancePage");
            Check(((System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)appearance.Children[1]).Child).Children[0]).Text == "Design folgt Windows", "Windows theme card is first");
            immediateWindow.Close();
            liveMenu.Close();
            var blockedParent = Path.Combine(root, "blocked-ui");
            File.WriteAllText(blockedParent, "not a directory");
            var blockedSettings = new SettingsService(Path.Combine(blockedParent, "settings.json"));
            var blockedWindow = new AeroStart.SettingsWindow(blockedSettings, new FailingAutostart());
            blockedWindow.Show();
            var blockedToggle = (System.Windows.Controls.CheckBox)blockedWindow.FindName("TaskManagerShortcutToggle");
            blockedToggle.IsChecked = true;
            blockedToggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(blockedToggle.IsChecked == false && !blockedSettings.Current.ShowTaskManagerShortcut
                && ((System.Windows.Controls.TextBlock)blockedWindow.FindName("StatusText")).Text.Contains("fehlgeschlagen"), "Failed immediate save restores UI and reports failure");
            var blockedRadio = (System.Windows.Controls.RadioButton)blockedWindow.FindName("WindowsMenu");
            blockedRadio.IsChecked = true;
            blockedRadio.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(blockedRadio.IsChecked == false, "Failed radio save restores selection without recursive write");
            var blockedSlider = (System.Windows.Controls.Slider)blockedWindow.FindName("MenuOpacitySlider");
            blockedSlider.Value = 30;
            Check(blockedSlider.Value == 100 && blockedSettings.Current.StartMenuOpacity == 100, "Failed slider save restores persisted opacity");
            blockedWindow.Close();
            var autostartKey = @"Software\AeroStart.FeatureChecks\" + Guid.NewGuid().ToString("N");
            try
            {
                using (var sandbox = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(autostartKey)) sandbox.SetValue("OtherApp", "untouched");
                var autostartExe = Path.Combine(root, "Folder with spaces", "AeroStart.exe");
                CheckAutostart(autostartKey, autostartExe);
                var autostart = new AutostartService(autostartKey, autostartExe);
                var autostartWindow = new AeroStart.SettingsWindow(new SettingsService(Path.Combine(root, "autostart.json")), autostart);
                autostartWindow.Show();
                var toggle = (System.Windows.Controls.CheckBox)autostartWindow.FindName("AutostartToggle");
                Check(toggle.IsChecked == false, "Autostart UI defaults off");
                autostart.SetEnabled(true);
                autostartWindow.Hide();
                autostartWindow.Show();
                Check(toggle.IsChecked == true, "Reopening settings reads actual registry");
                toggle.IsChecked = false;
                toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!autostart.IsEnabled() && toggle.IsChecked == false, "Autostart UI disables registry value");
                toggle.IsChecked = true;
                toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(autostart.IsEnabled() && toggle.IsChecked == true, "Autostart UI enables registry value");
                autostartWindow.Close();
                var failing = new FailingAutostart();
                var failedWindow = new AeroStart.SettingsWindow(new SettingsService(Path.Combine(root, "autostart-fail.json")), failing);
                failedWindow.Show();
                var failedToggle = (System.Windows.Controls.CheckBox)failedWindow.FindName("AutostartToggle");
                failedToggle.IsChecked = true;
                failedToggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(failedToggle.IsChecked == false && ((System.Windows.Controls.TextBlock)failedWindow.FindName("AutostartStatusText")).Text.Contains("nicht geändert"), "Autostart failure restores real off state and explains error");
                failing.FailRead = true;
                failedWindow.Hide();
                failedWindow.Show();
                Check(failedToggle.IsChecked is null && !failedToggle.IsEnabled, "Unreadable autostart is unknown, not falsely enabled");
                failedWindow.Close();
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(autostartKey, false); }
            if (args.Contains("--autostart-live"))
            {
                var productExe = args[Array.IndexOf(args, "--autostart-live") + 1];
                Check(File.Exists(productExe), "Live autostart check uses existing product EXE");
                CheckAutostart(AutostartService.RunKeyPath, productExe);
                Console.WriteLine("PASS: actual HKCU Run enable/update/disable, one AeroStart value, other values unchanged; original own value restored.");
            }
#if RELEASE
            using (var keyboard = new KeyboardIntegrationService(() => { }))
            {
                var diagnosticFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var observe = typeof(KeyboardIntegrationService).GetMethod("ObserveWinDiagnostic", diagnosticFlags)!;
                for (var i = 0; i < 35; i++)
                {
                    observe.Invoke(keyboard, [0x5B, true, false, KeyboardGesture.None]);
                    observe.Invoke(keyboard, [0x5B, false, false, KeyboardGesture.WindowsReleased]);
                }
                var diagnostic = KeyboardIntegrationService.WindowsKeyDebugLog;
                Check(diagnostic.Length == 30 && diagnostic.All(entry => entry.KeyUp >= entry.KeyDown
                    && entry.UpDecision == KeyboardGesture.WindowsReleased && entry.DownToUpMs >= 0), "Win diagnostic retains only last 30 completed gestures and timing");
                var last = diagnostic[^1];
                foreach (var key in new[] { 0x45, 0x52, 0x44 })
                {
                    observe.Invoke(keyboard, [0x5B, true, false, KeyboardGesture.None]);
                    observe.Invoke(keyboard, [key, true, false, KeyboardGesture.None]);
                    observe.Invoke(keyboard, [key, false, false, KeyboardGesture.None]);
                    observe.Invoke(keyboard, [0x5B, false, false, KeyboardGesture.None]);
                }
                Check(ReferenceEquals(KeyboardIntegrationService.WindowsKeyDebugLog[^1], last), "Win+E/R/D are not logged");
                typeof(KeyboardIntegrationService).GetMethod("RecordSendInput", diagnosticFlags)!.Invoke(keyboard, [3u, false]);
                observe.Invoke(keyboard, [0x5B, true, false, KeyboardGesture.None]);
                observe.Invoke(keyboard, [0x5B, false, false, KeyboardGesture.WindowsReleased]);
                typeof(KeyboardIntegrationService).GetMethod("RecordSendInput", diagnosticFlags)!.Invoke(keyboard, [3u, false]);
                typeof(KeyboardIntegrationService).GetMethod("RecordNativeWinTime", diagnosticFlags)!.Invoke(keyboard,
                    [0x5B, false, unchecked((uint)Environment.TickCount - 25)]);
                var completed = KeyboardIntegrationService.WindowsKeyDebugLog[^1];
                typeof(KeyboardIntegrationService).GetMethod("RecordMenuQueued", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [completed]);
                typeof(KeyboardIntegrationService).GetMethod("RecordMenuDispatch", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [completed, true]);
                Check(completed.SendInputResult == 3 && completed.MenuCallbackExecuted && completed.MenuDispatchTime is not null, "Win diagnostic records SendInput and actual dispatch branch");
                Check(completed.UpEventAgeMs >= 25 && completed.MenuQueueDelayMs >= 0, "Release diagnostic records native event age and dispatcher delay");
                Check(keyboard.GetWindowsKeyDiagnosticText().Contains("kein Funktionsnachweis"), "Snapshot does not claim hook handle proves health");
            }
            typeof(AeroStart.App).GetMethod("ShowKeyboardDiagnostic", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(app, null);
            var diagnosticWindow = (System.Windows.Window)typeof(AeroStart.App).GetField("_keyboardDiagnosticWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(app)!;
            Check(diagnosticWindow.IsVisible && diagnosticWindow.Title.Contains("Release"), "Release diagnostic opens without modal process pause");
            diagnosticWindow.Close();
#endif
            Check(MenuAcrylicService.AnimationAlpha(1) == 255 && MenuAcrylicService.AnimationAlpha(0) == 0
                && MenuAcrylicService.AnimationAlpha(0.5) == 128, "Acrylic alpha preserves shared fade and reaches 255");
            var modeSettings = new SettingsService(Path.Combine(root, "modes.json"));
            modeSettings.Save(new AppSettings { StartMenuOpacity = 35 });
            var settingsWindow = new AeroStart.SettingsWindow(modeSettings);
            var opacitySlider = (System.Windows.Controls.Slider)settingsWindow.FindName("MenuOpacitySlider");
            Check(opacitySlider.IsEnabled && opacitySlider.Value == 35, "Transparency slider restores saved value");
            modeSettings.Save(modeSettings.Current with { AcrylicEnabled = true });
            Check(opacitySlider.IsEnabled && opacitySlider.Value == 35 && modeSettings.Current.StartMenuOpacity == 35, "Disabled acrylic experiment leaves transparency slider enabled");
            var acrylicToggle = (System.Windows.Controls.CheckBox)settingsWindow.FindName("AcrylicToggle");
            Check(!acrylicToggle.IsEnabled && acrylicToggle.IsChecked == false, "Acrylic remains visibly unavailable despite saved on state");
            var persistedMode = new SettingsService(modeSettings.FilePath);
            persistedMode.Load();
            Check(persistedMode.Current.AcrylicEnabled && persistedMode.Current.StartMenuOpacity == 35, "Acrylic and retained transparency value persist together");
            modeSettings.Save(modeSettings.Current with { AcrylicEnabled = false });
            Check(opacitySlider.IsEnabled && opacitySlider.Value == 35, "Transparency re-enables slider with retained value");
            settingsWindow.Close();
            var menu = new AeroStart.MainWindow();
            var frame = (System.Windows.Controls.Border)menu.FindName("GlassFrame");
            var frameContent = (System.Windows.Controls.Grid)frame.Child;
            Check(frame.BorderThickness == new System.Windows.Thickness(1)
                && !frameContent.Children.OfType<System.Windows.Controls.Border>().Any(border => System.Windows.Controls.Grid.GetRowSpan(border) == 3 && border.BorderThickness != new System.Windows.Thickness(0)), "Exactly one outer menu contour");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var translation = (System.Windows.Media.TranslateTransform)frame.RenderTransform;
            var animate = typeof(AeroStart.MainWindow).GetMethod("AnimateMenu", flags)!;
            var complete = typeof(AeroStart.MainWindow).GetMethod("CompleteMenuAnimation", flags)!;
            var requestField = typeof(AeroStart.MainWindow).GetField("_animationRequest", flags)!;
            Check(menu.Background is SolidColorBrush { Color.A: 0 }
                && ((System.Windows.Controls.Grid)menu.Content).Background is SolidColorBrush { Color.A: 0 }
                && frame.Opacity == 0 && translation.Y == menu.Height, "Initial render state has no opaque outer background");
            for (var cycle = 0; cycle < 3; cycle++)
            {
                menu.ApplySettings(new AppSettings { AnimateMenu = false });
                animate.Invoke(menu, [true]);
                Check(frame.Opacity == 1 && translation.Y == 0 && !frame.HasAnimatedProperties,
                    "Disabled animation reaches fully visible state synchronously");
                animate.Invoke(menu, [false]);
                Check(frame.Opacity == 0 && translation.Y == menu.Height && !frame.HasAnimatedProperties,
                    "Closed state stays transparent and translated instead of resetting visible");
            }
            menu.ApplySettings(new AppSettings { AnimateMenu = true });
            animate.Invoke(menu, [true]);
            var openingRequest = (int)requestField.GetValue(menu)!;
            animate.Invoke(menu, [false]);
            var closingRequest = (int)requestField.GetValue(menu)!;
            complete.Invoke(menu, [true, openingRequest]);
            Check((int)requestField.GetValue(menu)! == closingRequest, "Old opening completion cannot replace closing request");
            animate.Invoke(menu, [true]);
            var reopenedRequest = (int)requestField.GetValue(menu)!;
            complete.Invoke(menu, [false, closingRequest]);
            Check((int)requestField.GetValue(menu)! == reopenedRequest, "Old close completion cannot cancel rapid reopen");
            complete.Invoke(menu, [true, reopenedRequest]);
            Check(frame.Opacity == 1 && translation.Y == 0 && !frame.HasAnimatedProperties,
                "Open completion preserves final visible state when removing clocks");
            animate.Invoke(menu, [false]);
            complete.Invoke(menu, [false, (int)requestField.GetValue(menu)!]);
            Check(frame.Opacity == 0 && translation.Y == menu.Height
                && (double)frame.GetAnimationBaseValue(System.Windows.UIElement.OpacityProperty) == 0,
                "Close completion leaves transparent base value after removing clocks");
            menu.ApplySettings(new AppSettings { AnimateMenu = true });
            translation.Y = menu.Height;
            frame.Opacity = 0;
            var resumePrepared = (Action?)null;
            animate.Invoke(menu, [true]);
            if (System.Windows.SystemParameters.ClientAreaAnimation && !System.Windows.SystemParameters.HighContrast)
                Check(frame.HasAnimatedProperties && translation.HasAnimatedProperties
                    && frame.Opacity == 0 && translation.Y == menu.Height,
                    "Opening clocks start at exact closed values");
            else Check(frame.Opacity == 1 && translation.Y == 0,
                "System-disabled animation completes immediately");
            animate.Invoke(menu, [false]);
            complete.Invoke(menu, [false, (int)requestField.GetValue(menu)!]);
            resumePrepared?.Invoke();
            Check(frame.Opacity == 0 && translation.Y == menu.Height && !frame.HasAnimatedProperties,
                "Close completion removes clocks and leaves closed state");
            Check(typeof(AeroStart.MainWindow).GetFields(flags).All(field => !field.Name.StartsWith("_diagnostic", StringComparison.Ordinal)),
                "Temporary GlassFrame diagnosis switches removed");
            var actualItem = new StartItem("Executable", exe, "");
            typeof(AeroStart.MainWindow).GetField("_all", flags)!.SetValue(menu, new List<StartItem> { actualItem });
            menu.ApplySettings(new AppSettings { StartMenuOpacity = 30, PinnedPrograms = pins });
            Check(menu.Opacity == 1 && ((Brush)menu.Resources["PanelBrush"]).Opacity == (System.Windows.SystemParameters.HighContrast ? 1 : 0.3), "Actual WPF window foreground opacity untouched");
            var sidebar = (System.Windows.Controls.Border)menu.FindName("MenuSidebar");
            Check(sidebar.Background is SolidColorBrush { Color.A: 0 } && sidebar.Opacity == 1, "Sidebar uses common frame without additional tint");
            var programs = (System.Windows.Controls.ListBox)menu.FindName("ProgramsList");
            Check(programs.Items.Count == 2 && ((ProgramListEntry)programs.Items[0]).Section == "Angeheftet", "Actual grouped list updated immediately");
            var container = new System.Windows.Controls.ListBoxItem { DataContext = programs.Items[0] };
            var eventArgs = (System.Windows.Controls.ContextMenuEventArgs)Activator.CreateInstance(typeof(System.Windows.Controls.ContextMenuEventArgs), flags, null, [container, true], null)!;
            typeof(AeroStart.MainWindow).GetMethod("Program_ContextMenuOpening", flags)!.Invoke(menu, [container, eventArgs]);
            var context = (System.Windows.Controls.ContextMenu)typeof(AeroStart.MainWindow).GetField("_programContextMenu", flags)!.GetValue(menu)!;
            Check(context.Items.Cast<System.Windows.Controls.MenuItem>().Select(option => (string)option.Header).SequenceEqual(new[] { "Von Start lösen", "Als Administrator ausführen", "Dateispeicherort öffnen" }), "Actual context actions and pin label");
            context.IsOpen = false;
            menu.ApplySettings(new AppSettings());
            Check(sidebar.Background is SolidColorBrush { Color.A: 0 }, "Sidebar remains untinted at full menu opacity");
            Check(programs.Items.Count == 1, "Unpin update retains ordinary entry");
            Check(typeof(AeroStart.MainWindow).GetField("_acrylic", flags)!.GetValue(menu) is null, "Default off creates no acrylic window");
            _ = new System.Windows.Interop.WindowInteropHelper(menu).EnsureHandle();
            menu.ApplySettings(new AppSettings { AcrylicEnabled = true });
            Check(typeof(AeroStart.MainWindow).GetField("_acrylic", flags)!.GetValue(menu) is null, "Saved acrylic on never creates host");
            menu.ApplySettings(new AppSettings { AcrylicEnabled = false });
            Check(typeof(AeroStart.MainWindow).GetField("_acrylic", flags)!.GetValue(menu) is null, "Acrylic HWND released on disable");
            Check(((Brush)menu.Resources["PanelBrush"]).Opacity == 1, "Disabling acrylic restores old palette");
            menu.ApplySettings(new AppSettings { AcrylicEnabled = true, StartMenuOpacity = 30 });
            var acrylicPanelOpacity = ((Brush)menu.Resources["PanelBrush"]).Opacity;
            var acrylicFrameOpacity = ((Brush)menu.Resources["WindowBackgroundBrush"]).Opacity;
            menu.ApplySettings(new AppSettings { AcrylicEnabled = true, StartMenuOpacity = 100 });
            Check(acrylicPanelOpacity == (System.Windows.SystemParameters.HighContrast ? 1 : 0.3)
                && acrylicFrameOpacity == (System.Windows.SystemParameters.HighContrast ? 1 : 0.3)
                && ((Brush)menu.Resources["PanelBrush"]).Opacity == 1
                && ((Brush)menu.Resources["WindowBackgroundBrush"]).Opacity == 1, "Transparency follows 30 and 100 even with saved acrylic on");
            menu.ApplySettings(new AppSettings { AcrylicEnabled = false, StartMenuOpacity = 30 });
            Check(typeof(AeroStart.MainWindow).GetField("_acrylic", flags)!.GetValue(menu) is null
                && ((Brush)menu.Resources["PanelBrush"]).Opacity == (System.Windows.SystemParameters.HighContrast ? 1 : 0.3), "Transparency mode restores old opacity and has no acrylic background");
            Thread.Sleep(300);
            menu.ApplySettings(new AppSettings { AcrylicEnabled = true });
            Check(typeof(AeroStart.MainWindow).GetField("_acrylic", flags)!.GetValue(menu) is null, "Repeated saved acrylic on remains unavailable");
            menu.ApplySettings(new AppSettings { AcrylicEnabled = false });
            menu.Close();
            app.Shutdown();
            Console.WriteLine("PASS: legacy settings, opacity/pin persistence, validation/write errors, pin identity/dedup/missing files, context availability, runas arguments/directory/UAC cancellation, Explorer quoting, foreground/brush isolation and high contrast.");
            Console.WriteLine("No programs, Explorer, UAC prompts or shell hooks started.");
        }
        finally { Directory.Delete(root, true); }
    }
}
