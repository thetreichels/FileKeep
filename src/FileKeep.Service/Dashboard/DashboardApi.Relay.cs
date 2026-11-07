using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FileKeep.Core;
using FileKeep.Core.Relay;
using FileKeep.Core.Service;
using QRCoder;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — iOS relay (docs/ios-relay-protocol.md): pairing (QR),
/// beaming a bundle as a drop, and relay status/inbox/drops.
/// The dashboard is loopback-only, so these endpoints are only reachable
/// from this machine; the relay folder itself is what the phone reaches.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>
    /// Pairing payload the phone scans (QR). <c>v</c> is the format
    /// version, <c>url</c> the relay HTTP base URL, <c>seed</c> the
    /// base64 pairing secret both sides derive keys from. The iOS side
    /// parses this JSON from the QR code.
    /// </summary>
    public sealed class RelayPairingDto
    {
        [JsonPropertyName("payload")]
        public string Payload { get; set; } = "";

        [JsonPropertyName("qrSvg")]
        public string QrSvg { get; set; } = "";

        [JsonPropertyName("relayFolder")]
        public string RelayFolder { get; set; } = "";
    }

    public sealed class RelayPairRequestDto
    {
        [JsonPropertyName("relayFolder")]
        public string RelayFolder { get; set; } = "";

        [JsonPropertyName("relayBaseUrl")]
        public string RelayBaseUrl { get; set; } = "";
    }

    public sealed class RelayPendingApprovalDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; set; }

        [JsonPropertyName("bodyJson")]
        public string BodyJson { get; set; } = "";
    }

    public sealed class RelayStatusDto
    {
        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        [JsonPropertyName("paired")]
        public bool Paired { get; set; }

        [JsonPropertyName("relayFolder")]
        public string? RelayFolder { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = "";

        [JsonPropertyName("lastPollUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LastPollUtc { get; set; }

        [JsonPropertyName("bundlesImported")]
        public int BundlesImported { get; set; }

        [JsonPropertyName("bundlesRejected")]
        public int BundlesRejected { get; set; }

        [JsonPropertyName("dropsPruned")]
        public int DropsPruned { get; set; }

        [JsonPropertyName("pendingApprovals")]
        public List<RelayPendingApprovalDto> PendingApprovals { get; set; } = new();

        [JsonPropertyName("lastError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastError { get; set; }
    }

    public sealed class RelayBeamRequestDto
    {
        [JsonPropertyName("kfbPath")]
        public string KfbPath { get; set; } = "";

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("lifetimeHours")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? LifetimeHours { get; set; }
    }

    public sealed class RelayBeamDto
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("expiryUtc")]
        public DateTimeOffset ExpiryUtc { get; set; }
    }

    public sealed class RelayDropDto
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("expiryUtc")]
        public DateTimeOffset ExpiryUtc { get; set; }

        [JsonPropertyName("verified")]
        public bool Verified { get; set; }
    }

    public sealed class RelayInboxBundleDto
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; set; }

        [JsonPropertyName("signatureValid")]
        public bool SignatureValid { get; set; }

        [JsonPropertyName("par2Status")]
        public string Par2Status { get; set; } = "";

        [JsonPropertyName("rejectionReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RejectionReason { get; set; }
    }

    /// <summary>Relay status for the dashboard (poller snapshot + config).</summary>
    public static (int Status, object Payload) GetRelayStatus(ServiceConfig config, RelayPoller? poller)
    {
        bool configured = !string.IsNullOrWhiteSpace(config.RelayFolder);
        bool paired = !string.IsNullOrEmpty(config.RelayPairingSecretProtected);
        RelayPollerStatus poll = poller?.Status ?? RelayPollerStatus.Disabled;
        var dto = new RelayStatusDto
        {
            Configured = configured,
            Paired = paired,
            RelayFolder = config.RelayFolder,
            State = poll.State.ToString(),
            LastPollUtc = poll.LastPollUtc,
            BundlesImported = poll.BundlesImported,
            BundlesRejected = poll.BundlesRejected,
            DropsPruned = poll.DropsPruned,
            LastError = poll.LastError,
        };
        foreach (PendingApproval approval in poll.PendingApprovals)
        {
            dto.PendingApprovals.Add(new RelayPendingApprovalDto
            {
                Id = approval.Id,
                CreatedUtc = approval.CreatedUtc,
                BodyJson = approval.BodyJson,
            });
        }
        return (200, dto);
    }

    /// <summary>
    /// Pairs a phone: generates a fresh pairing secret, DPAPI-protects it
    /// into the config (with the relay folder), and returns the QR payload
    /// (JSON + SVG) for the desktop to display. Re-pairing replaces the
    /// previous secret — a previously paired phone stops verifying.
    /// </summary>
    public static (int Status, object Payload) PairRelay(
        ServiceConfig config,
        string? configPath,
        RelayPairRequestDto request,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(request.RelayFolder))
            return (400, new ErrorDto("relayFolder is required."));
        if (string.IsNullOrWhiteSpace(request.RelayBaseUrl)
            || !Uri.TryCreate(request.RelayBaseUrl, UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            return (400, new ErrorDto("relayBaseUrl must be an absolute http(s) URL."));

        byte[] seed = RelayCrypto.NewPairingSecret();
        try
        {
            string payload = RelayPairingPayload(request.RelayBaseUrl, seed);
            string qrSvg;
            using (var gen = new QRCodeGenerator())
            using (var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M))
                qrSvg = QrMatrixToSvg(data.ModuleMatrix, pixelsPerModule: 6);

            string protectedSeed;
            try
            {
                protectedSeed = Dpapi.Protect(Convert.ToBase64String(seed));
            }
            catch (Exception ex)
            {
                return (500, new ErrorDto($"Could not protect the pairing secret: {ex.Message}"));
            }

            lock (config.JobsLock)
            {
                config.RelayFolder = request.RelayFolder.Trim();
                config.RelayPairingSecretProtected = protectedSeed;
                if (configPath is not null)
                    config.Save(configPath);
            }
            log?.Invoke($"dashboard: relay paired (folder {request.RelayFolder})");
            return (200, new RelayPairingDto
            {
                Payload = payload,
                QrSvg = qrSvg,
                RelayFolder = request.RelayFolder.Trim(),
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Pairing payload JSON scanned by the phone (see <see cref="RelayPairingDto"/>).</summary>
    internal static string RelayPairingPayload(string relayBaseUrl, byte[] seed) =>
        "{\"v\":1,\"url\":\"" + JsonEscape(relayBaseUrl) + "\",\"seed\":\"" + Convert.ToBase64String(seed) + "\"}";

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Unpairs the phone: clears the pairing secret (the relay folder is
    /// kept, so re-pairing is one step). The poller goes idle until a new
    /// pairing; drops already published stay readable until they expire.
    /// </summary>
    public static (int Status, object Payload) UnpairRelay(
        ServiceConfig config,
        string? configPath,
        Action<string>? log = null)
    {
        lock (config.JobsLock)
        {
            config.RelayPairingSecretProtected = null;
            if (configPath is not null)
                config.Save(configPath);
        }
        log?.Invoke("dashboard: relay unpaired");
        return (200, new OkDto(true));
    }

    /// <summary>
    /// Beams a .kfb file on disk as a one-time relay drop: publishes it
    /// (signed, par2-paired, index-committed) so the phone can fetch it
    /// over HTTP. Returns the drop token + expiry.
    /// </summary>
    public static (int Status, object Payload) BeamBundle(
        ServiceConfig config,
        string kfbPath,
        string? name,
        int? lifetimeHours,
        Action<string>? log = null)
    {
        if (!TryGetRelayWriter(config, out RelayWriter? writer, out string? error))
            return (400, new ErrorDto(error!));
        if (string.IsNullOrWhiteSpace(kfbPath) || !File.Exists(kfbPath))
            return (400, new ErrorDto("kfbPath does not exist."));
        if (!kfbPath.EndsWith(".kfb", StringComparison.OrdinalIgnoreCase))
            return (400, new ErrorDto("kfbPath must be a .kfb bundle file."));

        int hours = lifetimeHours ?? (int)TimeSpan.FromDays(7).TotalHours;
        hours = Math.Clamp(hours, 1, 24 * 30);
        string displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(kfbPath) : name.Trim();
        byte[] kfbBytes;
        try
        {
            kfbBytes = File.ReadAllBytes(kfbPath);
        }
        catch (Exception ex)
        {
            return (400, new ErrorDto($"Could not read the bundle: {ex.Message}"));
        }
        try
        {
            DropPublishResult result = writer!.PublishDrop(kfbBytes, displayName, TimeSpan.FromHours(hours));
            log?.Invoke($"dashboard: beamed {displayName} as drop {result.Token}");
            return (200, new RelayBeamDto
            {
                Token = result.Token,
                Name = result.Name,
                Size = result.Size,
                ExpiryUtc = result.ExpiryUtc,
            });
        }
        catch (Exception ex)
        {
            return (500, new ErrorDto($"Beam failed: {ex.Message}"));
        }
    }

    /// <summary>Lists current drops with per-drop signature verification.</summary>
    public static (int Status, object Payload) ListRelayDrops(ServiceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RelayFolder))
            return (400, new ErrorDto("Relay is not configured."));
        string dropsDir = Path.Combine(config.RelayFolder, "drops");
        var list = new List<RelayDropDto>();
        if (!Directory.Exists(dropsDir))
            return (200, list.ToArray());
        byte[]? hmacKey = TryDeriveHmacKey(config);
        foreach (string jsonPath in Directory.EnumerateFiles(dropsDir, "*.json"))
        {
            RelayDropMetadata? meta;
            try
            {
                meta = RelayJson.Deserialize(File.ReadAllText(jsonPath), RelayJson.Default, RelayJsonContext.Default.RelayDropMetadata);
            }
            catch
            {
                continue; // unreadable metadata: not a listable drop
            }
            if (meta is null)
                continue;
            list.Add(new RelayDropDto
            {
                Token = meta.Token,
                Name = meta.Name,
                Size = meta.Size,
                ExpiryUtc = meta.ExpiryUtc,
                Verified = hmacKey is not null && RelayCrypto.VerifyDropHmac(hmacKey, meta),
            });
        }
        list.Sort((a, b) => a.ExpiryUtc.CompareTo(b.ExpiryUtc));
        return (200, list.ToArray());
    }

    /// <summary>Live scan of the phone's inbox bundles (verification outcomes included).</summary>
    public static (int Status, object Payload) ListRelayInbox(ServiceConfig config)
    {
        if (!TryGetRelayInbox(config, out RelayInbox? inbox, out string? error))
            return (400, new ErrorDto(error!));
        var list = new List<RelayInboxBundleDto>();
        foreach (InboxBundle bundle in inbox!.ScanBundles())
        {
            list.Add(new RelayInboxBundleDto
            {
                Token = bundle.Token,
                Name = bundle.Name,
                Size = bundle.Size,
                CreatedUtc = bundle.CreatedUtc,
                SignatureValid = bundle.SignatureValid,
                Par2Status = bundle.Par2Status.ToString(),
                RejectionReason = bundle.RejectionReason,
            });
        }
        return (200, list.ToArray());
    }

    private static bool TryGetRelayWriter(ServiceConfig config, out RelayWriter? writer, out string? error)
    {
        writer = null;
        error = null;
        if (string.IsNullOrWhiteSpace(config.RelayFolder))
        {
            error = "Relay is not configured (no relay folder).";
            return false;
        }
        if (!TryDeriveKeys(config, out byte[]? hmacKey, out byte[]? encKey, out error))
            return false;
        writer = new RelayWriter(config.RelayFolder, hmacKey!, encKey!);
        return true;
    }

    private static bool TryGetRelayInbox(ServiceConfig config, out RelayInbox? inbox, out string? error)
    {
        inbox = null;
        error = null;
        if (string.IsNullOrWhiteSpace(config.RelayFolder))
        {
            error = "Relay is not configured (no relay folder).";
            return false;
        }
        if (!TryDeriveKeys(config, out byte[]? hmacKey, out byte[]? encKey, out error))
            return false;
        var writer = new RelayWriter(config.RelayFolder, hmacKey!, encKey!);
        inbox = new RelayInbox(config.RelayFolder, hmacKey!, encKey!, writer);
        return true;
    }

    private static bool TryDeriveKeys(ServiceConfig config, out byte[]? hmacKey, out byte[]? encKey, out string? error)
    {
        hmacKey = null;
        encKey = null;
        error = null;
        if (string.IsNullOrEmpty(config.RelayPairingSecretProtected))
        {
            error = "No phone is paired.";
            return false;
        }
        byte[] seed;
        try
        {
            seed = Convert.FromBase64String(Dpapi.Unprotect(config.RelayPairingSecretProtected));
        }
        catch (Exception ex)
        {
            error = $"Could not unlock the pairing secret: {ex.Message}";
            return false;
        }
        if (seed.Length != RelayCrypto.PairingSecretBytes)
        {
            error = "The stored pairing secret has the wrong length; re-pair the phone.";
            return false;
        }
        try
        {
            hmacKey = RelayCrypto.DeriveHmacKey(seed);
            encKey = RelayCrypto.DeriveEncKey(seed);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static byte[]? TryDeriveHmacKey(ServiceConfig config)
    {
        if (!TryDeriveKeys(config, out byte[]? hmacKey, out _, out _))
            return null;
        return hmacKey;
    }
}
