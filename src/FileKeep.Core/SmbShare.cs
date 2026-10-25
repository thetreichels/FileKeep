using System.Runtime.InteropServices;

namespace FileKeep.Core;

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
    private readonly string? _shareRoot;
    private readonly bool _holdsConnectionRef;
    private bool _disposed;

    // P2-C9: process-wide refcount of SMB connections by share root.
    // Ownership used to be per-instance guesswork: the instance whose
    // WNetAddConnection2 returned 0 "owned" the connection and cancelled
    // it on Dispose — even while another consumer (a backup holding the
    // share for its whole run) was still using the connection a second
    // instance had merely reused. Now every consumer of a connection
    // THIS process made holds a ref; the OS connection is cancelled
    // only when the last ref is released. Connections that pre-existed
    // outside this process are never refcounted (and never cancelled).
    private static readonly object ConnectionRegistryLock = new();
    private static readonly Dictionary<string, (int Count, bool ProcessConnected)>
        ConnectionRegistry = new(StringComparer.OrdinalIgnoreCase);

    private SmbShare(string uncPath, bool connectedByUs,
        string? shareRoot = null, bool holdsConnectionRef = false)
    {
        _uncPath = uncPath;
        _connectedByUs = connectedByUs;
        _shareRoot = shareRoot;
        _holdsConnectionRef = holdsConnectionRef;
    }

    internal static void AcquireConnectionRef(string shareRoot, bool processConnected)
    {
        lock (ConnectionRegistryLock)
        {
            ConnectionRegistry.TryGetValue(shareRoot, out var entry);
            ConnectionRegistry[shareRoot] =
                (entry.Count + 1, entry.ProcessConnected || processConnected);
        }
    }

    internal static bool HasConnectionRef(string shareRoot)
    {
        lock (ConnectionRegistryLock)
            return ConnectionRegistry.ContainsKey(shareRoot);
    }

    /// <summary>
    /// Drops one ref; returns true when the caller should cancel the OS
    /// connection (last ref, and this process made the connection).
    /// </summary>
    internal static bool ReleaseConnectionRef(string shareRoot)
    {
        lock (ConnectionRegistryLock)
        {
            if (!ConnectionRegistry.TryGetValue(shareRoot, out var entry))
                return false;
            if (entry.Count <= 1)
            {
                ConnectionRegistry.Remove(shareRoot);
                return entry.ProcessConnected;
            }
            ConnectionRegistry[shareRoot] = (entry.Count - 1, entry.ProcessConnected);
            return false;
        }
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
        return Connect(uncPath, username, password, createIfMissing: true);
    }

    /// <summary>
    /// Connects to an SMB share. <paramref name="uncPath"/> is the share root
    /// (e.g. \\NAS\backups) or a subdirectory (\\NAS\backups\filekeep).
    /// If <paramref name="username"/> is given, authenticates with
    /// WNetAddConnection2 (Windows only); otherwise uses the current user's
    /// credentials. If <paramref name="createIfMissing"/> is true (default),
    /// the subdirectory is created after connecting; set false for
    /// read-only access (restore/discovery) so typos fail instead of
    /// creating empty directories. Throws on failure.
    /// </summary>
    public static SmbShare Connect(string uncPath, string? username, string? password, bool createIfMissing)
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
            {
                if (!createIfMissing)
                    throw new IOException($"Directory not found: {uncPath}.");
                Directory.CreateDirectory(uncPath);
            }
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
            {
                if (!createIfMissing)
                    throw new IOException($"SMB share not reachable: {uncPath}. " +
                        "Check the path and that the current user has access.");
                // Try to create (only works if we have write access to the parent).
                try { Directory.CreateDirectory(uncPath); }
                catch (Exception ex)
                {
                    throw new IOException($"SMB share not reachable: {uncPath}. " +
                        $"Check the path and that the current user has access: {ex.Message}", ex);
                }
            }
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
        const int ERROR_ALREADY_ASSIGNED = 85;
        const int ERROR_DEVICE_ALREADY_REMEMBERED = 1202;
        const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
        if (result == ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            // MU-17: name the actual problem — an existing connection
            // under different credentials — instead of a generic
            // "check credentials" that sends the user in circles.
            throw new IOException(
                $"Could not connect to SMB share {shareRoot} as {username}: " +
                "Windows already has a connection to this server under " +
                "different credentials (error 1219). Disconnect the " +
                "existing connection (e.g. `net use /delete`) or use " +
                "those credentials.");
        }
        if (result != 0 && result != ERROR_ALREADY_ASSIGNED && result != ERROR_DEVICE_ALREADY_REMEMBERED)
        {
            throw new IOException(
                $"Could not connect to SMB share {shareRoot} as {username} " +
                $"(WNet error {result}). Check the share path and credentials.");
        }
        // If already connected (85/1202), the existing connection is reused.
        bool connectedByUs = result == 0;
        // P2-C9: take this consumer's ref on the connection now (before
        // the reachability checks, whose failure paths release it). A
        // reused connection is refcounted only when THIS process made
        // it; a pre-existing external connection stays a guest.
        bool holdsRef = connectedByUs || HasConnectionRef(shareRoot);
        if (holdsRef)
            AcquireConnectionRef(shareRoot, processConnected: connectedByUs);
        if (!connectedByUs)
        {
            // MU-17: a reused connection keeps ITS identity — Windows
            // silently ignores the credentials we were asked to use, so
            // operations would run as the wrong user and fail later
            // with unexplained permission errors. Verify the existing
            // connection's user matches the requested one.
            string? existingUser = GetConnectionUser(shareRoot);
            if (existingUser is not null && !SameUser(existingUser, username))
                throw new IOException(
                    $"SMB share {shareRoot} is already connected as " +
                    $"'{existingUser}', not '{username}'. Disconnect the " +
                    "existing connection or update the location's credentials.");
        }
        // Ensure the full subdirectory path exists / is reachable.
        if (!Directory.Exists(uncPath))
        {
            if (!createIfMissing)
            {
                if (holdsRef && ReleaseConnectionRef(shareRoot))
                    WNetCancelConnection2(shareRoot, 0, force: false);
                throw new IOException(
                    $"Connected to {shareRoot} but subdirectory not found: {uncPath}. " +
                    "Check the path.");
            }
            try { Directory.CreateDirectory(uncPath); }
            catch (Exception ex)
            {
                if (holdsRef && ReleaseConnectionRef(shareRoot))
                    WNetCancelConnection2(shareRoot, 0, force: false);
                throw new IOException(
                    $"Connected to {shareRoot} but could not reach {uncPath}: {ex.Message}", ex);
            }
        }
        return new SmbShare(uncPath, connectedByUs, shareRoot, holdsRef);
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
        if (_holdsConnectionRef && _shareRoot is not null)
        {
            // Cancel only when the last consumer lets go (P2-C9).
            if (ReleaseConnectionRef(_shareRoot) && OperatingSystem.IsWindows())
            {
                try { WNetCancelConnection2(_shareRoot, 0, force: false); }
                catch { /* best effort */ }
            }
        }
        else if (_connectedByUs && OperatingSystem.IsWindows())
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

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetUser(
        string lpName,
        System.Text.StringBuilder lpUserName,
        ref int lpnLength);

    /// <summary>
    /// The user an existing connection to the share was made with, or
    /// null when it can't be determined (no connection, API failure).
    /// </summary>
    private static string? GetConnectionUser(string shareRoot)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(256);
            int length = buffer.Capacity;
            return WNetGetUser(shareRoot, buffer, ref length) == 0
                ? buffer.ToString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>User comparison tolerant of DOMAIN\user vs bare-user forms.</summary>
    private static bool SameUser(string existing, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return true; // no requested identity to conflict with
        static string Bare(string u) =>
            u.Contains('\\') ? u[(u.IndexOf('\\') + 1)..] : u;
        return string.Equals(existing, requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Bare(existing), Bare(requested), StringComparison.OrdinalIgnoreCase);
    }
}
