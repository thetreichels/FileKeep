using FileKeep.Core.Relay;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// Runtime-risk fixes (audit 2026-11-10): W-5 size gates on relay
/// bundles/par2, W-2 poller persistent-failure escalation + backoff.
/// Sparse files stand in for multi-GB bundles — the gates check
/// FileInfo.Length, so no real gigabytes are written.
/// </summary>
public class RelayRuntimeRiskTests : IDisposable
{
    private readonly string _relayRoot;
    private readonly byte[] _seed = RelayCrypto.NewPairingSecret();
    private readonly byte[] _hmacKey;
    private readonly byte[] _encKey;
    private readonly RelayWriter _writer;
    private readonly RelayInbox _inbox;
    private readonly List<string> _log = new();

    public RelayRuntimeRiskTests()
    {
        _relayRoot = Path.Combine(Path.GetTempPath(), "fk-relay-risk-" + Guid.NewGuid().ToString("N"));
        _hmacKey = RelayCrypto.DeriveHmacKey(_seed);
        _encKey = RelayCrypto.DeriveEncKey(_seed);
        _writer = new RelayWriter(_relayRoot, _hmacKey, _encKey);
        _inbox = new RelayInbox(_relayRoot, _hmacKey, _encKey, _writer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_relayRoot, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSignedMetadata(string token, string name, long size, DateTimeOffset? created = null)
    {
        DateTimeOffset when = created ?? new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var meta = new RelayInboxMetadata
        {
            Token = token,
            Name = name,
            Size = size,
            CreatedUtc = when,
            Hmac = RelayCrypto.ComputeInboxHmac(_hmacKey, token, name, size, when),
        };
        File.WriteAllText(Path.Combine(_relayRoot, "inbox", "laptop", token + ".json"),
            RelayJson.Serialize(meta, RelayJson.Indented, RelayJsonContext.Default.RelayInboxMetadata));
    }

    private static void WriteSparse(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.CreateNew);
        fs.SetLength(length);
    }

    [Fact]
    public void ScanBundles_OversizedBundle_IsRejectedWithoutReading()
    {
        // A sparse file over the cap, with metadata correctly signed for
        // that size: the gate must refuse it on length alone, without
        // loading 2 GiB into memory.
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        WriteSparse(Path.Combine(inboxDir, "aaaa.kfb"), RelayInbox.MaxBundleBytes + 1);
        WriteSignedMetadata("aaaa", "huge.kfb", RelayInbox.MaxBundleBytes + 1);

        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.False(item.SignatureValid);
        Assert.Equal(InboxPar2Status.NotRead, item.Par2Status);
        Assert.Equal(RelayInbox.MaxBundleBytes + 1, item.Size);
        Assert.Contains("size limit", item.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanBundles_OnDiskLargerThanSignedSize_IsRejectedWithoutReading()
    {
        // Sparse 1 MiB file, metadata signed for 1000 bytes: the signed
        // size is the authoritative bound, so the read is refused.
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        WriteSparse(Path.Combine(inboxDir, "bbbb.kfb"), 1024 * 1024);
        WriteSignedMetadata("bbbb", "grown.kfb", 1000);

        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.False(item.SignatureValid);
        Assert.Equal(InboxPar2Status.NotRead, item.Par2Status);
        Assert.Equal(1024 * 1024, item.Size);
        Assert.Contains("signed size", item.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScanBundles_OversizedPar2_IsInvalidPar2()
    {
        // A small valid bundle with a par2 far larger than the format
        // allows for it: the par2 is not read, the bundle stays importable.
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        byte[] kfb = new byte[1000];
        new Random(71).NextBytes(kfb);
        File.WriteAllBytes(Path.Combine(inboxDir, "cccc.kfb"), kfb);
        WriteSignedMetadata("cccc", "small.kfb", kfb.Length);
        long oversized = RelayInbox.ExpectedPar2Length(kfb.Length) + 1;
        File.WriteAllBytes(Path.Combine(inboxDir, "cccc.kfb.par2"), new byte[(int)oversized]);

        InboxBundle item = Assert.Single(_inbox.ScanBundles());
        Assert.True(item.SignatureValid);
        Assert.Equal(InboxPar2Status.InvalidPar2, item.Par2Status);
    }

    [Fact]
    public void PollOnce_OversizedBundle_IsRejectedAndLeftInPlace()
    {
        string inboxDir = Path.Combine(_relayRoot, "inbox", "laptop");
        WriteSparse(Path.Combine(inboxDir, "dddd.kfb"), RelayInbox.MaxBundleBytes + 1);
        WriteSignedMetadata("dddd", "huge.kfb", RelayInbox.MaxBundleBytes + 1);

        var poller = new RelayPoller(
            () => (_relayRoot, new RelayPoller.RelayKeys(_hmacKey, _encKey)), _log.Add);
        poller.PollOnce();

        Assert.Equal(0, poller.Status.BundlesImported);
        Assert.Equal(1, poller.Status.BundlesRejected);
        Assert.True(File.Exists(Path.Combine(inboxDir, "dddd.kfb")));
    }

    [Fact]
    public async Task RunAsync_PersistentFailures_EscalateWithAuditEventOnce()
    {
        var poller = new RelayPoller(
            () => throw new InvalidOperationException("boom"),
            _log.Add,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        Task run = poller.RunAsync(cts.Token);
        try
        {
            // Wait for the escalation (3rd consecutive failure, ~150 ms in).
            bool escalated = false;
            for (int i = 0; i < 100 && !escalated; i++)
            {
                await Task.Delay(100);
                escalated = _log.Any(l => l.Contains("relay.poll.persistent-failure"));
            }
            Assert.True(escalated, "Expected the persistent-failure audit event in the log.");
            Assert.Contains(_log, l => l.Contains("3 times consecutively"));
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // The audit event fires once, at the threshold — not on every poll.
        Assert.Single(_log, l => l.Contains("relay.poll.persistent-failure"));
        Assert.Equal(RelayPollerState.Error, poller.Status.State);
    }

    [Fact]
    public async Task RunAsync_TransientFailuresBelowThreshold_DoNotEscalate()
    {
        int calls = 0;
        var poller = new RelayPoller(
            () =>
            {
                if (Interlocked.Increment(ref calls) <= 2)
                    throw new InvalidOperationException("transient");
                return (_relayRoot, new RelayPoller.RelayKeys(_hmacKey, _encKey));
            },
            _log.Add,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        Task run = poller.RunAsync(cts.Token);
        try
        {
            await Task.Delay(1000);
        }
        finally
        {
            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.DoesNotContain(_log, l => l.Contains("relay.poll.persistent-failure"));
        Assert.Equal(RelayPollerState.Running, poller.Status.State);
    }
}
