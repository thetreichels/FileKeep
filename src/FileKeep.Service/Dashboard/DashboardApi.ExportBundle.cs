using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FileKeep.Core;
using FileKeep.Core.Bundle;
using FileKeep.Core.Security;
using FileKeep.Core.Service;

namespace FileKeep.Service;

/// <summary>
/// DashboardApi — Export backup as encrypted bundle (.kfb) for the iOS
/// secure workspace. Restores a backup to temp, packages the files into
/// a "FileKeep offline bundle v1", and returns the bundle bytes.
/// </summary>
public static partial class DashboardApi
{
	/// <summary>
	/// Unlock method for bundle export.
	/// </summary>
	public sealed class ExportUnlockMethodDto
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

	public sealed class ExportBundleRequestDto
	{
		[JsonPropertyName("backupId")]
		public string BackupId { get; set; } = "";

		[JsonPropertyName("methods")]
		public List<ExportUnlockMethodDto> Methods { get; set; } = new();

		[JsonPropertyName("requireAll")]
		public bool RequireAll { get; set; } // true = AND, false = OR
	}

	/// <summary>
	/// Exports a backup as an encrypted .kfb bundle.
	/// Returns (200, bundleBytes) on success, or (status, ErrorDto).
	/// </summary>
	public static (int Status, object Payload) ExportBundle(
		ServiceConfig config,
		string repo,
		ExportBundleRequestDto request,
		RestoreSession? session = null,
		IYubiKeyProvider? yubiKeyProvider = null,
		CancellationToken cancellationToken = default,
		Action<string>? log = null)
	{
		if (!IsKnownRepo(config, repo))
			return (400, new ErrorDto("Unknown repo (not a configured job)."));
		if (string.IsNullOrWhiteSpace(request.BackupId))
			return (400, new ErrorDto("backupId is required."));
		if (request.Methods is null || request.Methods.Count == 0)
			return (400, new ErrorDto("At least one unlock method is required."));

		// Validate methods
		foreach (var m in request.Methods)
		{
			if (m.Type != "passphrase" && m.Type != "yubikey-hmac-sha1")
				return (400, new ErrorDto($"Unsupported unlock method: {m.Type}"));
			if (m.Type == "passphrase" && string.IsNullOrEmpty(m.Passphrase))
				return (400, new ErrorDto("Passphrase is required for passphrase method."));
		}

		if (request.RequireAll && request.Methods.Count < 2)
			return (400, new ErrorDto("AND requires at least 2 methods."));

		string? repoPassphrase = GetRestorePassphrase(config, session);
		if (string.IsNullOrEmpty(repoPassphrase))
			return HasEnabledUnlockMethod(config)
				? (401, new ErrorDto(RestoreSessionLockedMessage(config)))
				: (500, PassphraseUnavailableError(config, "Service passphrase is not configured."));

		string tempDir = Path.Combine(Path.GetTempPath(), $"fk-export-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(tempDir);

			// Restore backup to temp
			using (var r = BackupRepository.Open(repo, repoPassphrase))
			{
				var result = r.Restore(request.BackupId, tempDir, new RestoreOptions(), cancellationToken);
				if (result.Warnings.Count > 0)
				{
					log?.Invoke($"Export restore had {result.Warnings.Count} warnings");
				}
				log?.Invoke($"Export restore: {result.RestoredFiles} files restored");
			}

			// Collect files from temp
			var files = new List<(string Path, byte[] Plaintext)>();
			foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
			{
				string relativePath = Path.GetRelativePath(tempDir, file);
				// Normalize to forward slashes for the bundle
				relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
				byte[] plaintext = File.ReadAllBytes(file);
				files.Add((relativePath, plaintext));
			}

			if (files.Count == 0)
				return (400, new ErrorDto("Backup contains no files to export."));

			// Generate DEK
			byte[] dek = RandomNumberGenerator.GetBytes(KeyDerivation.KeySizeBytes);
			try
			{
				// Create wraps
				var wraps = new List<BundleWrap>();
				var wrapInfos = new List<(string Type, byte[] Kek, byte[]? Salt, int? Iterations, int? Slot, byte[]? Challenge)>();

				try
				{
					foreach (var m in request.Methods)
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
								return (500, new ErrorDto("YubiKey provider not available."));
							int slot = m.Slot ?? 2;
							byte[] challenge = RandomNumberGenerator.GetBytes(32);
							byte[] hmacResponse;
							try
							{
								hmacResponse = yubiKeyProvider.CalculateHmacSha1(
									challenge, slot, TimeSpan.FromSeconds(30));
							}
							catch (TimeoutException)
							{
								return (400, new ErrorDto("YubiKey touch timed out."));
							}
							catch (Exception ex)
							{
								return (500, new ErrorDto($"YubiKey error: {ex.Message}"));
							}
							byte[] kek = YubiKeyCrypto.DeriveKek(hmacResponse);
							CryptographicOperations.ZeroMemory(hmacResponse);
							wrapInfos.Add(("yubikey-hmac-sha1", kek, null, null, slot, challenge));
						}
					}

					if (request.RequireAll && wrapInfos.Count > 1)
					{
						// AND: split DEK into shares
						byte[][] shares = DekSharing.Split(dek, wrapInfos.Count);
						try
						{
							for (int i = 0; i < wrapInfos.Count; i++)
							{
								var (type, kek, salt, iterations, slot, challenge) = wrapInfos[i];
								var wrap = BundleWriter.CreateWrap(
									type, kek, shares[i],
									mode: "and", group: "g1",
									salt: salt, iterations: iterations,
									slot: slot, challenge: challenge);
								wraps.Add(wrap);
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
						// OR: each wrap holds the full DEK
						foreach (var (type, kek, salt, iterations, slot, challenge) in wrapInfos)
						{
							var wrap = BundleWriter.CreateWrap(
								type, kek, dek,
								salt: salt, iterations: iterations,
								slot: slot, challenge: challenge);
							wraps.Add(wrap);
						}
					}
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

				// Create bundle
				byte[] bundleBytes = BundleWriter.Create(wraps, dek, files);

				// Zero plaintext buffers
				foreach (var (_, plaintext) in files)
					CryptographicOperations.ZeroMemory(plaintext);

				return (200, bundleBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(dek);
			}
		}
		catch (Exception ex)
		{
			log?.Invoke($"Export bundle failed: {ex.Message}");
			return (500, new ErrorDto($"Export failed: {ex.Message}"));
		}
		finally
		{
			// Clean up temp dir
			try
			{
				if (Directory.Exists(tempDir))
					Directory.Delete(tempDir, recursive: true);
			}
			catch { }
		}
	}
}
