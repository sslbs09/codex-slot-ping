using System.Runtime.InteropServices;

namespace CodexSlotPing.Updater;

internal static class InstallRootProtection
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    internal static string DownloadsPath { get; } = GetDownloadsPath();

    internal static bool IsProtected(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return true;

        string normalized;
        try { normalized = Normalize(root); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return true; }

        var volumeRoot = Path.GetPathRoot(normalized) ?? string.Empty;
        if (PathsEqual(normalized, volumeRoot)) return true;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new[] { profile, desktop, DownloadsPath, windows, system }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(path => PathsEqual(normalized, path));
    }

    private static string GetDownloadsPath()
    {
        try
        {
            var folderId = DownloadsFolderId;
            var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var path);
            if (result >= 0 && path != IntPtr.Zero)
            {
                try
                {
                    var value = Marshal.PtrToStringUni(path);
                    if (!string.IsNullOrWhiteSpace(value)) return Normalize(value);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(path);
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? string.Empty : Normalize(Path.Combine(profile, "Downloads"));
    }

    private static string Normalize(string path)
    {
        if (OperatingSystem.IsWindows() && IsDevicePath(path))
        {
            throw new NotSupportedException("Windows device paths are not supported for install roots.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
