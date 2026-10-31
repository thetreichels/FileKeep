using System.Net;

namespace FileKeep.Core.Service;

/// <summary>
/// Guards Kestrel bind addresses: no FileKeep HTTP surface may listen
/// on a non-loopback address without an explicit, reviewed exception.
/// The dashboard has no authentication, so a non-loopback bind would
/// expose a LocalSystem-privilege API to the network. Every Kestrel
/// bind site (dashboard, portable reader) must call
/// <see cref="RequireLoopbackBind"/> before binding, so a future
/// change that makes a bind configurable cannot silently open it.
/// </summary>
public static class LoopbackGuard
{
    /// <summary>
    /// True only for loopback bind values: "localhost" (any case), any
    /// 127.x.x.x literal, ::1 (bracketed or not). Classification is purely
    /// textual — no DNS resolution — so a hostname that merely resolves to
    /// loopback does not qualify, and neither do wildcards (0.0.0.0, ::, *)
    /// or LAN addresses.
    /// </summary>
    public static bool IsLoopbackBind(string? bind)
    {
        string value = (bind ?? "").Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless
    /// <paramref name="bind"/> is a loopback address. Call before
    /// binding Kestrel; the process must fail closed rather than
    /// serve on a network address.
    /// </summary>
    public static void RequireLoopbackBind(string? bind)
    {
        if (!IsLoopbackBind(bind))
            throw new InvalidOperationException(
                $"bind '{bind}' is not a loopback address. This HTTP surface has no " +
                "authentication and must not be exposed to a network — bind " +
                "to 127.0.0.1, ::1, or localhost, or remove the setting.");
    }
}
