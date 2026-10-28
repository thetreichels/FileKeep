using System.Net.Sockets;
using FileKeep.Core.Service;

namespace FileKeep.Core;

/// <summary>
/// Bounded reachability tries for network backup locations. Used ONLY
/// at a due time (or a manual run) as the availability gate — never
/// polled — so a roaming laptop whose network is gone gets a derived
/// pause instead of a failed run. A try proves transport reachability
/// only: it does not authenticate, so a server that answers but
/// rejects the password still counts as reachable and the resulting
/// auth failure stays a real, visible run failure.
/// </summary>
public static class NetworkProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>True when a TCP connection to host:port completes in time.</summary>
    public static bool IsTcpReachable(string host, int port, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            return false;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host.Trim(), port);
            return connect.Wait(timeout ?? DefaultTimeout) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The reachability try for one location: a Usenet location is its
    /// host:port; an SMB location is its share's server on port 445
    /// (a non-UNC "share" is a local path — always considered
    /// reachable); anything else is not network-gated.
    /// </summary>
    public static bool IsLocationReachable(BackupLocation loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (loc.ParsedKind == LocationKind.Nntp)
            return IsTcpReachable(loc.Host, loc.Port > 0 ? loc.Port : 119);
        if (loc.ParsedKind == LocationKind.Smb)
        {
            string? server = SmbServerOf(loc.Share);
            return server is null || IsTcpReachable(server, 445);
        }
        return true;
    }

    /// <summary>The server part of a UNC path (\\server\share → server); null for non-UNC paths.</summary>
    public static string? SmbServerOf(string? share)
    {
        if (string.IsNullOrWhiteSpace(share))
            return null;
        string s = share.Trim();
        if (!s.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        string rest = s.Substring(2);
        int slash = rest.IndexOf('\\');
        string server = slash >= 0 ? rest.Substring(0, slash) : rest;
        return server.Length > 0 ? server : null;
    }
}
