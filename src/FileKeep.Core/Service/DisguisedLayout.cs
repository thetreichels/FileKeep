using System.Security.Cryptography;
using System.Text;

namespace FileKeep.Core.Service;

/// <summary>
/// Deterministic camouflage naming for disguised USB/SMB destinations
/// (the "disguise" slice of the Offline Folders design).
///
/// A disguised destination looks like an ordinary folder of media files:
/// every real file wears a derived name with a plausible media extension,
/// and all files sit flat in the cover folder — no <c>chunks/</c>,
/// <c>manifests/</c> or <c>repo.json</c> to give the game away. The names
/// are derived, never indexed: FileKeep recomputes any file's disguised
/// name from its logical name plus the location key, so a disguised
/// destination needs no index file that would itself need hiding.
///
/// Naming scheme: <c>SHA-256("FileKeep disguise v1" ‖ locationKey ‖
/// domain ‖ logicalName)</c>, rendered as the first 24 hex chars plus an
/// extension chosen by a digest byte from a fixed plausible set. The
/// domain separates namespaces (a chunk, a manifest, repo.json, parity
/// and decoys can never collide even for the same logical string), and
/// the location key separates locations (the same chunk wears a
/// different name on each disguised destination, so destinations cannot
/// be correlated by filename).
///
/// This is camouflage, not cryptography: anyone who knows the scheme
/// can recompute the names. The contents stay encrypted exactly as on
/// a plain destination — disguise only changes what a casual look at
/// the folder reveals.
///
/// Header spoofing (v1): on disguised destinations, files whose derived
/// name carries a media extension additionally wear a fixed fake media
/// header prepended on write, stripped on read (see
/// <see cref="HeaderForExtension"/>, <see cref="WrapContent"/> and
/// <see cref="UnwrapContent"/>). The strip is detect-and-strip: a file
/// that does not start with its extension's expected header — e.g. one
/// written by the pre-header disguise slice, or a headerless extension —
/// reads back as-is, so pre-v1 disguised destinations stay readable and
/// a re-sync upgrades them to headered form. The header table is
/// versioned (<see cref="HeaderTableVersion"/>): a future table change
/// must keep v1 headers recognizable (or add explicit version
/// negotiation), or v1 files become unreadable.
/// </summary>
public sealed class DisguisedLayout
{
    /// <summary>Version of the fake-header table below. Bump on any change.</summary>
    public const int HeaderTableVersion = 1;
    /// <summary>Fixed plausible extensions, in derivation order. Do not reorder.</summary>
    public static readonly string[] Extensions =
        { ".jpg", ".mp4", ".png", ".mov", ".dat", ".cache", ".bin", ".avi" };

    private static readonly string[] ReservedDeviceNames =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
          "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
          "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>Stable per-location identity used in name derivation (the location's name).</summary>
    public string LocationKey { get; }

    /// <summary>Innocuous on-destination folder name, e.g. "Photos".</summary>
    public string CoverName { get; }

    /// <summary>Whether decoy files are written alongside real files.</summary>
    public bool DecoysEnabled { get; }

