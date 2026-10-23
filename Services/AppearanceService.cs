using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AeroStart.Services;
public static class AppearanceService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public static void Apply(Window window, HwndSource? source)
    {
        var highContrast = SystemParameters.HighContrast;
        var dark = ReadSetting(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
        var accentEnabled = ReadSetting(DwmKey, "ColorPrevalence", 0) != 0 || ReadSetting(PersonalizeKey, "ColorPrevalence", 0) != 0;
        var transparency = !dark && !highContrast && !SystemParameters.IsRemoteSession && ReadSetting(PersonalizeKey, "EnableTransparency", 1) != 0;
        var glass = !window.AllowsTransparency && source != null && ApplyDwm(source, dark, transparency);
        Color? accent = null;
        if (accentEnabled && DwmGetColorizationColor(out var color, out _) >= 0)
            accent = Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        var palette = CreatePalette(dark, glass, highContrast, accent);
        foreach (var key in palette.Keys) window.Resources[key] = palette[key];
        window.Background = window.AllowsTransparency || glass ? Brushes.Transparent : (Brush)palette["WindowBackgroundBrush"];
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = window.AllowsTransparency || glass ? Colors.Transparent : ((SolidColorBrush)palette["WindowBackgroundBrush"]).Color;
    }

    internal static ResourceDictionary CreatePalette(bool dark, bool glass, bool highContrast, Color? accentColor = null)
    {
        glass &= !dark && !highContrast;
        var background = dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(235, 237, 240);
        var panel = dark ? Color.FromRgb(36, 36, 36) : Color.FromRgb(250, 250, 251);
        var foreground = dark ? Color.FromRgb(242, 242, 242) : Color.FromRgb(29, 30, 32);
        var secondary = dark ? Color.FromRgb(168, 168, 168) : Color.FromRgb(91, 95, 102);
        var border = dark ? Color.FromRgb(52, 52, 52) : Color.FromRgb(166, 171, 178);
        var accent = accentColor ?? (dark ? Color.FromRgb(193, 198, 205) : Color.FromRgb(77, 84, 94));
        var resources = new ResourceDictionary();
        if (highContrast)
        {
            Set(resources, "WindowBackgroundBrush", SystemColors.WindowColor);
            Set(resources, "PanelBrush", SystemColors.WindowColor);
            Set(resources, "SidebarBrush", SystemColors.WindowColor);
            Set(resources, "SearchBrush", SystemColors.WindowColor);
            Set(resources, "ForegroundBrush", SystemColors.WindowTextColor);
            Set(resources, "SecondaryTextBrush", SystemColors.WindowTextColor);
            Set(resources, "BorderBrush", SystemColors.WindowTextColor);
            Set(resources, "HoverBrush", SystemColors.HighlightColor);
            Set(resources, "PressedBrush", SystemColors.HighlightColor);
            Set(resources, "SelectedBrush", SystemColors.HighlightColor);
            Set(resources, "HoverTextBrush", SystemColors.HighlightTextColor);
            Set(resources, "AccentBrush", SystemColors.HighlightColor);
            Set(resources, "AccentTextBrush", SystemColors.HighlightTextColor);
            Set(resources, "PopupBrush", SystemColors.WindowColor);
            Set(resources, "ScrollTrackBrush", SystemColors.WindowColor);
            Set(resources, "ScrollThumbBrush", SystemColors.WindowTextColor);
            Set(resources, "ScrollThumbHoverBrush", SystemColors.HighlightColor);
            Set(resources, "GlassEdgeBrush", SystemColors.WindowTextColor);
            Set(resources, "ReflectionBrush", Colors.Transparent);
        }
        else
        {
            Set(resources, "WindowBackgroundBrush", WithOpacity(background, glass ? (byte)82 : (byte)255));
            Set(resources, "PanelBrush", WithOpacity(panel, glass ? (byte)158 : (byte)255));
            Set(resources, "SidebarBrush", dark ? background : Colors.Transparent);
            Set(resources, "SearchBrush", dark ? background : panel);
            Set(resources, "ForegroundBrush", foreground);
            Set(resources, "SecondaryTextBrush", secondary);
            Set(resources, "BorderBrush", !dark && accentColor.HasValue ? Mix(border, accent, 0.3) : border);
            Set(resources, "HoverBrush", dark ? Color.FromRgb(44, 44, 44) : Mix(panel, accent, 0.14));
            Set(resources, "PressedBrush", dark ? Color.FromRgb(38, 38, 38) : Mix(panel, accent, 0.24));
            Set(resources, "SelectedBrush", dark ? Color.FromRgb(44, 44, 44) : Mix(panel, accent, 0.2));
            Set(resources, "HoverTextBrush", foreground);
            Set(resources, "AccentBrush", accent);
            Set(resources, "AccentTextBrush", ContrastText(accent));
            Set(resources, "PopupBrush", dark ? panel : Color.FromRgb(247, 248, 250));
            Set(resources, "ScrollTrackBrush", dark ? background : Color.FromRgb(225, 228, 232));
            Set(resources, "ScrollThumbBrush", dark ? Color.FromRgb(58, 58, 58) : Color.FromRgb(139, 145, 153));
            Set(resources, "ScrollThumbHoverBrush", dark ? Color.FromRgb(72, 72, 72) : Color.FromRgb(106, 113, 124));
            Set(resources, "GlassEdgeBrush", dark ? border : WithOpacity(Colors.White, 144));
            if (dark)
            {
                Set(resources, "ReflectionBrush", Colors.Transparent);
            }
            else
            {
                var reflection = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0.25, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(WithOpacity(Colors.White, 95), 0),
                        new(WithOpacity(Colors.White, 30), 0.48),
                        new(Colors.Transparent, 0.5),
                        new(Colors.Transparent, 1)
                    }
                };
                reflection.Freeze();
                resources["ReflectionBrush"] = reflection;
            }
        }
        return resources;
    }

    private static bool ApplyDwm(HwndSource source, bool dark, bool transparency)
    {
        if (DwmIsCompositionEnabled(out var enabled) < 0 || !enabled) return false;
        SetAttribute(source.Handle, 20, dark ? 1 : 0);
        SetAttribute(source.Handle, 33, SystemParameters.HighContrast ? 1 : 2);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
        // DWMWA_SYSTEMBACKDROP_TYPE: transient-window acrylic, or no backdrop.
        var glass = SetAttribute(source.Handle, 38, transparency ? 3 : 1) >= 0 && transparency;
        var inset = glass ? -1 : 0;
        var margins = new Margins { Left = inset, Right = inset, Top = inset, Bottom = inset };
        if (DwmExtendFrameIntoClientArea(source.Handle, ref margins) < 0)
        {
            SetAttribute(source.Handle, 38, 1);
            return false;
        }
        return glass;
    }

    private static int ReadSetting(string path, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    private static Color WithOpacity(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color Mix(Color first, Color second, double weight) => Color.FromRgb(
        (byte)(first.R * (1 - weight) + second.R * weight),
        (byte)(first.G * (1 - weight) + second.G * weight),
        (byte)(first.B * (1 - weight) + second.B * weight));
    private static Color ContrastText(Color color)
    {
        static double Linear(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.179 ? Colors.Black : Colors.White;
    }
    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
    private static int SetAttribute(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint color, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
