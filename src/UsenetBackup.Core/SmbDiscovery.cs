using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UsenetBackup.Core;

/// <summary>
/// Discovers SMB shares on the local network. WinPE/WinRE has full TCP/IP
/// and an SMB client, but none of the browsing/discovery services that power
/// Explorer's Network view — so this does it the direct way:
///
/// 1. Enumerate local IPv4 interfaces and compute their subnets.
/// 2. Probe TCP 445 (SMB) on every address in each subnet, in parallel.
/// 3. Call NetShareEnum against each responding host to list its shares.
///
/// Hidden/administrative shares (C$, ADMIN$, IPC$, anything ending in $) are
/// filtered out. Share enumeration may require credentials on locked-down
/// hosts; those hosts are still reported (as "host reachable, shares hidden")
/// so the user can type the share path manually.
/// </summary>
public sealed class SmbDiscovery
{
    public sealed record DiscoveredShare(
        string HostAddress,
        string? HostName,
        string ShareName,
        string Remark);

    public sealed record DiscoveredHost(
        string HostAddress,
        string? HostName,
        bool SharesListed,
        IReadOnlyList<DiscoveredShare> Shares);

    private readonly Action<string>? _log;

    public SmbDiscovery(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// Scans local subnets for SMB hosts and enumerates their shares.
    /// <paramref name="progress"/> receives (hostsScanned, hostsTotal).
    /// Only scans subnets with a prefix length of /16 or longer (scanning
    /// anything larger would take unreasonable time).
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredHost>> DiscoverAsync(
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = GetScanTargets().ToList();
        _log?.Invoke($"SMB discovery: scanning {targets.Count} addresses.");
        int done = 0;
        var smbHosts = new System.Collections.Concurrent.ConcurrentBag<IPAddress>();

        // Probe port 445 in parallel batches.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 64,
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(targets, options, async (ip, ct) =>
        {
            if (await IsSmbPortOpenAsync(ip, ct))
                smbHosts.Add(ip);
            int d = Interlocked.Increment(ref done);
            if (d % 16 == 0) progress?.Invoke(d, targets.Count);
        });
        progress?.Invoke(targets.Count, targets.Count);

        var results = new List<DiscoveredHost>();
        foreach (IPAddress ip in smbHosts.OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string addr = ip.ToString();
            string? hostName = TryResolveHostname(ip);
            var shares = new List<DiscoveredShare>();
            bool listed = false;
            try
            {
                foreach (var (name, remark) in EnumShares(addr))
                {
                    shares.Add(new DiscoveredShare(addr, hostName, name, remark));
                }
                listed = true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"SMB discovery: {addr} has port 445 open but share list failed: {ex.Message}");
            }
            results.Add(new DiscoveredHost(addr, hostName, listed, shares));
        }
        _log?.Invoke($"SMB discovery: {results.Count} SMB host(s), " +
            $"{results.Sum(h => h.Shares.Count)} share(s) found.");
        return results;
    }

    /// <summary>
    /// All IPv4 host addresses in local subnets (/16 or smaller), excluding
    /// the local addresses themselves, network, and broadcast.
    /// </summary>
    internal static IEnumerable<IPAddress> GetScanTargets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var props = nic.GetIPProperties();
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (unicast.IPv4Mask is null)
                    continue;
                int prefix = MaskToPrefix(unicast.IPv4Mask);
                if (prefix < 16)
                    continue; // too big to scan
                uint mask = ToUInt32(unicast.IPv4Mask);
                uint addr = ToUInt32(unicast.Address);
                uint network = addr & mask;
                uint broadcast = network | ~mask;
                for (uint host = network + 1; host < broadcast; host++)
                {
                    if (host == addr)
                        continue;
                    yield return new IPAddress(BitConverter.GetBytes(host).Reverse().ToArray());
                }
            }
        }
    }

    private static async Task<bool> IsSmbPortOpenAsync(IPAddress ip, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await client.ConnectAsync(ip, 445, linked.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryResolveHostname(IPAddress ip)
    {
        try
        {
            var entry = Dns.GetHostEntry(ip);
            string name = entry.HostName;
            int dot = name.IndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lists non-hidden disk shares on a remote host via NetShareEnum.</summary>
    internal static IEnumerable<(string Name, string Remark)> EnumShares(string server)
    {
        var shares = new List<(string, string)>();
        IntPtr bufPtr = IntPtr.Zero;
        try
        {
            int entriesRead, totalEntries, resumeHandle = 0;
            int result = NetShareEnum(
                server, 1, out bufPtr, 0xFFFFFFFF,
                out entriesRead, out totalEntries, ref resumeHandle);
            // 0 = success, 234 (MORE_DATA) also yields entries.
            if (result != 0 && result != 234)
                throw new IOException($"NetShareEnum failed on {server} (error {result}).");
            int structSize = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < entriesRead; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(
                    IntPtr.Add(bufPtr, i * structSize));
                if (info.shi1_netname is null)
                    continue;
                string name = info.shi1_netname;
                // Skip hidden/admin shares: IPC$, ADMIN$, C$, anything ending in $.
                if (name.EndsWith("$", StringComparison.Ordinal))
                    continue;
                // Type 0 = disk. Skip print/pipe/device shares.
                if (info.shi1_type != 0)
                    continue;
                shares.Add((name, info.shi1_remark ?? ""));
            }
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
                NetApiBufferFree(bufPtr);
        }
        return shares;
    }

    private static int MaskToPrefix(IPAddress mask)
    {
        uint m = ToUInt32(mask);
        int prefix = 0;
        while ((m & 0x80000000) != 0) { prefix++; m <<= 1; }
        return prefix;
    }

    private static uint ToUInt32(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_netname;
        public int shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string servername,
        int level,
        out IntPtr bufptr,
        uint prefmaxlen,
        out int entriesread,
        out int totalentries,
        ref int resume_handle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
