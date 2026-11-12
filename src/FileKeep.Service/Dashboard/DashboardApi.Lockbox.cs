using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileKeep.Core;
using FileKeep.Core.Bundle;
using FileKeep.Core.Json;
using FileKeep.Core.Relay;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — Lockbox (.kfb bundle) API (Windows GUI only; the iOS
/// rename is out of scope). The .kfb file extension stays; "Lockbox" is
/// the UI name for a bundle.
///
/// Two-way bridge, per the 2026-11-11 decisions:
///   - Create a Lockbox from files, or from a backup (delegates to
///     <see cref="ExportBundle"/>).
///   - Folder -&gt; Lockbox (whole folder, or one file), optionally
///     beamed to the paired phone through the relay.
///   - Lockbox -&gt; folder (import): a bundle's files become a stealth
///     folder, with the same fail-closed merge rule as
///     <see cref="ImportOfflineFolder"/>.
///   - Received Lockboxes: relay-inbox drops from the phone.
///
/// ROUTES (wired 2026-11-11, phase 2): Dashboard.cs registers the
/// routes below, and <see cref="DashboardJsonContext"/> carries the
/// DTO registrations. The GUI worker builds against the contract in
/// the file header below.
///
/// API CONTRACT (for the deferred routes + GUI worker):
///   POST /api/operations/create-lockbox   (multipart — see below)
///   POST /api/operations/create-lockbox-from-backup (JSON body)
///   POST /api/operations/folder-to-lockbox (JSON body)
///   POST /api/operations/lockbox-to-folder (multipart — see below)
///   POST /api/operations/import-received-lockbox (JSON body)
///   GET  /api/lockboxes/received          (no body)
///   DELETE /api/lockboxes/received/{token} (no body; CSRF-checked)
///
/// Multipart shape (follows the POST /api/operations/download-nzb
/// precedent — Dashboard.cs reads await request.ReadFormAsync(); the GUI
/// sends FormData with no Content-Type header):
///   - create-lockbox: one file part per file, part name "files", the
///     part's filename IS the path inside the Lockbox, '/'-separated
///     (e.g. filename "sub/name.txt"); plus one text field "options"
///     holding JSON: <see cref="CreateLockboxOptionsDto"/>
///     ({methods, requireAll, name, sendViaRelay, lifetimeHours}).
///     The route returns the bytes as a download named options.name
///     (default "lockbox.kfb") — unless sendViaRelay is true, in
///     which case the fresh Lockbox is beamed to the paired phone
///     and the route returns the <see cref="RelayBeamDto"/> JSON
///     instead (same shape as folder-to-lockbox).
///   - lockbox-to-folder: one file part named "lockbox" (the .kfb);
///     plus one text field "options" holding JSON:
///     <see cref="LockboxToFolderRequestDto"/>
///     ({folderName, location, unlock:{passphrase?, useYubiKey, slot?}}).
///   - JSON bodies (folder-to-lockbox, and create-from-backup which
///     shares the export-bundle route pattern): CSRF header
///     X-CSRF-Token, ReadFromJsonAsync&lt;JsonElement&gt;, source-generated
///     deserialize via DashboardJsonContext. Every DTO property carries
///     an explicit [JsonPropertyName], like ExportBundleRequestDto.
///   - All three download paths return a <see cref="LockboxDownloadDto"/>
///     ({bytes, fileName}); the deferred route does
///     Results.File(dto.Bytes, "application/octet-stream", dto.FileName).
///     The relay-send path returns a <see cref="RelayBeamDto"/> instead.
///   - A received drop STAYS in the inbox after import (Joel,
///     2026-11-11); DELETE /api/lockboxes/received/{token} removes
///     it manually via <see cref="DeleteReceivedLockbox"/> — the
///     drop's .kfb/.kfb.par2/.json only, never any folder.
///   - "Send to phone" is gated by the GUI on
///     GET /api/relay/status -&gt; RelayStatusDto.Paired; this file does
///     not build that check.
///   - No made-tracking in v1: created Lockboxes are download-only,
///     same as export today. A registry is a follow-up, not built here.
///
/// SHARED WRAP CORE: <see cref="BuildLockboxWraps"/> is the single
/// shared helper for building bundle wraps (passphrase PBKDF2 600k with
/// a 32-byte salt; YubiKey HMAC-SHA1 slot 2 via the provider with a 30s
/// touch timeout; AND = <see cref="DekSharing.Split"/> shares,
/// OR = full DEK in each wrap; KEKs/shares/challenges zeroed after
/// use). <see cref="ExportBundle"/> calls it too (rewired 2026-11-11,
/// phase 2); its former inline copy is deleted.
/// </summary>
public static partial class DashboardApi
{
	/// <summary>YubiKey touch timeout, same as ExportBundle (30s).</summary>
	private static readonly TimeSpan LockboxYubiKeyTouchTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Unlock method for a Lockbox. Same shape as ExportUnlockMethodDto.</summary>
	public sealed class LockboxUnlockMethodDto
	{
		[JsonPropertyName("type")]
		public string Type { get; set; } = ""; // "passphrase" or "yubikey-hmac-sha1"

		[JsonPropertyName("passphrase")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Passphrase { get; set; } // for passphrase type

		[JsonPropertyName("slot")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Slot { get; set; } // for yubikey type, default 2
	}

	/// <summary>
	/// The "options" JSON text field of the create-lockbox multipart
	/// form (file parts carry the bytes; their filenames are the
	/// in-Lockbox paths).
	/// </summary>
	public sealed class CreateLockboxOptionsDto
	{
		[JsonPropertyName("methods")]
		public List<LockboxUnlockMethodDto> Methods { get; set; } = new();

		[JsonPropertyName("requireAll")]
		public bool RequireAll { get; set; } // true = AND, false = OR

		[JsonPropertyName("name")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Name { get; set; } // download filename without .kfb; default "lockbox"

		[JsonPropertyName("sendViaRelay")]
		public bool SendViaRelay { get; set; } // true = beam to the paired phone instead of downloading

		[JsonPropertyName("lifetimeHours")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? LifetimeHours { get; set; } // relay drop lifetime; BeamBundle clamps 1h..30d
	}

	/// <summary>JSON body for create-from-backup (same route pattern as export-bundle).</summary>
	public sealed class LockboxFromBackupRequestDto
	{
		[JsonPropertyName("backupId")]
		public string BackupId { get; set; } = "";

		[JsonPropertyName("methods")]
		public List<LockboxUnlockMethodDto> Methods { get; set; } = new();

		[JsonPropertyName("requireAll")]
		public bool RequireAll { get; set; } // true = AND, false = OR

		[JsonPropertyName("name")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Name { get; set; } // download filename without .kfb; default "backup"

		[JsonPropertyName("sendViaRelay")]
		public bool SendViaRelay { get; set; } // true = beam to the paired phone instead of downloading

		[JsonPropertyName("lifetimeHours")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? LifetimeHours { get; set; } // relay drop lifetime; BeamBundle clamps 1h..30d
	}

	/// <summary>JSON body for folder-to-lockbox.</summary>
	public sealed class FolderToLockboxRequestDto
	{
		[JsonPropertyName("folderName")]
		public string FolderName { get; set; } = "";

		[JsonPropertyName("scope")]
		public string Scope { get; set; } = OfflineExport.ScopeFolder; // "folder" or "file"

		[JsonPropertyName("filePath")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? FilePath { get; set; } // required when scope is "file"

		[JsonPropertyName("methods")]
		public List<LockboxUnlockMethodDto> Methods { get; set; } = new();

		[JsonPropertyName("requireAll")]
		public bool RequireAll { get; set; } // true = AND, false = OR

		[JsonPropertyName("sendViaRelay")]
		public bool SendViaRelay { get; set; } // true = beam to the paired phone

		[JsonPropertyName("name")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Name { get; set; } // drop/download name without .kfb; default folder name

		[JsonPropertyName("lifetimeHours")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? LifetimeHours { get; set; } // relay drop lifetime; BeamBundle clamps 1h..30d
	}

	/// <summary>How to unlock an uploaded Lockbox for import.</summary>
	public sealed class LockboxUnlockDto
	{
		[JsonPropertyName("passphrase")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Passphrase { get; set; }

		[JsonPropertyName("useYubiKey")]
		public bool UseYubiKey { get; set; }

		[JsonPropertyName("slot")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? Slot { get; set; } // default 2
	}

	/// <summary>
	/// The "options" JSON text field of the lockbox-to-folder multipart
	/// form (the .kfb itself arrives as the "lockbox" file part).
	/// </summary>
	public sealed class LockboxToFolderRequestDto
	{
		[JsonPropertyName("folderName")]
		public string FolderName { get; set; } = "";

		[JsonPropertyName("location")]
		public string Location { get; set; } = ""; // location name, same picker values as stealth-folder creation

		[JsonPropertyName("unlock")]
		public LockboxUnlockDto Unlock { get; set; } = new();
	}

	/// <summary>JSON body for import-received-lockbox (token, not bytes).</summary>
	public sealed class ImportReceivedLockboxRequestDto
	{
		[JsonPropertyName("token")]
		public string Token { get; set; } = ""; // relay-inbox drop token (the .kfb filename stem)

		[JsonPropertyName("folderName")]
		public string FolderName { get; set; } = "";

		[JsonPropertyName("location")]
		public string Location { get; set; } = ""; // location name, same picker values as stealth-folder creation

		[JsonPropertyName("unlock")]
		public LockboxUnlockDto Unlock { get; set; } = new();
	}

	/// <summary>
	/// A Lockbox ready to download: the bytes plus the filename the
	/// deferred route hands to Results.File. The route never invents
	/// its own filename — it uses this one.
	/// </summary>
	public sealed class LockboxDownloadDto
	{
		[JsonPropertyName("fileName")]
		public string FileName { get; set; } = "";

		[JsonIgnore]
		public byte[] Bytes { get; set; } = Array.Empty<byte>();
	}

	/// <summary>Import summary for lockbox-to-folder.</summary>
	public sealed class LockboxToFolderResultDto
	{
		[JsonPropertyName("folderName")]
		public string FolderName { get; set; } = "";

		[JsonPropertyName("fileCount")]
		public int FileCount { get; set; }

		[JsonPropertyName("files")]
		public List<string> Files { get; set; } = new();

		[JsonPropertyName("merged")]
		public bool Merged { get; set; } // true when the folder already existed and new paths were appended
	}

	/// <summary>One relay-received Lockbox (a phone drop in inbox/laptop/).</summary>
	public sealed class ReceivedLockboxDto
	{
		[JsonPropertyName("token")]
		public string Token { get; set; } = "";

		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("size")]
		public long Size { get; set; }

		[JsonPropertyName("receivedUtc")]
		public DateTimeOffset ReceivedUtc { get; set; }

		[JsonPropertyName("signatureValid")]
		public bool SignatureValid { get; set; }

		[JsonPropertyName("par2Status")]
		public string Par2Status { get; set; } = "";

		[JsonPropertyName("rejectionReason")]
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? RejectionReason { get; set; }
	}

	/// <summary>
	/// Validates an unlock-method list with exactly ExportBundle's
	/// rules and messages: at least one method, known types only,
	/// passphrase present for passphrase methods, AND needs 2+.
	/// Returns the error message, or null when valid.
	/// </summary>
	internal static string? ValidateLockboxMethods(
		IReadOnlyList<LockboxUnlockMethodDto>? methods, bool requireAll)
	{
		if (methods is null || methods.Count == 0)
			return "At least one unlock method is required.";
		foreach (var m in methods)
		{
			if (m.Type != "passphrase" && m.Type != "yubikey-hmac-sha1")
				return $"Unsupported unlock method: {m.Type}";
			if (m.Type == "passphrase" && string.IsNullOrEmpty(m.Passphrase))
				return "Passphrase is required for passphrase method.";
		}
		if (requireAll && methods.Count < 2)
			return "AND requires at least 2 methods.";
		return null;
	}

	/// <summary>
	/// THE shared wrap-building core. Builds the <see cref="BundleWrap"/>s
	/// for a DEK from the caller's methods: passphrase wraps derive
	/// their KEK with PBKDF2-HMAC-SHA-512, 600,000 iterations, a fresh
	/// 32-byte salt; YubiKey wraps replay a fresh 32-byte challenge
	/// through the provider (slot defaults to 2, 30s touch timeout —
	/// same errors as ExportBundle: timeout -&gt; 400, no provider -&gt;
	/// 500, other provider errors -&gt; 500). AND (requireAll) splits
	/// the DEK with <see cref="DekSharing.Split"/> so every wrap holds
	/// one share (mode "and", group "g1"); OR gives every wrap the full
	/// DEK. KEKs, challenges, and shares are zeroed before return on
	/// every path. The caller owns the DEK and zeroes it.
	/// </summary>
	internal static (List<BundleWrap>? Wraps, int Status, string? Error) BuildLockboxWraps(
		byte[] dek,
		IReadOnlyList<LockboxUnlockMethodDto> methods,
		bool requireAll,
		IYubiKeyProvider? yubiKeyProvider)
	{
		var wraps = new List<BundleWrap>();
		var wrapInfos = new List<(string Type, byte[] Kek, byte[]? Salt, int? Iterations, int? Slot, byte[]? Challenge)>();
		try
		{
			foreach (var m in methods)
			{
				if (m.Type == "passphrase")
				{
					byte[] salt = RandomNumberGenerator.GetBytes(32);
					const int iterations = 600000;
					byte[] kek = KeyDerivation.DeriveKey(m.Passphrase!, salt, iterations);
					wrapInfos.Add(("passphrase", kek, salt, iterations, null, null));
				}
				else if (m.Type == "yubikey-hmac-sha1")
				{
					if (yubiKeyProvider is null)
						return (null, 500, "YubiKey provider not available.");
					int slot = m.Slot ?? 2;
					byte[] challenge = RandomNumberGenerator.GetBytes(32);
					byte[] hmacResponse;
					try
					{
						hmacResponse = yubiKeyProvider.CalculateHmacSha1(
							challenge, slot, LockboxYubiKeyTouchTimeout);
					}
					catch (TimeoutException)
					{
						return (null, 400, "YubiKey touch timed out.");
					}
					catch (Exception ex)
					{
						return (null, 500, $"YubiKey error: {ex.Message}");
					}
					byte[] kek = YubiKeyCrypto.DeriveKek(hmacResponse);
					CryptographicOperations.ZeroMemory(hmacResponse);
					wrapInfos.Add(("yubikey-hmac-sha1", kek, null, null, slot, challenge));
				}
			}

			if (requireAll && wrapInfos.Count > 1)
			{
				byte[][] shares = DekSharing.Split(dek, wrapInfos.Count);
				try
				{
					for (int i = 0; i < wrapInfos.Count; i++)
					{
						var (type, kek, salt, iterations, slot, challenge) = wrapInfos[i];
						wraps.Add(BundleWriter.CreateWrap(
							type, kek, shares[i],
							mode: "and", group: "g1",
							salt: salt, iterations: iterations,
							slot: slot, challenge: challenge));
					}
				}
				finally
				{
					foreach (var s in shares)
						CryptographicOperations.ZeroMemory(s);
				}
			}
			else
			{
				foreach (var (type, kek, salt, iterations, slot, challenge) in wrapInfos)
				{
					wraps.Add(BundleWriter.CreateWrap(
						type, kek, dek,
						salt: salt, iterations: iterations,
						slot: slot, challenge: challenge));
				}
			}
			return (wraps, 200, null);
		}
		finally
		{
			foreach (var (_, kek, _, _, _, challenge) in wrapInfos)
			{
				CryptographicOperations.ZeroMemory(kek);
				if (challenge is not null)
					CryptographicOperations.ZeroMemory(challenge);
			}
		}
	}

	/// <summary>
	/// Builds Lockbox bytes from in-memory plaintext files via the
	/// shared wrap core. Takes ownership of the plaintext buffers:
	/// they are zeroed before return on every path (success or
	/// failure), exactly like ExportBundle zeroes the files it
	/// restored. Returns (bytes, 200, null) or (null, status, error).
	/// </summary>
	internal static (byte[]? Bytes, int Status, string? Error) BuildLockboxBytes(
		IReadOnlyList<(string Path, byte[] Plaintext)> files,
		IReadOnlyList<LockboxUnlockMethodDto> methods,
		bool requireAll,
		IYubiKeyProvider? yubiKeyProvider)
	{
		byte[] dek = RandomNumberGenerator.GetBytes(KeyDerivation.KeySizeBytes);
		try
		{
			var (wraps, status, error) = BuildLockboxWraps(dek, methods, requireAll, yubiKeyProvider);
			if (wraps is null)
				return (null, status, error);
			return (BundleWriter.Create(wraps, dek, files), 200, null);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(dek);
			foreach (var (_, plaintext) in files)
				if (plaintext is not null)
					CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	/// <summary>
	/// THE shared beam helper: beams already-built Lockbox bytes to
	/// the paired phone. Stages the bytes to a temp
	/// <c>fk-lockbox-&lt;guid&gt;.kfb</c> (BeamBundle takes a .kfb path
	/// on disk), calls <see cref="BeamBundle"/>, and deletes the temp
	/// file in <c>finally</c> on EVERY path (success, beam error, or
	/// staging exception) — the regression gate is
	/// <c>FolderToLockbox_SendViaRelay_Unconfigured_Returns400_AndDeletesTemp</c>.
	/// The bytes are zeroed in <c>finally</c> too: callers must not
	/// use them afterwards (the download path never reaches here).
	/// Returns BeamBundle's (status, <see cref="RelayBeamDto"/>) on
	/// success, or (status, <see cref="ErrorDto"/>) on failure —
	/// unconfigured/unpaired relay → 400 "Relay is not configured."
	/// (or "No phone is paired.") via TryGetRelayInbox.
	/// </summary>
	public static (int Status, object Payload) BeamLockboxBytes(
		ServiceConfig config,
		byte[] bytes,
		string displayName,
		int? lifetimeHours,
		Action<string>? log = null)
	{
		string tempPath = Path.Combine(Path.GetTempPath(), $"fk-lockbox-{Guid.NewGuid():N}.kfb");
		try
		{
			File.WriteAllBytes(tempPath, bytes);
			var (beamStatus, beamPayload) = BeamBundle(
				config, tempPath, displayName, lifetimeHours, log);
			return (beamStatus, beamPayload);
		}
		catch (Exception ex)
		{
			return (500, new ErrorDto($"Could not stage the Lockbox for beaming: {ex.Message}"));
		}
		finally
		{
			try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	/// <summary>
	/// Creates a Lockbox from in-memory files. Returns
	/// (200, <see cref="LockboxDownloadDto"/>) on success, or
	/// (status, <see cref="ErrorDto"/>). When
	/// <paramref name="sendViaRelay"/> is true the fresh Lockbox is
	/// beamed to the paired phone instead of downloaded, via
	/// <see cref="BeamLockboxBytes"/> (which zeroes the bytes), and
	/// the beam result is returned as-is.
	/// </summary>
	public static (int Status, object Payload) CreateLockboxFromFiles(
		ServiceConfig? config,
		IReadOnlyList<(string Path, byte[] Plaintext)> files,
		IReadOnlyList<LockboxUnlockMethodDto> methods,
		bool requireAll,
		string? name = null,
		bool sendViaRelay = false,
		int? lifetimeHours = null,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (files is null || files.Count == 0)
			return (400, new ErrorDto("At least one file is required."));
		foreach (var (path, plaintext) in files)
		{
			if (string.IsNullOrWhiteSpace(path))
				return (400, new ErrorDto("File path must not be empty."));
			if (plaintext is null)
				return (400, new ErrorDto($"File '{path}' has no content."));
		}
		if (ValidateLockboxMethods(methods, requireAll) is { } error)
			return (400, new ErrorDto(error));
		var (bytes, status, buildError) = BuildLockboxBytes(files, methods, requireAll, yubiKeyProvider);
		if (bytes is null)
			return (status, new ErrorDto(buildError ?? "Could not build the Lockbox."));
		log?.Invoke($"lockbox: created from {files.Count} file(s)");
		if (!sendViaRelay)
		{
			return (200, new LockboxDownloadDto
			{
				Bytes = bytes,
				FileName = DownloadFileName(name, "lockbox.kfb"),
			});
		}
		if (config is null)
		{
			CryptographicOperations.ZeroMemory(bytes);
			return (400, new ErrorDto("Relay is not configured."));
		}
		return BeamLockboxBytes(config, bytes, DownloadFileName(name, "lockbox.kfb"), lifetimeHours, log);
	}

	/// <summary>
	/// Download-only overload of <see cref="CreateLockboxFromFiles"/>:
	/// no relay is involved, so no config is needed. Kept so the
	/// existing download-only callers (LockboxApiTests,
	/// LockboxReceivedImportTests) need no change; the route and the
	/// beam tests use the config-first overload above.
	/// </summary>
	public static (int Status, object Payload) CreateLockboxFromFiles(
		IReadOnlyList<(string Path, byte[] Plaintext)> files,
		IReadOnlyList<LockboxUnlockMethodDto> methods,
		bool requireAll,
		string? name = null,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null) =>
		CreateLockboxFromFiles(null, files, methods, requireAll, name,
			sendViaRelay: false, null, yubiKeyProvider, cancellationToken, log);

	/// <summary>
	/// Creates a Lockbox from a backup. DELEGATES to
	/// <see cref="ExportBundle"/> — the restore-to-temp path lives
	/// there and is not duplicated here. Returns
	/// (200, <see cref="LockboxDownloadDto"/>) on success; the
	/// FileName is request.Name (default "backup") with a .kfb suffix,
	/// so the deferred route names the download
	/// <c>&lt;name&gt;.kfb</c> instead of ExportBundle's hardcoded
	/// "backup.kfb". When request.SendViaRelay is true the fresh
	/// Lockbox is beamed to the paired phone instead (via
	/// <see cref="BeamLockboxBytes"/>) and the beam result is
	/// returned as-is. Errors pass through unchanged.
	/// </summary>
	public static (int Status, object Payload) CreateLockboxFromBackup(
		ServiceConfig config,
		string repo,
		LockboxFromBackupRequestDto request,
		RestoreSession? session = null,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		var exportRequest = new ExportBundleRequestDto
		{
			BackupId = request.BackupId,
			Methods = request.Methods.Select(m => new ExportUnlockMethodDto
			{
				Type = m.Type,
				Passphrase = m.Passphrase,
				Slot = m.Slot,
			}).ToList(),
			RequireAll = request.RequireAll,
		};
		var (status, payload) = ExportBundle(
			config, repo, exportRequest, session, yubiKeyProvider, cancellationToken, log);
		if (status != 200 || payload is not byte[] bytes)
			return (status, payload);
		string fileName = DownloadFileName(request.Name, "backup.kfb");
		if (request.SendViaRelay)
			return BeamLockboxBytes(config, bytes, fileName, request.LifetimeHours, log);
		return (200, new LockboxDownloadDto
		{
			Bytes = bytes,
			FileName = fileName,
		});
	}

	/// <summary>
	/// Folder -&gt; Lockbox. Reads the folder's files' PLAINTEXT bytes
	/// through the existing offline-store read path — the same calls
	/// the DAV GET handler makes (<see cref="OfflineFoldersSession.CopyContentKey"/>
	/// for the content key, <see cref="OfflineFileStore.ResolveStore"/>
	/// for the location's blob store, <see cref="OfflineFileStore.Retrieve"/>
	/// for reassembly) — no decryption is reimplemented here. Scope
	/// "folder" takes the whole folder; scope "file" takes the single
	/// file named by request.FilePath (mirrors
	/// <see cref="OfflineExport.ScopeFolder"/> /
	/// <see cref="OfflineExport.ScopeFile"/>). When
	/// request.SendViaRelay is true, the .kfb is beamed with
	/// <see cref="BeamLockboxBytes"/> (temp stage + <see cref="BeamBundle"/>,
	/// temp deleted on every path) and the beam result is returned
	/// as-is. Otherwise the bytes are returned for download. The GUI
	/// gates "Send to phone" on GET /api/relay/status -&gt;
	/// RelayStatusDto.Paired — that check is not built here.
	/// </summary>
	public static (int Status, object Payload) FolderToLockbox(
		ServiceConfig config,
		OfflineFoldersSession session,
		string catalogPath,
		FolderToLockboxRequestDto request,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		if (ValidateLockboxMethods(request.Methods, request.RequireAll) is { } methodError)
			return (400, new ErrorDto(methodError));
		if (request.Scope is not (OfflineExport.ScopeFolder or OfflineExport.ScopeFile))
			return (400, new ErrorDto("Scope must be \"folder\" or \"file\"."));
		var entries = session.GetEntries();
		if (entries is null)
			return (403, new ErrorDto("Offline Folders is locked."));
		var folder = entries.FirstOrDefault(e =>
			string.Equals(e.Name, request.FolderName, StringComparison.OrdinalIgnoreCase));
		if (folder is null)
			return (404, new ErrorDto("No such offline folder."));

		List<OfflineFileEntry> targets;
		if (request.Scope == OfflineExport.ScopeFile)
		{
			if (string.IsNullOrWhiteSpace(request.FilePath))
				return (400, new ErrorDto("filePath is required for file scope."));
			if (OfflineFoldersCatalog.ValidateFilePath(request.FilePath) is { } pathError)
				return (400, new ErrorDto(pathError));
			string trimmed = request.FilePath.Trim();
			var file = folder.Files.FirstOrDefault(f =>
				string.Equals(f.Path, trimmed, StringComparison.OrdinalIgnoreCase));
			if (file is null)
				return (404, new ErrorDto("No such offline file."));
			targets = new List<OfflineFileEntry> { file };
		}
		else
		{
			targets = folder.Files.ToList();
		}
		if (targets.Count == 0)
			return (400, new ErrorDto("Folder contains no files to export."));

		var location = config.SnapshotLocations().FirstOrDefault(l =>
			string.Equals(l.Name, folder.LocationId, StringComparison.OrdinalIgnoreCase));
		if (location is null)
			return (400, new ErrorDto("That folder's location no longer exists."));
		byte[]? contentKey = session.CopyContentKey();
		if (contentKey is null)
			return (403, new ErrorDto("Offline Folders is locked."));

		var files = new List<(string Path, byte[] Plaintext)>();
		try
		{
			OfflineFileStore.OfflineLocationStore store;
			try { store = OfflineFileStore.ResolveStore(location); }
			catch (InvalidOperationException ex) { return (400, new ErrorDto(ex.Message)); }
			using (store)
			{
				foreach (var file in targets)
				{
					cancellationToken.ThrowIfCancellationRequested();
					using var ms = new MemoryStream();
					try
					{
						OfflineFileStore.Retrieve(file.ChunkIds, store.Store, contentKey, ms,
							() => session.IsUnlocked);
					}
					catch (InvalidDataException ex) { return (400, new ErrorDto(ex.Message)); }
					catch (CryptographicException)
					{
						return (400, new ErrorDto("The file's stored content failed verification."));
					}
					files.Add((file.Path, ms.ToArray()));
				}
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(contentKey);
		}

		string defaultName = request.Scope == OfflineExport.ScopeFile
			? $"{folder.Name}-{FileBaseName(targets[0].Path)}"
			: folder.Name;
		string displayName = DownloadFileName(request.Name, defaultName + ".kfb");

		var (bytes, status, buildError) = BuildLockboxBytes(files, request.Methods, request.RequireAll, yubiKeyProvider);
		if (bytes is null)
			return (status, new ErrorDto(buildError ?? "Could not build the Lockbox."));

		if (!request.SendViaRelay)
		{
			log?.Invoke($"lockbox: folder '{folder.Name}' exported ({files.Count} file(s))");
			return (200, new LockboxDownloadDto { Bytes = bytes, FileName = displayName });
		}

		// Beam needs a .kfb path on disk: the shared
		// BeamLockboxBytes stages to temp, beams, and deletes the
		// temp on every path (success, beam error, or exception).
		return BeamLockboxBytes(config, bytes, displayName, request.LifetimeHours, log);
	}

	/// <summary>
	/// Lockbox -&gt; folder (import). Opens the uploaded .kfb with the
	/// supplied unlock and writes each file into a stealth folder —
	/// see <see cref="ImportLockboxBytes"/> for the shared core (merge
	/// semantics, errors). Thin wrapper: validates the uploaded bytes
	/// and delegates.
	/// </summary>
	public static (int Status, object Payload) LockboxToFolder(
		ServiceConfig config,
		OfflineFoldersSession session,
		string catalogPath,
		byte[] lockboxBytes,
		LockboxToFolderRequestDto request,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		return ImportLockboxBytes(config, session, catalogPath, lockboxBytes,
			request.FolderName, request.Location, request.Unlock,
			yubiKeyProvider, cancellationToken, log);
	}

	/// <summary>
	/// THE shared Lockbox-import core, used by both
	/// <see cref="LockboxToFolder"/> (uploaded .kfb bytes) and
	/// <see cref="ImportReceivedLockbox"/> (bytes resolved from a
	/// relay-inbox token). Opens the .kfb with the supplied unlock
	/// (passphrase and/or YubiKey touch — the same wrap semantics the
	/// writer produces: OR wraps unlock alone, an AND group needs
	/// every share), then writes each file into a stealth folder
	/// through the existing <see cref="StoreOfflineFile"/> chunk-write
	/// path (no chunking or encryption is reimplemented here). The
	/// destination is a location name, the same picker values
	/// stealth-folder creation uses; a missing folder is created
	/// first with <see cref="AddOfflineFolder"/>.
	///
	/// MERGE SEMANTICS — copied exactly from
	/// <see cref="ImportOfflineFolder"/> (fail-closed): an existing
	/// folder merges (new paths are appended), but a same-path entry
	/// is NEVER silently replaced — the import is refused with a 400
	/// naming the conflicting path, so the user deletes it first and
	/// re-imports deliberately. Files are validated up front, but
	/// stores happen one by one, so a mid-import store failure leaves
	/// the earlier files stored (unlike ImportOfflineFolder, which is
	/// catalog-atomic because it moves no data); the error names the
	/// file that failed.
	/// </summary>
	internal static (int Status, object Payload) ImportLockboxBytes(
		ServiceConfig config,
		OfflineFoldersSession session,
		string catalogPath,
		byte[] lockboxBytes,
		string folderName,
		string location,
		LockboxUnlockDto unlock,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		if (lockboxBytes is null || lockboxBytes.Length == 0)
			return (400, new ErrorDto("No Lockbox file was uploaded."));
		if ((long)lockboxBytes.Length > RelayInbox.MaxBundleBytes)
			return (400, new ErrorDto(
				$"Lockbox is {lockboxBytes.Length} bytes, over the {RelayInbox.MaxBundleBytes}-byte size limit."));
		if (OfflineFoldersCatalog.ValidateName(folderName) is { } nameError)
			return (400, new ErrorDto(nameError));
		string trimmedFolder = folderName.Trim();
		var entries = session.GetEntries();
		if (entries is null)
			return (403, new ErrorDto("Offline Folders is locked."));
		var locationEntry = config.SnapshotLocations().FirstOrDefault(l =>
			string.Equals(l.Name, location, StringComparison.OrdinalIgnoreCase));
		if (locationEntry is null)
			return (400, new ErrorDto("That location does not exist."));

		var (header, files, openError) = OpenLockbox(lockboxBytes, unlock, yubiKeyProvider);
		if (header is null || files is null)
			return (400, new ErrorDto(openError ?? "Could not open the Lockbox."));
		try
		{
			foreach (var (path, _) in files)
			{
				if (OfflineFoldersCatalog.ValidateFilePath(path) is { } pathError)
					return (400, new ErrorDto(pathError));
			}

			var existing = entries.FirstOrDefault(e =>
				string.Equals(e.Name, trimmedFolder, StringComparison.OrdinalIgnoreCase));
			bool merged;
			if (existing is not null)
			{
				// Fail-closed merge, exactly like ImportOfflineFolder:
				// new paths are appended, but a same-path entry is never
				// silently replaced — refuse and name the conflict.
				var conflicts = files
					.Where(f => existing.Files.Any(e =>
						string.Equals(e.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
					.Select(f => f.Path)
					.ToList();
				if (conflicts.Count > 0)
					return (400, new ErrorDto(
						$"Already in folder '{existing.Name}': {string.Join(", ", conflicts)} — " +
						$"delete {(conflicts.Count == 1 ? "it" : "them")} first, then import again."));
				merged = true;
			}
			else
			{
				var (addStatus, addPayload) = AddOfflineFolder(
					config, session, catalogPath, trimmedFolder, locationEntry.Name, log);
				if (addStatus != 200)
					return (addStatus, addPayload);
				merged = false;
			}

			var storedPaths = new List<string>();
			foreach (var (path, plaintext) in files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var entry = header.Files.FirstOrDefault(e =>
					string.Equals(e.Path, path, StringComparison.Ordinal));
				using var content = new MemoryStream(plaintext, writable: false);
				var (storeStatus, storePayload) = StoreOfflineFile(
					config, session, catalogPath, trimmedFolder, path, content,
					entry?.ModifiedUtc ?? DateTimeOffset.UtcNow, log);
				if (storeStatus != 200)
					return (storeStatus, storePayload);
				storedPaths.Add(path);
			}
			log?.Invoke($"lockbox: imported {files.Count} file(s) into folder '{trimmedFolder}'{(merged ? " (merged)" : "")}");
			return (200, new LockboxToFolderResultDto
			{
				FolderName = trimmedFolder,
				FileCount = storedPaths.Count,
				Files = storedPaths,
				Merged = merged,
			});
		}
		finally
		{
			foreach (var (_, plaintext) in files)
				CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	/// <summary>
	/// Import a received Lockbox by token (the one-click import for a
	/// row in GET /api/lockboxes/received). The token resolves to the
	/// drop's .kfb through the existing relay-inbox scan —
	/// <see cref="RelayInbox.ScanBundles"/> via
	/// <see cref="ListRelayInbox"/> — the token is the .kfb filename
	/// stem in inbox/laptop/, and the scan verifies the signed .json
	/// sidecar before the bytes are trusted. The token is only ever
	/// matched against scanned tokens, never used as a path, so a
	/// hostile token cannot escape the inbox. The resolved bytes then
	/// go through the shared <see cref="ImportLockboxBytes"/> core,
	/// so the merge rule and errors are identical to
	/// lockbox-to-folder. Unknown token → 404 (same convention as
	/// FolderToLockbox's "No such offline folder"); a drop whose
	/// signature failed → 400 naming the rejection reason (only
	/// <see cref="InboxBundle.SignatureValid"/> bundles are
	/// importable). Relay not configured / no phone paired → 400,
	/// same as the received list.
	/// </summary>
	public static (int Status, object Payload) ImportReceivedLockbox(
		ServiceConfig config,
		OfflineFoldersSession session,
		string catalogPath,
		ImportReceivedLockboxRequestDto request,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		if (!TryGetRelayInbox(config, out RelayInbox? inbox, out string? error))
			return (400, new ErrorDto(error!));
		if (inbox is null)
			return (500, new ErrorDto("Relay inbox is unavailable."));
		return ImportReceivedLockbox(config, inbox, session, catalogPath, request,
			yubiKeyProvider, cancellationToken, log);
	}

	/// <summary>
	/// Test seam: import-by-token over an explicit
	/// <see cref="RelayInbox"/> (tests drive it with a simulated
	/// phone drop instead of a paired relay).
	/// </summary>
	internal static (int Status, object Payload) ImportReceivedLockbox(
		ServiceConfig config,
		RelayInbox inbox,
		OfflineFoldersSession session,
		string catalogPath,
		ImportReceivedLockboxRequestDto request,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		ArgumentNullException.ThrowIfNull(inbox);
		if (string.IsNullOrWhiteSpace(request.Token))
			return (404, new ErrorDto("No received Lockbox with that token."));
		var bundle = inbox.ScanBundles().FirstOrDefault(b =>
			string.Equals(b.Token, request.Token.Trim(), StringComparison.Ordinal));
		if (bundle is null)
			return (404, new ErrorDto("No received Lockbox with that token."));
		if (!bundle.SignatureValid)
			return (400, new ErrorDto(
				bundle.RejectionReason ?? "The received Lockbox failed signature verification."));
		byte[] lockboxBytes;
		try
		{
			lockboxBytes = File.ReadAllBytes(bundle.KfbPath);
		}
		catch (Exception ex)
		{
			return (400, new ErrorDto($"Could not read the received Lockbox: {ex.Message}"));
		}
		return ImportLockboxBytes(config, session, catalogPath, lockboxBytes,
			request.FolderName, request.Location, request.Unlock,
			yubiKeyProvider, cancellationToken, log);
	}

	/// <summary>
	/// Received Lockboxes: relay-received .kfb drops from the phone
	/// (the inbox scan — <see cref="RelayInbox.ScanBundles"/> via
	/// <see cref="ListRelayInbox"/>; <c>RelayPoller</c> saves received
	/// drops into inbox/laptop/). Returns
	/// (200, <see cref="ReceivedLockboxDto"/>[]) on success, or the
	/// inbox error (400 when the relay is not configured or no phone
	/// is paired).
	/// </summary>
	public static (int Status, object Payload) ListReceivedLockboxes(ServiceConfig config)
	{
		var (status, payload) = ListRelayInbox(config);
		if (status != 200)
			return (status, payload);
		var inbox = (IReadOnlyList<RelayInboxBundleDto>)payload;
		return (200, inbox.Select(MapReceivedLockbox).ToArray());
	}

	/// <summary>
	/// Test seam: the same listing over an explicit
	/// <see cref="RelayInbox"/> (tests drive it with a simulated
	/// phone drop instead of a paired relay).
	/// </summary>
	internal static (int Status, object Payload) ListReceivedLockboxes(RelayInbox inbox)
	{
		ArgumentNullException.ThrowIfNull(inbox);
		return (200, inbox.ScanBundles().Select(MapReceivedLockbox).ToArray());
	}

	/// <summary>
	/// Deletes one received Lockbox from the relay inbox by token —
	/// the manual per-row Delete in GET /api/lockboxes/received.
	/// An imported drop STAYS in the inbox (Joel, 2026-11-11); this
	/// is the only way it leaves. Follows
	/// <see cref="ImportReceivedLockbox"/>'s pattern exactly:
	/// TryGetRelayInbox → 400 when the relay is not configured or no
	/// phone is paired; the token is matched against the scanned
	/// bundles by ordinal equality (never used as a path), unknown
	/// token → 404. Deletes ONLY the drop's .kfb/.kfb.par2/.json via
	/// <see cref="RelayInbox.DeleteBundle"/> — it never touches any
	/// folder, so deleting after an import leaves the imported
	/// folder intact. Returns (200, <see cref="OkDto"/>(true)).
	/// </summary>
	public static (int Status, object Payload) DeleteReceivedLockbox(ServiceConfig config, string token)
	{
		if (!TryGetRelayInbox(config, out RelayInbox? inbox, out string? error))
			return (400, new ErrorDto(error!));
		if (inbox is null)
			return (500, new ErrorDto("Relay inbox is unavailable."));
		return DeleteReceivedLockbox(config, inbox, token);
	}

	/// <summary>
	/// Test seam: delete-by-token over an explicit
	/// <see cref="RelayInbox"/> (tests drive it with a simulated
	/// phone drop instead of a paired relay; the pairing secret
	/// needs DPAPI, which is Windows-only).
	/// </summary>
	internal static (int Status, object Payload) DeleteReceivedLockbox(
		ServiceConfig config, RelayInbox inbox, string token)
	{
		ArgumentNullException.ThrowIfNull(inbox);
		if (string.IsNullOrWhiteSpace(token))
			return (404, new ErrorDto("No received Lockbox with that token."));
		var bundle = inbox.ScanBundles().FirstOrDefault(b =>
			string.Equals(b.Token, token.Trim(), StringComparison.Ordinal));
		if (bundle is null)
			return (404, new ErrorDto("No received Lockbox with that token."));
		inbox.DeleteBundle(bundle.Token);
		return (200, new OkDto(true));
	}

	private static ReceivedLockboxDto MapReceivedLockbox(RelayInboxBundleDto dto) => new()
	{
		Token = dto.Token,
		Name = dto.Name,
		Size = dto.Size,
		ReceivedUtc = dto.CreatedUtc,
		SignatureValid = dto.SignatureValid,
		Par2Status = dto.Par2Status,
		RejectionReason = dto.RejectionReason,
	};

	private static ReceivedLockboxDto MapReceivedLockbox(InboxBundle bundle) => new()
	{
		Token = bundle.Token,
		Name = bundle.Name,
		Size = bundle.Size,
		ReceivedUtc = bundle.CreatedUtc,
		SignatureValid = bundle.SignatureValid,
		Par2Status = bundle.Par2Status.ToString(),
		RejectionReason = bundle.RejectionReason,
	};

	/// <summary>
	/// Sanitizes a download/drop name into a safe "<c>.kfb</c>"
	/// filename: trims, replaces Windows-invalid filename characters
	/// with '-', appends ".kfb" when missing. Falls back to
	/// <paramref name="fallback"/> (which must already end in .kfb)
	/// when the name is blank or sanitizes to nothing.
	/// </summary>
	public static string DownloadFileName(string? name, string fallback)
	{
		string raw = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
		var invalid = Path.GetInvalidFileNameChars();
		var sb = new System.Text.StringBuilder(raw.Length);
		foreach (char c in raw)
			sb.Append(invalid.Contains(c) ? '-' : c);
		string cleaned = sb.ToString().Trim().Trim('.');
		if (cleaned.Length == 0)
			return fallback;
		if (!cleaned.EndsWith(".kfb", StringComparison.OrdinalIgnoreCase))
			cleaned += ".kfb";
		return cleaned;
	}

	/// <summary>
	/// Opens a .kfb bundle: parses the "FKBO" header (magic + 4-byte
	/// little-endian header length + header JSON, exactly as
	/// <see cref="BundleWriter"/> writes it), unwraps the DEK from the
	/// supplied unlock, and AES-256-GCM decrypts every file segment.
	/// No new crypto is invented: passphrase KEKs use
	/// <see cref="KeyDerivation"/> with the wrap's own salt/iterations,
	/// YubiKey KEKs replay the wrap's challenge through the provider
	/// and <see cref="YubiKeyCrypto.DeriveKek"/>, AND shares recombine
	/// with <see cref="DekSharing.Combine"/>. The writer never emits
	/// TOTP wraps, so a bundle whose only wraps are TOTP (or any
	/// unknown type) cannot be opened here. Returns
	/// (header, files, null) or (null, null, error). The caller owns
	/// the returned plaintext buffers and must zero them after use.
	/// </summary>
	internal static (BundleHeader? Header, List<(string Path, byte[] Plaintext)>? Files, string? Error) OpenLockbox(
		byte[] bundleBytes,
		LockboxUnlockDto unlock,
		IYubiKeyProvider? yubiKeyProvider = null)
	{
		if (bundleBytes is null || bundleBytes.Length < 8)
			return (null, null, "Not a FileKeep Lockbox (.kfb) file.");
		if (bundleBytes[0] != (byte)'F' || bundleBytes[1] != (byte)'K' ||
			bundleBytes[2] != (byte)'B' || bundleBytes[3] != (byte)'O')
			return (null, null, "Not a FileKeep Lockbox (.kfb) file.");
		int headerLength = BitConverter.ToInt32(bundleBytes, 4);
		if (headerLength <= 0 || 8L + headerLength > bundleBytes.Length)
			return (null, null, "The Lockbox header is damaged.");
		BundleHeader? header;
		try
		{
			header = JsonSerializer.Deserialize(
				bundleBytes.AsSpan(8, headerLength), FileKeepJsonContext.Default.BundleHeader);
		}
		catch (Exception ex)
		{
			return (null, null, $"The Lockbox header could not be read: {ex.Message}");
		}
		if (header is null)
			return (null, null, "The Lockbox header is damaged.");
		if (!string.Equals(header.Format, BundleWriter.ExpectedFormat, StringComparison.Ordinal))
			return (null, null, "Not a FileKeep Lockbox (.kfb) file.");
		if (header.Wraps.Count == 0)
			return (null, null, "The Lockbox has no unlock methods.");

		var (dek, dekError) = UnwrapLockboxDek(header.Wraps, unlock, yubiKeyProvider);
		if (dek is null)
			return (null, null, dekError ?? "Could not unlock the Lockbox.");
		try
		{
			var files = new List<(string Path, byte[] Plaintext)>(header.Files.Count);
			try
			{
				foreach (var entry in header.Files)
				{
					if (entry.Offset < 0 || entry.Length < ChunkCrypto.TagSizeBytes ||
						entry.Offset + entry.Length > bundleBytes.Length)
						throw new InvalidDataException($"File '{entry.Path}' lies outside the Lockbox.");
					byte[] nonce = Convert.FromBase64String(entry.NonceB64);
					byte[] segment = bundleBytes.AsSpan(
						(int)entry.Offset, (int)entry.Length).ToArray();
					byte[] plaintext;
					try
					{
						plaintext = AesGcmDecrypt(dek, nonce, segment);
					}
					catch (CryptographicException)
					{
						throw new InvalidDataException(
							"The Lockbox contents failed verification — the file is damaged.");
					}
					catch (ArgumentException)
					{
						throw new InvalidDataException(
							"The Lockbox contents failed verification — the file is damaged.");
					}
					if (plaintext.Length != entry.Size)
						throw new InvalidDataException($"File '{entry.Path}' has the wrong size.");
					files.Add((entry.Path, plaintext));
				}
			}
			catch
			{
				foreach (var (_, plaintext) in files)
					CryptographicOperations.ZeroMemory(plaintext);
				throw;
			}
			return (header, files, null);
		}
		catch (InvalidDataException ex)
		{
			return (null, null, ex.Message);
		}
		catch (FormatException ex)
		{
			return (null, null, $"The Lockbox header is damaged: {ex.Message}");
		}
		finally
		{
			CryptographicOperations.ZeroMemory(dek);
		}
	}

	/// <summary>
	/// Unwraps a bundle's DEK. AND wraps (mode "and") are handled as
	/// one group: every wrap in the group must unwrap from the
	/// supplied methods, and the shares recombine. Otherwise each
	/// OR wrap is tried in order and the first success wins.
	/// </summary>
	private static (byte[]? Dek, string? Error) UnwrapLockboxDek(
		IReadOnlyList<BundleWrap> wraps,
		LockboxUnlockDto unlock,
		IYubiKeyProvider? yubiKeyProvider)
	{
		var andWraps = wraps.Where(w =>
			string.Equals(w.Mode, "and", StringComparison.Ordinal)).ToList();
		if (andWraps.Count > 0)
		{
			// The writer emits a single AND group ("g1"); take the
			// first group present and require all of its wraps.
			string? group = andWraps[0].Group;
			var groupWraps = andWraps.Where(w =>
				string.Equals(w.Group, group, StringComparison.Ordinal)).ToList();
			var shares = new List<byte[]>();
			try
			{
				foreach (var wrap in groupWraps)
				{
					var (plain, error) = TryUnwrapOne(wrap, unlock, yubiKeyProvider);
					if (plain is null)
						return (null, error ?? "Could not unlock the Lockbox.");
					shares.Add(plain);
				}
				return (DekSharing.Combine(shares.ToArray()), null);
			}
			finally
			{
				foreach (var s in shares)
					CryptographicOperations.ZeroMemory(s);
			}
		}

		string? lastError = null;
		foreach (var wrap in wraps)
		{
			var (plain, error) = TryUnwrapOne(wrap, unlock, yubiKeyProvider);
			if (plain is not null)
				return (plain, null);
			lastError = error;
		}
		// A provider/timeout error is more useful than the generic
		// wrong-key message when it is the only thing that went wrong.
		if (lastError is not null &&
			(lastError.StartsWith("YubiKey", StringComparison.Ordinal) ||
			 lastError.StartsWith("This Lockbox needs", StringComparison.Ordinal)))
			return (null, lastError);
		return (null, "Could not unlock the Lockbox — wrong passphrase or key, or the file is damaged.");
	}

	/// <summary>
	/// Derives one wrap's KEK from the supplied unlock and unwraps
	/// its DEK-or-share. Returns (plaintext, null), or
	/// (null, error) — error is null when the wrap simply does not
	/// match the supplied methods (caller tries the next wrap), and
	/// non-null for hard failures (missing method, timeout, no
	/// provider) that should surface instead of the generic message.
	/// </summary>
	private static (byte[]? Plaintext, string? Error) TryUnwrapOne(
		BundleWrap wrap,
		LockboxUnlockDto unlock,
		IYubiKeyProvider? yubiKeyProvider)
	{
		byte[] kek;
		try
		{
			if (wrap.Type == "passphrase")
			{
				if (string.IsNullOrEmpty(unlock.Passphrase))
					return (null, "This Lockbox needs a passphrase to unlock.");
				if (wrap.SaltB64 is null || wrap.Iterations is null)
					return (null, "The Lockbox wrap is damaged.");
				byte[] salt = Convert.FromBase64String(wrap.SaltB64);
				kek = KeyDerivation.DeriveKey(unlock.Passphrase, salt, wrap.Iterations.Value);
			}
			else if (wrap.Type == "yubikey-hmac-sha1")
			{
				if (!unlock.UseYubiKey)
					return (null, "This Lockbox needs a YubiKey touch to unlock.");
				if (yubiKeyProvider is null)
					return (null, "YubiKey provider not available.");
				if (wrap.ChallengeB64 is null)
					return (null, "The Lockbox wrap is damaged.");
				int slot = unlock.Slot ?? wrap.Slot ?? 2;
				byte[] challenge = Convert.FromBase64String(wrap.ChallengeB64);
				byte[] hmacResponse;
				try
				{
					hmacResponse = yubiKeyProvider.CalculateHmacSha1(
						challenge, slot, LockboxYubiKeyTouchTimeout);
				}
				catch (TimeoutException)
				{
					return (null, "YubiKey touch timed out.");
				}
				catch (Exception ex)
				{
					return (null, $"YubiKey error: {ex.Message}");
				}
				kek = YubiKeyCrypto.DeriveKek(hmacResponse);
				CryptographicOperations.ZeroMemory(hmacResponse);
			}
			else
			{
				return (null, null); // unknown wrap type: not this method
			}
		}
		catch (FormatException)
		{
			return (null, "The Lockbox wrap is damaged.");
		}
		catch (ArgumentException)
		{
			// A damaged wrap: short salt (DeriveKey requires 32 bytes)
			// or a short challenge/key.
			return (null, "The Lockbox wrap is damaged.");
		}
		try
		{
			byte[] nonce = Convert.FromBase64String(wrap.NonceB64);
			byte[] data = Convert.FromBase64String(wrap.DataB64);
			return (AesGcmDecrypt(kek, nonce, data), null);
		}
		catch (CryptographicException)
		{
			return (null, null); // wrong key for this wrap: try the next one
		}
		catch (FormatException)
		{
			return (null, "The Lockbox wrap is damaged.");
		}
		catch (ArgumentException)
		{
			// A damaged wrap: wrong nonce length for AES-GCM.
			return (null, "The Lockbox wrap is damaged.");
		}
		finally
		{
			CryptographicOperations.ZeroMemory(kek);
		}
	}

	/// <summary>
	/// AES-256-GCM decrypt of a BundleWriter segment: data is
	/// ciphertext || 16-byte tag, no AAD — the exact inverse of
	/// BundleWriter's encrypt. Throws CryptographicException on
	/// tampering.
	/// </summary>
	private static byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] data)
	{
		if (data.Length < ChunkCrypto.TagSizeBytes)
			throw new CryptographicException("Ciphertext too short.");
		byte[] plaintext = new byte[data.Length - ChunkCrypto.TagSizeBytes];
		byte[] tag = new byte[ChunkCrypto.TagSizeBytes];
		Array.Copy(data, data.Length - ChunkCrypto.TagSizeBytes, tag, 0, tag.Length);
		using var gcm = new AesGcm(key, ChunkCrypto.TagSizeBytes);
		gcm.Decrypt(nonce, data.AsSpan(0, data.Length - ChunkCrypto.TagSizeBytes), tag, plaintext);
		return plaintext;
	}
}
