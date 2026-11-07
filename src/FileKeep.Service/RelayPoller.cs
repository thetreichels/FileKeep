using System.Security.Cryptography;
using FileKeep.Core;
using FileKeep.Core.Relay;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// Polls the relay's phone side (docs/ios-relay-protocol.md §8) on the
/// service's behalf: prunes expired drops, verifies phone-dropped bundles,
/// imports the good ones, acks them, and surfaces pending approval
/// messages. Runs as the third supervised half alongside the dashboard
/// and the scheduler (see Program.cs).
///
/// A fault in one pass must not kill the poller (SF-18 pattern, same as
/// the scheduler): the pass is logged and retried next cycle. The poller
/// never touches the phone's outbox files except to delete bundles it
/// has imported; rejected bundles stay in place for the dashboard to show.
/// </summary>
public sealed class RelayPoller
{
    /// <summary>Derived relay keys (HMAC + encryption), from the pairing secret.</summary>
    public sealed record RelayKeys(byte[] HmacKey, byte[] EncKey);

    private readonly Func<(string? Folder, RelayKeys? Keys)> _resolve;
    private readonly Action<string>? _log;
    private readonly TimeSpan _pollInterval;

    private readonly object _statusLock = new();
    private RelayPollerStatus _status = RelayPollerStatus.Disabled;
    private string _lastLoggedState = "";

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

    /// <summary>Main loop: runs until <paramref name="stopping"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        Log("relay poller started");
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A fault in one pass must not kill the poller.
                SetStatus(_status with { State = RelayPollerState.Error, LastError = ex.Message });
                Log($"relay: poll pass failed, will retry next cycle: {ex.Message}");
            }
            try { await Task.Delay(_pollInterval, stopping); }
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
            byte[] kfbBytes = File.ReadAllBytes(bundle.KfbPath);
            if (bundle.Par2Status == InboxPar2Status.Damaged)
            {
                byte[]? repaired = RelayPar2.Repair(kfbBytes, File.ReadAllBytes(bundle.KfbPath + ".par2"));
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