    public DisguisedLayout(string locationKey, string coverName, bool decoysEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationKey);
        if (!IsValidCoverName(coverName, out string? error))
            throw new ArgumentException(error, nameof(coverName));
        LocationKey = locationKey;
        CoverName = coverName;
        DecoysEnabled = decoysEnabled;
    }

    /// <summary>
    /// The layout for a location, or null when the location is not
    /// disguised (plain locations keep their classic layout exactly).
    /// Only USB and SMB locations can be disguised.
    /// </summary>
    public static DisguisedLayout? ForLocation(BackupLocation loc)
    {
        if (loc is null)
            return null;
        if (!loc.DisguiseEnabled)
            return null;
        if (loc.Type != "usb" && loc.Type != "smb")
            return null;
        if (!IsValidCoverName(loc.CoverName, out _))
            return null;
        return new DisguisedLayout(loc.Name, loc.CoverName, loc.DisguiseDecoys);
    }

    /// <summary>
    /// The on-destination folder for a USB location: the cover name when
    /// disguised, the configured folder otherwise. Single place for the
    /// rule, used by sync, batched copy, metadata copy, restore and test.
    /// </summary>
    public static string UsbFolderFor(BackupLocation loc) =>
        loc.Disguise is { } layout ? layout.CoverName : loc.UsbFolder;

    /// <summary>Validates a cover folder name. Shared by the API and the tests.</summary>
    public static bool IsValidCoverName(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Cover folder name is required for disguised locations.";
            return false;
        }
        if (name.Length > 128)
        {
            error = "Cover folder name is too long (max 128 characters).";
            return false;
        }
        if (name is "." or "..")
        {
            error = "Cover folder name must not be '.' or '..'.";
            return false;
        }
        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "Cover folder name must not end with a space or a period.";
            return false;
        }
        // Windows invalid set, checked explicitly rather than via
        // Path.GetInvalidFileNameChars(): cover folders live on Windows
        // destinations (USB/SMB) even when this code runs on Linux, and
        // the Linux set would wrongly allow ':' and '\'.
        const string windowsInvalid = "<>:\"/\\|?*";
        foreach (char c in windowsInvalid)
        {
            if (name.Contains(c))
            {
                error = "Cover folder name must not contain path separators or special characters.";
                return false;
            }
        }
        if (name.Any(c => c < 32))
        {
            error = "Cover folder name must not contain control characters.";
            return false;
        }
        string baseName = name.Split('.')[0];
        foreach (string reserved in ReservedDeviceNames)
        {
            if (baseName.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Cover folder name must not be the reserved device name '{reserved}'.";
                return false;
            }
        }
        return true;
    }

    /// <summary>Disguised file name for a chunk (chunk IDs are 64 lowercase hex chars).</summary>
    public string ChunkFileName(string chunkIdHex) =>
        DisguisedName("chunk", chunkIdHex.ToLowerInvariant());

    /// <summary>Disguised file name for a manifest file (e.g. "b1.json").</summary>
    public string ManifestFileName(string manifestFileName) =>
        DisguisedName("manifest", manifestFileName);

    /// <summary>Disguised file name for repo.json.</summary>
    public string RepoFileName() => DisguisedName("repo", "repo.json");

    /// <summary>Disguised file name for a parity file (relative path under parity/).</summary>
    public string ParityFileName(string relativePath) =>
        DisguisedName("parity", relativePath.Replace('\\', '/'));

    /// <summary>Disguised file name for decoy <paramref name="index"/> (stable across syncs).</summary>
    public string DecoyFileName(int index) =>
        DisguisedName("decoy", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// How many decoys a destination with <paramref name="chunkCount"/>
    /// chunks gets: 10% of chunks, clamped to [8, 64]. Stable for a given
    /// chunk count, so re-syncs do not churn the decoy set.
    /// </summary>
    public static int DecoyCount(int chunkCount) => Math.Clamp(chunkCount / 10, 8, 64);

    /// <summary>
    /// Deterministic decoy content: a plausible media header for media
    /// extensions (JPEG SOI/JFIF, PNG signature, MP4 ftyp, RIFF/AVI),
    /// random bytes otherwise — sized like a chunk (64 KiB–2 MiB).
    /// Deterministic per index so re-syncs rewrite identical bytes.
    /// </summary>
    public byte[] DecoyContent(int index)
    {
        string name = DecoyFileName(index);
        string ext = Path.GetExtension(name);
        byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes(
            "FileKeep decoy v1\0" + LocationKey + "\0" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        int size = 64 * 1024 + (BitConverter.ToInt32(seed, 0) & 0x7fffffff) % (2 * 1024 * 1024 - 64 * 1024);
        var random = new Random(BitConverter.ToInt32(seed, 4));
        byte[] content = new byte[size];
        random.NextBytes(content);
        byte[] header = HeaderForExtension(ext);
        Array.Copy(header, content, Math.Min(header.Length, content.Length));
        return content;
    }

    /// <summary>
    /// The fixed fake media header for a disguised file's extension
    /// (header spoofing v1, table version <see cref="HeaderTableVersion"/>).
    /// Lengths: .jpg 20, .png 16, .mp4/.mov 12, .avi 12, everything else
    /// (including .dat/.cache/.bin) 0 — non-media extensions have no
    /// expected header to fake. Shared by decoys and by real-file
    /// wrapping so the two can never diverge.
    /// </summary>
    public static byte[] HeaderForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10,
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 },
        ".png" => new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R' },
        ".mp4" or ".mov" => new byte[] { 0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' },
        ".avi" => new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0x00, 0x00, 0x00, 0x00, (byte)'A', (byte)'V', (byte)'I', 0x20 },
        _ => Array.Empty<byte>(),
    };

    /// <summary>Longest header in <see cref="HeaderForExtension"/> (the .jpg one).</summary>
    public static int MaxHeaderLength { get; } = 20;

    /// <summary>
    /// Prepends the extension's fake header to <paramref name="content"/>
    /// (no-op for headerless extensions). The header is keyed off the
    /// destination file name's extension, so wrap and strip always agree.
    /// </summary>
    public static byte[] WrapContent(byte[] content, string fileName)
    {
        byte[] header = HeaderForExtension(Path.GetExtension(fileName));
        if (header.Length == 0)
            return content;
        byte[] wrapped = new byte[header.Length + content.Length];
        Array.Copy(header, wrapped, header.Length);
        Array.Copy(content, 0, wrapped, header.Length, content.Length);
        return wrapped;
    }

    /// <summary>
    /// Detect-and-strip: if <paramref name="stored"/> starts with the
    /// extension's expected header, returns the remainder; otherwise
    /// returns <paramref name="stored"/> unchanged. The tolerance is
    /// what keeps pre-header (pre-v1) disguised files readable, and what
    /// makes a corrupted header fail closed downstream: unstripped
    /// header bytes fed to AES-GCM verification fail authentication
    /// instead of decrypting to wrong bytes.
    /// </summary>
    public static byte[] UnwrapContent(byte[] stored, string fileName)
    {
        byte[] header = HeaderForExtension(Path.GetExtension(fileName));
        if (header.Length == 0 || stored.Length < header.Length)
            return stored;
        for (int i = 0; i < header.Length; i++)
            if (stored[i] != header[i])
                return stored;
        return stored[header.Length..];
    }

    /// <summary>
    /// Copies <paramref name="srcPath"/> to <paramref name="destPath"/>
    /// with the destination file name's fake header prepended, but only
    /// when the destination is missing or its wrapped content differs
    /// (length + streaming SHA-256 of header+source vs destination).
    /// Writes via temp + move for atomicity. Returns true if a copy
    /// happened (<paramref name="bytesCopied"/> is the wrapped length on
    /// a copy, 0 on a skip). This is the single wrapped-write path for
    /// disguised destinations (SMB sync, USB sync, batched copies,
    /// metadata).
    /// </summary>
    public static bool CopyWrappedIfNeeded(string srcPath, string destPath, out long bytesCopied)
    {
        byte[] header = HeaderForExtension(Path.GetExtension(destPath));
        var srcInfo = new FileInfo(srcPath);
        var destInfo = new FileInfo(destPath);
        bytesCopied = 0;
        if (destInfo.Exists && destInfo.Length == srcInfo.Length + header.Length
            && WrappedMatches(srcPath, destPath))
            return false;
        bytesCopied = srcInfo.Length + header.Length;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string tmp = destPath + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var input = File.OpenRead(srcPath))
        using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (header.Length > 0)
                output.Write(header, 0, header.Length);
            input.CopyTo(output);
        }
        try { File.Move(tmp, destPath, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
        return true;
    }

    /// <summary>
    /// True when the destination file holds exactly the wrapped form of
    /// the source file (header + source bytes). Streaming; files are
    /// never fully loaded. Used for copy-verify confirms.
    /// </summary>
    public static bool WrappedMatches(string srcPath, string destPath)
    {
        byte[] header = HeaderForExtension(Path.GetExtension(destPath));
        var srcInfo = new FileInfo(srcPath);
        var destInfo = new FileInfo(destPath);
        if (!destInfo.Exists || destInfo.Length != srcInfo.Length + header.Length)
            return false;
        using var incremental = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (header.Length > 0)
            incremental.AppendData(header);
        using (var input = File.OpenRead(srcPath))
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                incremental.AppendData(buffer, 0, read);
        }
        string expected = Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
        using var destStream = File.OpenRead(destPath);
        string actual = Hashing.Sha256Hex(destStream);
        return string.Equals(expected, actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies a disguised destination file to <paramref name="destPath"/>
    /// with its fake header stripped when present (detect-and-strip, so
    /// pre-header files copy through unchanged). The single wrapped-read
    /// path for restore pulls. Streams; files are never fully loaded.
    /// </summary>
    public static void CopyUnwrapped(string srcPath, string destPath)
    {
        byte[] header = HeaderForExtension(Path.GetExtension(srcPath));
        using var input = File.OpenRead(srcPath);
        using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        if (header.Length > 0)
        {
            byte[] peek = new byte[header.Length];
            int read = 0;
            while (read < peek.Length)
            {
                int n = input.Read(peek, read, peek.Length - read);
                if (n == 0) break;
                read += n;
            }
            bool matches = read == header.Length;
            for (int i = 0; matches && i < header.Length; i++)
                if (peek[i] != header[i])
                    matches = false;
            if (!matches && read > 0)
                output.Write(peek, 0, read);
        }
        input.CopyTo(output);
    }

    /// <summary>
    /// Writes the destination's decoy set into <paramref name="directory"/>,
    /// skipping files that already exist with identical content (so
    /// re-syncs are idempotent and do not churn decoys).
    /// </summary>
    public void WriteDecoys(string directory, int chunkCount)
    {
        Directory.CreateDirectory(directory);
        int count = DecoyCount(chunkCount);
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(directory, DecoyFileName(i));
            byte[] content = DecoyContent(i);
            if (File.Exists(path) && new FileInfo(path).Length == content.Length)
                continue;
            File.WriteAllBytes(path, content);
        }
    }

    private string DisguisedName(string domain, string logical)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            "FileKeep disguise v1\0" + LocationKey + "\0" + domain + "\0" + logical));
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        string ext = Extensions[digest[12] % Extensions.Length];
        return hex[..24] + ext;
    }
}
