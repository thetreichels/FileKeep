using System.Security.Cryptography;
using FileKeep.Core;
using FileKeep.Core.Relay;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// Polls the relay's phone side (docs/ios-relay-protocol.md §7–§8) on the
/// service's behalf: prunes expired drops, verifies phone-dropped bundles,
/// imports the good ones, acks them, surfaces pending approval
/// messages, and matches phone approval replies (§7) against approval
/// requests the desktop issued. Runs as the third supervised half
/// alongside the dashboard and the scheduler (see Program.cs).
///
/// A fault in one pass must not kill the poller (SF-18 pattern, same as
/// the scheduler): the pass is logged and retried next cycle. Three
/// consecutive failures escalate as a distinct audit event and retries
/// back off (W-2), so a persistent fault cannot hide as one log line
/// per poll forever. The poller
/// never touches the phone's outbox files except to delete bundles it
/// has imported and approval messages it has consumed; rejected bundles
/// stay in place for the dashboard to show.
/// </summary>
public sealed class RelayPoller
{
    /// <summary>Derived relay keys (HMAC + encryption), from the pairing secret.</summary>
    public sealed record RelayKeys(byte[] HmacKey, byte[] EncKey);

    /// <summary>How long a desktop-issued approval request stays claimable.</summary>
    public static readonly TimeSpan ApprovalRequestLifetime = TimeSpan.FromMinutes(15);

    private readonly Func<(string? Folder, RelayKeys? Keys)> _resolve;
    private readonly Action<string>? _log;
    private readonly TimeSpan _pollInterval;

    private readonly object _statusLock = new();
    private RelayPollerStatus _status = RelayPollerStatus.Disabled;
    private string _lastLoggedState = "";

    // W-2: consecutive poll passes that threw. At
    // PersistentFailureThreshold the fault is escalated as a distinct
    // audit event and retries back off; any clean pass resets the count.
    private int _consecutiveFailures;

    /// <summary>Consecutive failed passes before the fault is escalated (same threshold as the scheduler fix).</summary>
    private const int PersistentFailureThreshold = 3;

    /// <summary>Audit event for a poller fault that survived the retry threshold.</summary>
    private const string PersistentFailureEventId = "relay.poll.persistent-failure";

    // Approval requests the desktop issued (§7), awaiting the phone's
    // reply; and phone replies already matched, awaiting a claim by the
    // dashboard (one approval unlocks one restore — no replay).
    private readonly object _approvalLock = new();
    private readonly List<PendingApprovalRequest> _pendingRequests = new();
    private readonly List<ReceivedApproval> _receivedApprovals = new();

