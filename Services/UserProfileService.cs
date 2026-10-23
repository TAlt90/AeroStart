using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AeroStart.Services;
public sealed record UserProfile(string UserName, ImageSource Picture, bool HasAccountPicture, string? Email = null);

public static class UserProfileService
{
    private static readonly string[] PictureValues = { "Image448", "Image240", "Image192", "Image96", "Image64", "Image48", "Image40", "Image32", "Image1080" };
    public static ImageSource Fallback { get; } = CreateFallback();

    public static Task<UserProfile> LoadCurrentAsync() => Task.Run(LoadCurrent);

    private static UserProfile LoadCurrent()
    {
        var accountName = Environment.UserName;
        string? sid = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!string.IsNullOrWhiteSpace(identity.Name)) accountName = identity.Name;
            sid = identity.User?.Value;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
        }

        var name = GetDisplayName(accountName, sid);
        var email = GetMicrosoftAccountEmail(accountName, sid);
        foreach (var path in GetPicturePaths(sid).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var picture = TryLoadPicture(path);
            if (picture != null) return new UserProfile(name, picture, true, email);
        }
        return new UserProfile(name, Fallback, false, email);
    }

    private static string? GetMicrosoftAccountEmail(string accountName, string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return null;
        var userName = accountName[(accountName.LastIndexOf('\\') + 1)..];
        var identityBuffer = IntPtr.Zero;
        var accountBuffer = IntPtr.Zero;
        try
        {
            if (NetUserGetInfo(null, userName, 23, out identityBuffer) != 0 || identityBuffer == IntPtr.Zero) return null;
            var identity = Marshal.PtrToStructure<UserInfo23>(identityBuffer);
            if (identity.UserSid == IntPtr.Zero || new SecurityIdentifier(identity.UserSid).Value != sid) return null;
            if (NetUserGetInfo(null, userName, 24, out accountBuffer) != 0 || accountBuffer == IntPtr.Zero) return null;
            var account = Marshal.PtrToStructure<UserInfo24>(accountBuffer);
            if (account.InternetIdentity == 0) return null;
            var provider = Marshal.PtrToStringUni(account.Provider);
            var address = Marshal.PtrToStringUni(account.Principal)?.Trim();
            return string.Equals(provider, "MicrosoftAccount", StringComparison.OrdinalIgnoreCase)
                && System.Net.Mail.MailAddress.TryCreate(address, out var parsed) && parsed.Address == address ? address : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (identityBuffer != IntPtr.Zero) NetApiBufferFree(identityBuffer);
            if (accountBuffer != IntPtr.Zero) NetApiBufferFree(accountBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo24
    {
        public int InternetIdentity;
        public uint Flags;
        public IntPtr Provider, Principal, UserSid;
    }

    private static string GetDisplayName(string accountName, string? sid)
    {
        const int nameDisplay = 3;
        var displayName = new StringBuilder(256);
        var size = (uint)displayName.Capacity;
        var found = GetUserNameEx(nameDisplay, displayName, ref size);
        if (!found && size > displayName.Capacity && size <= 32768)
        {
            displayName = new StringBuilder((int)size);
            found = GetUserNameEx(nameDisplay, displayName, ref size);
        }
        if (found && !string.IsNullOrWhiteSpace(displayName.ToString())) return displayName.ToString().Trim();

        var userName = accountName[(accountName.LastIndexOf('\\') + 1)..];
        if (!string.IsNullOrWhiteSpace(sid))
        {
            var buffer = IntPtr.Zero;
            try
            {
                if (NetUserGetInfo(null, userName, 23, out buffer) == 0 && buffer != IntPtr.Zero)
                {
                    var info = Marshal.PtrToStructure<UserInfo23>(buffer);
                    if (info.UserSid != IntPtr.Zero && new SecurityIdentifier(info.UserSid).Value == sid)
                    {
                        var fullName = Marshal.PtrToStringUni(info.FullName);
                        if (!string.IsNullOrWhiteSpace(fullName)) return fullName.Trim();
                    }
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        }
        return string.IsNullOrWhiteSpace(userName) ? "Benutzerkonto" : userName;
    }

    private static List<string> GetPicturePaths(string? sid)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(sid))
        {
            var keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid;
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
                    using var key = root.OpenSubKey(keyPath, writable: false);
                    foreach (var value in PictureValues)
                    {
                        if (key?.GetValue(value) is string path && !string.IsNullOrWhiteSpace(path))
                            paths.Add(Environment.ExpandEnvironmentVariables(path));
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
            var publicFolder = Environment.GetEnvironmentVariable("PUBLIC");
            if (!string.IsNullOrWhiteSpace(publicFolder))
                AddDirectoryPictures(paths, Path.Combine(publicFolder, "AccountPictures", sid));
        }
        AddDirectoryPictures(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "AccountPictures"));
        return paths;
    }

    private static void AddDirectoryPictures(List<string> paths, string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            paths.AddRange(new DirectoryInfo(directory).EnumerateFiles()
                .Where(file => file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                            || file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                            || file.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                            || file.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc).Select(file => file.FullName));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static ImageSource? TryLoadPicture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0 || stream.Length > 20 * 1024 * 1024) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static ImageSource CreateFallback()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(58, 64, 72)), null, new Rect(0, 0, 64, 64));
            var silhouette = new SolidColorBrush(Color.FromRgb(182, 191, 201));
            context.DrawEllipse(silhouette, null, new Point(32, 24), 11, 12);
            context.DrawRoundedRectangle(silhouette, null, new Rect(12, 40, 40, 30), 16, 16);
        }
        drawing.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 64, 64));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo23
    {
        public IntPtr Name;
        public IntPtr FullName;
        public IntPtr Comment;
        public uint Flags;
        public IntPtr UserSid;
    }

    [DllImport("secur32.dll", EntryPoint = "GetUserNameExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder nameBuffer, ref uint size);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetUserGetInfo(string? serverName, string userName, uint level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern uint NetApiBufferFree(IntPtr buffer);
}
