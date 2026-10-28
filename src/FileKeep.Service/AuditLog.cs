namespace FileKeep.Service;

/// <summary>
/// Structured security audit records (security review rec 6).
/// Records are single lines of the form
/// <c>AUDIT &lt;event-id&gt; &lt;outcome&gt; &lt;detail&gt;</c>,
/// written through the service's log sink (so they land in
/// service.log with its timestamp prefix) and kept greppable by the
/// stable event ids below.
/// <para>
/// Contract for callers: <paramref name="detail"/> must be stable,
/// non-secret identifiers only — method names, booleans, counts,
/// repo paths, backup ids, fixed reason codes. Never user input,
/// never exception text, never passphrase/code/blob/key material.
/// The helper does not sanitize: sanitizing here would mangle
/// legitimate path details (a 32+ character directory name looks
/// exactly like a token), so the guarantee lives in what callers
/// pass, and tests assert the records contain no secret-shaped
/// content for every wired event.
/// </para>
/// A null log sink is a no-op, so call sites without logging (and
/// tests that do not capture) keep working unchanged.
/// </summary>
public static class AuditLog
{
    /// <summary>Service configuration loaded (startup).</summary>
    public const string ConfigLoad = "config.load";

    /// <summary>A repository opened for restore or verification.</summary>
    public const string RepoOpen = "repo.open";

    /// <summary>A restore-session unlock attempt (any method).</summary>
    public const string UnlockAttempt = "unlock.attempt";

    /// <summary>A restore verification run completed (issues or clean).</summary>
    public const string RestoreVerify = "restore.verify";

    /// <summary>A failed authentication or proof attempt.</summary>
    public const string AuthFailure = "auth.fail";

    /// <summary>The security mode set changed (profile/method change).</summary>
    public const string SecurityMode = "security.mode";

    /// <summary>Successful outcome.</summary>
    public const string Ok = "ok";

    /// <summary>Failed outcome.</summary>
    public const string Fail = "fail";

    /// <summary>Formats one audit record (no timestamp; the log sink adds it).</summary>
    public static string Format(string eventId, string outcome, string detail) =>
        $"AUDIT {eventId} {outcome} {detail}";

    /// <summary>Writes one audit record through <paramref name="log"/> (no-op when null).</summary>
    public static void Record(Action<string>? log, string eventId, string outcome, string detail) =>
        log?.Invoke(Format(eventId, outcome, detail));
}