    /// <param name="resolve">Returns the current relay folder + keys (re-resolved every pass, so pairing changes apply without a restart).</param>
    /// <param name="pollInterval">Seconds between passes; default 60.</param>
    public RelayPoller(Func<(string? Folder, RelayKeys? Keys)> resolve, Action<string>? log = null, TimeSpan? pollInterval = null)
    {
        _resolve = resolve;
        _log = log;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Production resolver: folder + DPAPI-unprotected pairing secret from the live config.</summary>
    public static (string? Folder, RelayKeys? Keys) ResolveFromConfig(ServiceConfig config)
    {
        string? folder = config.RelayFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return (null, null);
        if (string.IsNullOrEmpty(config.RelayPairingSecretProtected))
            return (folder, null);
        string seedB64 = Dpapi.Unprotect(config.RelayPairingSecretProtected);
        byte[] seed = Convert.FromBase64String(seedB64);
        try
        {
            return (folder, new RelayKeys(RelayCrypto.DeriveHmacKey(seed), RelayCrypto.DeriveEncKey(seed)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Latest poll outcome (snapshot; safe to read from the dashboard).</summary>
    public RelayPollerStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>
    /// Records a desktop-issued approval request (§7) so a later phone
    /// reply can be matched to it. Called by the dashboard when the user
    /// asks the phone to approve a restore.
    /// </summary>
    public void RecordApprovalRequest(string nonce, string what, string detail)
    {
        lock (_approvalLock)
        {
            PruneApprovalRequestsLocked(DateTimeOffset.UtcNow);
            _pendingRequests.RemoveAll(r => r.Nonce == nonce);
            _pendingRequests.Add(new PendingApprovalRequest(
                nonce, what, detail, DateTimeOffset.UtcNow + ApprovalRequestLifetime));
        }
    }

    /// <summary>
    /// Peeks at a matched phone approval for <paramref name="nonce"/>
    /// without consuming it, so the caller can validate everything else
    /// before taking it (a failed claim must not burn the approval).
    /// </summary>
    public ReceivedApproval? PeekApproval(string nonce)
    {
        lock (_approvalLock)
        {
            return _receivedApprovals.FirstOrDefault(a => a.Nonce == nonce);
        }
    }

    /// <summary>
    /// Takes a matched phone approval for <paramref name="nonce"/>, if the
    /// poller has consumed one. A taken approval is gone — one approval
    /// unlocks one restore, never twice. Returns null while the phone has
    /// not replied yet (or the request expired unanswered).
    /// </summary>
    public ReceivedApproval? TryTakeApproval(string nonce)
    {
        lock (_approvalLock)
        {
            int i = _receivedApprovals.FindIndex(a => a.Nonce == nonce);
            if (i < 0)
                return null;
            ReceivedApproval approval = _receivedApprovals[i];
            _receivedApprovals.RemoveAt(i);
            return approval;
        }
    }

    /// <summary>Whether a request for <paramref name="nonce"/> is still awaiting the phone.</summary>
    public bool HasPendingApprovalRequest(string nonce)
    {
        lock (_approvalLock)
        {
            PruneApprovalRequestsLocked(DateTimeOffset.UtcNow);
            return _pendingRequests.Any(r => r.Nonce == nonce);
        }
    }

    private void PruneApprovalRequestsLocked(DateTimeOffset now)
    {
        _pendingRequests.RemoveAll(r => r.ExpiresUtc <= now);
        _receivedApprovals.RemoveAll(a => a.ReceivedUtc + ApprovalRequestLifetime <= now);
    }

    /// <summary>Main loop: runs until <paramref name="stopping"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        Log("relay poller started");
        while (!stopping.IsCancellationRequested)
        {
            TimeSpan delay = _pollInterval;
            try
            {
                PollOnce();
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A fault in one pass must not kill the poller — but a
                // persistent fault must not stay one log line per poll
                // forever either (W-2): at the threshold it is escalated
                // as a distinct audit event and retries back off.
                _consecutiveFailures++;
                SetStatus(_status with { State = RelayPollerState.Error, LastError = ex.Message });
                if (_consecutiveFailures == PersistentFailureThreshold)
                {
                    AuditLog.Record(_log, PersistentFailureEventId, AuditLog.Fail,
                        $"poll pass failed {_consecutiveFailures} times consecutively: {ex.Message}");
                    Log($"relay: poll pass failed {PersistentFailureThreshold} times consecutively — persistent fault, backing off retries: {ex.Message}");
                }
                else if (_consecutiveFailures > PersistentFailureThreshold)
                {
                    Log($"relay: poll pass failed ({_consecutiveFailures} consecutive failures) — persistent fault, backing off: {ex.Message}");
                }
                else
                {
                    Log($"relay: poll pass failed, will retry next cycle: {ex.Message}");
                }
                if (_consecutiveFailures >= PersistentFailureThreshold)
                {
                    int shift = Math.Min(_consecutiveFailures - PersistentFailureThreshold, 3);
                    delay = _pollInterval * (1 << shift);
                }
            }
            try { await Task.Delay(delay, stopping); }
            catch (OperationCanceledException) { break; }
        }
        Log("relay poller stopped");
    }

    /// <summary>One poll pass. Exposed for tests.</summary>
    public void PollOnce()
    {
        (string? folder, RelayKeys? keys) = _resolve();
        if (string.IsNullOrWhiteSpace(folder))
        {
            SetStatus(RelayPollerStatus.Disabled, "relay: not configured (no relay folder), poller idle");
            return;
        }
        if (keys is null)
        {
            SetStatus(_status with { State = RelayPollerState.Unpaired, LastError = null },
                "relay: folder is set but no phone is paired, poller idle");
            return;
        }
        if (!Directory.Exists(folder))
        {
            SetStatus(_status with { State = RelayPollerState.Error, LastError = $"Relay folder does not exist: {folder}" },
                $"relay: folder does not exist: {folder}");
            return;
        }

        var writer = new RelayWriter(folder, keys.HmacKey, keys.EncKey);
        var inbox = new RelayInbox(folder, keys.HmacKey, keys.EncKey, writer);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int pruned = writer.PruneExpiredDrops(now);
        int imported = 0;
        int rejected = 0;
        var pendingApprovals = new List<PendingApproval>();

        foreach (InboxBundle bundle in inbox.ScanBundles())
        {
            if (!bundle.SignatureValid)
            {
                rejected++;
                Log($"relay: rejected inbox bundle {bundle.Token} ({bundle.Name}): {bundle.RejectionReason}");
                continue;
            }
            // W-5: re-check the on-disk size before reading — the file
            // may have grown (or been swapped) since ScanBundles gated
            // it, and the scan's size is the bound the poller trusts.
            // A file that vanished (or cannot be statted) between the
            // scan and this pass is rejected, not a pass failure: one
            // bundle must not fail the whole poll.
            long onDiskLength;
            try
            {
                onDiskLength = new FileInfo(bundle.KfbPath).Length;
            }
            catch (Exception ex)
            {
                rejected++;
                Log($"relay: rejected inbox bundle {bundle.Token} ({bundle.Name}): could not re-check its on-disk size: {ex.Message}; left in place");
                continue;
            }
            if (onDiskLength > RelayInbox.MaxBundleBytes || onDiskLength != bundle.Size)
            {
                rejected++;
                Log($"relay: rejected inbox bundle {bundle.Token} ({bundle.Name}): on-disk size {onDiskLength} no longer matches the scanned size {bundle.Size}; left in place");
                continue;
            }
            byte[] kfbBytes = File.ReadAllBytes(bundle.KfbPath);
            if (bundle.Par2Status == InboxPar2Status.Damaged)
            {
                // W-5: the par2 is likewise re-checked against its exact
                // expected length before it is read; an oversized par2
                // counts as unrepairable. A par2 that vanished (or cannot
                // be read) between the scan and this pass is likewise
                // unrepairable — it must not fail the whole poll.
                string par2Path = bundle.KfbPath + ".par2";
                byte[]? repaired = null;
                if (File.Exists(par2Path))
                {
                    try
                    {
                        if (new FileInfo(par2Path).Length <= RelayInbox.ExpectedPar2Length(kfbBytes.Length))
                            repaired = RelayPar2.Repair(kfbBytes, File.ReadAllBytes(par2Path));
                    }
                    catch (Exception ex)
                    {
                        Log($"relay: could not read the par2 for inbox bundle {bundle.Token} ({bundle.Name}): {ex.Message}");
                    }
                }
                if (repaired is null)
                {
                    rejected++;
                    Log($"relay: inbox bundle {bundle.Token} ({bundle.Name}) is damaged beyond repair; left in place");
                    continue;
                }
                kfbBytes = repaired;
                Log($"relay: repaired inbox bundle {bundle.Token} ({bundle.Name}) from its par2");
            }
            // The received copy always holds a verified pair: regenerate
            // the par2 from the final bytes (covers repaired, missing,
            // and invalid-par2 cases alike).
            byte[] par2 = RelayPar2.Generate(kfbBytes);
            string receivedDir = Path.Combine(folder, "received");
            Directory.CreateDirectory(receivedDir);
            string receivedKfb = Path.Combine(receivedDir, bundle.Token + ".kfb");
            File.WriteAllBytes(receivedKfb, kfbBytes);
            File.WriteAllBytes(receivedKfb + ".par2", par2);
            string inboxJson = Path.Combine(folder, "inbox", "laptop", bundle.Token + ".json");
            if (File.Exists(inboxJson))
                File.Copy(inboxJson, Path.Combine(receivedDir, bundle.Token + ".json"), overwrite: true);
            inbox.DeleteBundle(bundle.Token);
            inbox.Acknowledge(bundle.Token, "imported");
            imported++;
            Log($"relay: imported inbox bundle {bundle.Token} ({bundle.Name})");
        }

        foreach (PeerMessage message in inbox.ReadLaptopMessages())
        {
            if (message.FailureReason is not null)
            {
                Log($"relay: phone message {message.Entry.Id} could not be decrypted: {message.FailureReason}");
                continue;
            }
            Log($"relay: phone message {message.Entry.Id} ({message.Entry.Kind}) received");
            if (message.Entry.Kind == RelayMessageKinds.ApprovalRequest && message.Plaintext is not null)
            {
                pendingApprovals.Add(new PendingApproval(
                    message.Entry.Id,
                    message.Entry.CreatedUtc,
                    System.Text.Encoding.UTF8.GetString(message.Plaintext)));
            }
            else if (message.Entry.Kind == RelayMessageKinds.Approval && message.Plaintext is not null)
            {
                // §7 reply: the phone answered a desktop-issued request.
                // Match it by nonce; a matched reply is consumed (the
                // message is deleted) and held for the dashboard to claim
                // — one approval unlocks one restore. An unmatched reply
                // answers nothing the desktop asked; leave it in place.
                ApprovalBody? body;
                try
                {
                    body = RelayJson.Deserialize(
                        System.Text.Encoding.UTF8.GetString(message.Plaintext),
                        RelayJson.Default, RelayJsonContext.Default.ApprovalBody);
                }
                catch
                {
                    body = null;
                }
                if (body is null || string.IsNullOrEmpty(body.Nonce))
                {
                    Log($"relay: phone approval {message.Entry.Id} has no usable nonce; left in place");
                    continue;
                }
                bool matched;
                lock (_approvalLock)
                {
                    PruneApprovalRequestsLocked(now);
                    matched = _pendingRequests.Any(r => r.Nonce == body.Nonce);
                    if (matched)
                    {
                        PendingApprovalRequest request = _pendingRequests.First(r => r.Nonce == body.Nonce);
                        _pendingRequests.Remove(request);
                        _receivedApprovals.RemoveAll(a => a.Nonce == body.Nonce);
                        _receivedApprovals.Add(new ReceivedApproval(
                            body.Nonce, body.Approved, request.What, now));
                    }
                }
                if (!matched)
                {
                    Log($"relay: phone approval {message.Entry.Id} matches no pending request; left in place");
                    continue;
                }
                inbox.DeleteMessage(message.Entry.Id);
                Log($"relay: phone approval for '{body.Nonce}' received (approved={body.Approved})");
            }
        }

        SetStatus(new RelayPollerStatus(
            RelayPollerState.Running,
            now,
            _status.BundlesImported + imported,
            _status.BundlesRejected + rejected,
            _status.DropsPruned + pruned,
            pendingApprovals,
            null));
        if (imported > 0 || rejected > 0 || pruned > 0)
            Log($"relay: poll done — {imported} imported, {rejected} rejected, {pruned} drops pruned");
    }

    private void SetStatus(RelayPollerStatus status, string? logOnTransition = null)
    {
        lock (_statusLock) { _status = status; }
        if (logOnTransition is not null && _lastLoggedState != status.State.ToString())
        {
            _lastLoggedState = status.State.ToString();
            Log(logOnTransition);
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}

/// <summary>Poller state for the dashboard status card.</summary>
public enum RelayPollerState
{
    /// <summary>No relay folder configured.</summary>
    Disabled,
    /// <summary>Folder set, but no pairing secret (no phone paired).</summary>
    Unpaired,
    /// <summary>Polling normally.</summary>
    Running,
    /// <summary>Last pass failed (folder missing, DPAPI failure, ...).</summary>
    Error,
}

/// <summary>Latest relay poll outcome (snapshot for the dashboard).</summary>
public sealed record RelayPollerStatus(
    RelayPollerState State,
    DateTimeOffset? LastPollUtc,
    int BundlesImported,
    int BundlesRejected,
    int DropsPruned,
    IReadOnlyList<PendingApproval> PendingApprovals,
    string? LastError)
{
    public static readonly RelayPollerStatus Disabled = new(
        RelayPollerState.Disabled, null, 0, 0, 0, Array.Empty<PendingApproval>(), null);
}

/// <summary>A pending phone approval request surfaced by the poller.</summary>
public sealed record PendingApproval(string Id, DateTimeOffset CreatedUtc, string BodyJson);

/// <summary>
/// A desktop-issued approval request (§7) awaiting the phone's reply.
/// Expires unanswered after <see cref="RelayPoller.ApprovalRequestLifetime"/>.
/// </summary>
public sealed record PendingApprovalRequest(
    string Nonce, string What, string Detail, DateTimeOffset ExpiresUtc);

/// <summary>
/// A phone approval reply matched to a desktop-issued request, held for
/// the dashboard to claim. One approval unlocks one restore — claiming
/// removes it, so it can never be replayed.
/// </summary>
public sealed record ReceivedApproval(
    string Nonce, bool Approved, string What, DateTimeOffset ReceivedUtc);
