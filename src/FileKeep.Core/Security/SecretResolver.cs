namespace FileKeep.Core.Security;

/// <summary>
/// Middle-path secret centralization (2026-10-27): ONE place that encodes
/// the secret-resolution precedence every call site used to inline. This
/// is deliberately NOT a ProtectedSecret/secret-provider abstraction: it
/// returns plain strings in narrow scope exactly like the code it
/// replaces, and changes no precedence, no stored format, and no
/// fallback.
/// </summary>
/// <remarks>
/// Precedence encoded here, verified against the pre-existing inline
/// code at every wired call site:
/// <list type="table">
/// <item>
/// <term>NNTP password</term>
/// <description>env <c>FILEKEEP_NNTP_PASSWORD</c> if non-empty, else the
/// DPAPI blob if non-empty, else null. (Scheduler, dashboard test
/// connection.)</description>
/// </item>
/// <item>
/// <term>SMB password</term>
/// <description>env <c>FILEKEEP_SMB_PASSWORD</c> if non-empty, else the
/// DPAPI blob if non-empty, else null. (Scheduler, dashboard test
/// connection, offline stores, save-as.)</description>
/// </item>
/// <item>
/// <term>Repository passphrase</term>
/// <description>env <c>FILEKEEP_PASSPHRASE</c> if non-empty, else the
/// DPAPI blob if non-empty, else none. (Scheduler
/// <c>TryGetPassphrase</c>; the dashboard's no-config branch reads env
/// only because without a config there is no blob.)</description>
/// </item>
/// <item>
/// <term>Availability</term>
/// <description>A secret is "available" when its blob is non-empty OR its
/// env var is non-empty (the <c>HasPassword</c> /
/// <c>HasConfiguredPassphrase</c> checks).</description>
/// </item>
/// </list>
/// Empty means <see cref="string.IsNullOrEmpty"/> throughout — the
/// pre-existing semantics, preserved exactly: a whitespace-only env var
/// still wins over the blob (it is non-empty), because changing that
/// would change which source wins. Unprotect failures propagate to the
/// caller, which keeps its existing handling (job failure, a 400, or
/// fall-back-to-no-password); the helper never swallows them.
/// Intentionally NOT routed through this helper (no competing source,
/// so there is no precedence to centralize): the YubiKey slot secret
/// (DPAPI blob only), the TOTP live/pending secrets (DPAPI blobs only;
/// pending-vs-live is enrollment-state logic in the protection
/// endpoints), the added-passphrase wrap and the YubiKey challenge wrap
/// (keyed unwraps needing a typed candidate or a key touch, not a
/// source choice), the dashboard's passphrase-source label (it needs
/// the source distinction, not just availability), and the CLI's
/// flag &gt; env &gt; prompt chains (the CLI never resolves a DPAPI
/// blob, so there is no env-vs-blob site to wire).
/// Plaintext carriers (<c>*Plaintext</c> fields) are a write-path only:
/// the dashboard converts them to DPAPI blobs at save time and nulls
/// them before persisting; they are never a resolution source here.
/// </remarks>
public static class SecretResolver
{
    /// <summary>
    /// Resolves a stored credential: the env var wins when non-empty,
    /// otherwise the DPAPI blob is decrypted when non-empty, otherwise
    /// null. <paramref name="unprotect"/> defaults to
    /// <see cref="Dpapi.Unprotect"/>; tests pass a fake so resolution
    /// is provable on Linux, where DPAPI throws.
    /// </summary>
    public static string? ResolvePassword(string envVar, string? protectedBlob,
        Func<string, string>? unprotect = null)
    {
        string? fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;
        if (string.IsNullOrEmpty(protectedBlob))
            return null;
        return (unprotect ?? Dpapi.Unprotect)(protectedBlob);
    }

    /// <summary>
    /// True when a secret is available from either source: a non-empty
    /// stored blob or a non-empty env var.
    /// </summary>
    public static bool HasSecret(string envVar, string? protectedBlob) =>
        !string.IsNullOrEmpty(protectedBlob) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar));
}
