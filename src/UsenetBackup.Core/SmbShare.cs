using System.Runtime.InteropServices;

namespace UsenetBackup.Core;

/// <summary>
/// Authenticated connection to an SMB network share. On Windows, uses
/// WNetAddConnection2 to establish the connection with explicit credentials;
/// on other platforms, relies on the OS already having the share mounted
/// (UNC paths work directly if accessible).
/// Dispose to disconnect (only disconnects connections this instance made).
/// </summary>
public sealed class SmbShare : IDisposable
{
    private readonly string _uncPath;
    private readonly bool _connectedByUs;
    private bool _disposed;

    private SmbShare(string uncPath, bool connectedByUs)
    {
        _uncPath = uncPath;
        _connectedByUs = connectedByUs;
    }

    /// <summary>
    /// Connects to an SMB share. <paramref name="uncPath"/> is the share root
    /// (e.g. \\NAS\backups) or a subdirectory (\\NAS\backups\filekeep).
    /// If <paramref name="username"/> is given, authenticates with
    /// WNetAddConnection2 (Windows only); otherwise uses the current user's
    /// credentials. Throws on failure.
    /// </summary>
    public static SmbShare Connect(string uncPath, string? username, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uncPath);
        uncPath = uncPath.Trim().Replace('/', Path.DirectorySeparatorChar);

        // Local (non-UNC) paths are allowed when no credentials are given:
        // the share is already mounted or it's a plain directory (also handy
        // for tests). UNC paths go through the normal flow below.
        if (!uncPath.StartsWith(@"\\") && !uncPath.StartsWith("//"))
        {
            if (!string.IsNullOrWhiteSpace(username))
                throw new ArgumentException(
                    "Credentials require a UNC share path (\\\\server\\share).", nameof(uncPath));
            if (!Directory.Exists(uncPath))
                throw new IOException($"Directory not reachable: {uncPath}.");
            return new SmbShare(Path.GetFullPath(uncPath), connectedByUs: false);
        }

        uncPath = NormalizeUnc(uncPath);

        // Share root for the connection: \\server\share (auth is per-share).
        string shareRoot = GetShareRoot(uncPath);

        if (string.IsNullOrWhiteSpace(username) || !OperatingSystem.IsWindows())
        {
            // No explicit creds (or non-Windows): verify the path is reachable
            // with the current identity.
            if (!Directory.Exists(uncPath))
                throw new IOException($"SMB share not reachable: {uncPath}. " +
                    "Check the path and that the current user has access.");
            return new SmbShare(uncPath, connectedByUs: false);
        }

        var netResource = new NETRESOURCE
        {
            dwScope = 0,
            dwType = 1, // RESOURCETYPE_DISK
            dwDisplayType = 0,
            dwUsage = 0,
            lpLocalName = null,
            lpRemoteName = shareRoot,
            lpComment = null,
            lpProvider = null,
        };
        int result = WNetAddConnection2(
            ref netResource,
            password ?? "",
            username,
            0); // CONNECT_UPDATE_PROFILE=0: don't persist
        if (result != 0)
        {
            throw new IOException(
                $"Could not connect to SMB share {shareRoot} as {username} " +
                $"(WNet error {result}). Check the share path and credentials.");
        }
        // Ensure the full subdirectory path exists / is reachable.
        if (!Directory.Exists(uncPath))
        {
            try { Directory.CreateDirectory(uncPath); }
            catch (Exception ex)
            {
                WNetCancelConnection2(shareRoot, 0, force: false);
                throw new IOException(
                    $"Connected to {shareRoot} but could not reach {uncPath}: {ex.Message}", ex);
            }
        }
        return new SmbShare(uncPath, connectedByUs: true);
    }

    /// <summary>The connected UNC path (share root or subdirectory).</summary>
    public string UncPath => _uncPath;

    /// <summary>Combines the share path with a relative subpath.</summary>
    public string Combine(params string[] parts)
    {
        string path = _uncPath;
        foreach (string part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_connectedByUs && OperatingSystem.IsWindows())
        {
            try { WNetCancelConnection2(GetShareRoot(_uncPath), 0, force: false); }
            catch { /* best effort */ }
        }
    }

    internal static string NormalizeUnc(string unc)
    {
        unc = unc.Trim().Replace('/', '\\');
        if (!unc.StartsWith(@"\\"))
            throw new ArgumentException($"Not a UNC path: {unc}", nameof(unc));
        return unc.TrimEnd('\\');
    }

    /// <summary>Extracts \\server\share from a possibly deeper UNC path.</summary>
    internal static string GetShareRoot(string unc)
    {
        // \\server\share\a\b -> \\server\share
        string[] parts = unc.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid UNC path: {unc}", nameof(unc));
        return $@"\\{parts[0]}\{parts[1]}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NETRESOURCE lpNetResource,
        string lpPassword,
        string lpUsername,
        int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(
        string lpName,
        int dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool force);
}
