using System.Text;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "init" => Init(args[1..]),
        "backup" => Backup(args[1..]),
        "backup-disk" => BackupDisk(args[1..]),
        "restore" => Restore(args[1..]),
        "restore-disk" => RestoreDisk(args[1..]),
        "verify" => Verify(args[1..]),
        "list" => List(args[1..]),
        "nntp-check" => NntpCheck(args[1..]),
        "nntp-upload" => NntpUpload(args[1..]),
        "manifest-discover" => ManifestDiscover(args[1..]),
        "nzb-generate" => NzbGenerate(args[1..]),
        "download" => Download(args[1..]),
        "expiration-check" => ExpirationCheck(args[1..]),
        "serve" => Serve(args[1..]),
        _ => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        usenet-backup — local repository engine (milestones 1-2) + NNTP backend (milestone 3)

        Usage:
          usenet-backup init <repo> [--chunk-size BYTES]
          usenet-backup backup <repo> <source-dir> [--parent <backup-id>] [--backup-privilege]
          usenet-backup backup-disk <repo> <device> [--image-name NAME]
          usenet-backup restore <repo> <backup-id> <dest-dir>
          usenet-backup restore-disk <repo> <backup-id> <device> [--yes]
          usenet-backup verify <repo> <backup-id>
          usenet-backup list <repo>
          usenet-backup nntp-check --host HOST [--port PORT] [--ssl] [--user USER]
          usenet-backup nntp-upload <repo> <backup-id> --host HOST [--port PORT]
              [--ssl] [--user USER] [--newsgroup GROUP]
          usenet-backup nzb-generate <repo> <backup-id> <output.nzb>
              [--newsgroup GROUP] [--poster POSTER]
          usenet-backup download <repo> <nzb-file> --host HOST [--port PORT]
              [--ssl] [--user USER] [--newsgroup GROUP]
          usenet-backup serve <repo> [--port PORT] [--bind ADDR]
          usenet-backup expiration-check <repo> [--warn-days DAYS] [--retention-days DAYS] [--config PATH]
          (--user also accepts --username as an alias)

        --parent turns the backup into an incremental against that parent
        manifest. Unchanged files (same size + mtime) reuse the parent's
        chunks without re-reading.

        nntp-upload posts every unique chunk of a backup as one yEnc article
        each. It is resumable: already-posted articles are skipped via the
        local upload journal plus a server STAT check.

        nzb-generate writes an NZB 1.1 index of the backup's chunk articles
        (one file per chunk, deterministic message-IDs). Chunks with no
        upload journal record are flagged — run nntp-upload first.

        download fetches every chunk referenced by an NZB index into the
        repo's local chunk store. It is resumable: chunks already present
        are skipped. Each fetched chunk is authenticated and hash-verified
        before being stored. Run verify/restore afterwards as usual.

        --backup-privilege enables the Windows SeBackupPrivilege and reads
        files with backup semantics (Windows only, requires administrator
        rights), so files locked by other processes can be read. This reads
        the live files, not a point-in-time copy.

        backup-disk images a raw block device (e.g. \\.\C: on Windows,
        /dev/sda on Linux) through the normal chunk/encrypt pipeline as a
        single image entry; restore-disk writes it back. The image is of the
        live volume, not a point-in-time copy.

        The passphrase is read from --passphrase, the USENETBACKUP_PASSPHRASE
        environment variable, or an interactive prompt (in that order).
        The NNTP password comes from --password, USENETBACKUP_NNTP_PASSWORD,
        or an interactive prompt (only when --user is given).
        """);
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"error: unknown command '{cmd}'");
    PrintUsage();
    return 2;
}

static string GetPassphrase(string[] args)
{
    string? fromArg = GetOption(args, "--passphrase");
    if (!string.IsNullOrEmpty(fromArg))
    {
        WarnSecretOnCommandLine("--passphrase", "USENETBACKUP_PASSPHRASE");
        return fromArg;
    }
    string? fromEnv = Environment.GetEnvironmentVariable("USENETBACKUP_PASSPHRASE");
    if (!string.IsNullOrEmpty(fromEnv))
        return fromEnv;
    string typed = PromptInteractive("Passphrase: ", "--passphrase", "USENETBACKUP_PASSPHRASE");
    if (string.IsNullOrEmpty(typed))
        throw new InvalidOperationException("A passphrase is required.");
    return typed;
}

/// <summary>
/// A secret passed as a command-line flag is visible to other users on the
/// machine via the process list. Warn once per invocation.
/// </summary>
static void WarnSecretOnCommandLine(string flag, string envVar)
{
    Console.Error.WriteLine(
        $"warning: {flag} exposes the secret in the process list; " +
        $"prefer the {envVar} environment variable or the interactive prompt.");
}

/// <summary>
/// Prompts on stdin for a secret, but fails fast instead of hanging when stdin
/// is not interactive (piped / redirected), e.g. in scripts.
/// </summary>
static string PromptInteractive(string prompt, string flagName, string envName)
{
    if (Console.IsInputRedirected)
        throw new InvalidOperationException(
            $"A secret is required but stdin is not interactive. Pass {flagName} or set {envName}.");
    Console.Write(prompt);
    return Console.ReadLine() ?? "";
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

static string[] Positionals(string[] args) =>
    args.Where((a, i) => !(a.StartsWith("--") || (i > 0 && args[i - 1].StartsWith("--")))).ToArray();

static bool HasFlag(string[] args, string name) =>
    args.Any(a => a == name);

static int Init(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 1) { Console.Error.WriteLine("error: init <repo>"); return 2; }
    int chunkSize = int.TryParse(GetOption(args, "--chunk-size"), out int cs) ? cs : BackupRepository.DefaultChunkSize;
    using var repo = BackupRepository.Init(pos[0], GetPassphrase(args), chunkSize);
    Console.WriteLine($"Initialized repository at {Path.GetFullPath(pos[0])} (chunk size {chunkSize}).");
    return 0;
}

static int Backup(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 2) { Console.Error.WriteLine("error: backup <repo> <source-dir> [--parent <backup-id>] [--backup-privilege]"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    string? parent = GetOption(args, "--parent");
    using ISnapshotProvider? snap = HasFlag(args, "--backup-privilege") ? new BackupPrivilegeSnapshotProvider(pos[1]) : null;
    var manifest = parent is null
        ? repo.BackupDirectory(pos[1], snap)
        : repo.BackupIncremental(pos[1], parent, snap);
    Console.WriteLine($"{manifest.Type} backup {manifest.BackupId}" +
        (manifest.ParentId is null ? "" : $" (parent {manifest.ParentId})") +
        (manifest.Snapshot is null ? "" : $" [snapshot: {manifest.Snapshot}]") +
        $": {manifest.Files.Count} files, {manifest.Files.Sum(f => f.Chunks.Count)} chunk refs, {repo.StoredChunkCount()} unique chunks stored.");
    return 0;
}

static int BackupDisk(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 2) { Console.Error.WriteLine("error: backup-disk <repo> <device> [--image-name NAME]"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    string imageName = GetOption(args, "--image-name") ?? "disk.img";
    var manifest = repo.BackupDiskImage(pos[1], imageName);
    Console.WriteLine($"disk-image backup {manifest.BackupId}: {manifest.Files[0].Size} bytes as '{imageName}', " +
        $"{manifest.Files[0].Chunks.Count} chunks, {repo.StoredChunkCount()} unique chunks stored.");
    return 0;
}

static int RestoreDisk(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 3) { Console.Error.WriteLine("error: restore-disk <repo> <backup-id> <device> [--yes]"); return 2; }
    string device = pos[2];
    if (!HasFlag(args, "--yes"))
    {
        // Destructive by design: make the operator prove they mean THIS device.
        if (Console.IsInputRedirected)
            throw new InvalidOperationException(
                "Refusing to overwrite a block device without confirmation: stdin is not " +
                "interactive. Re-run with --yes to confirm non-interactively.");
        Console.Error.WriteLine($"WARNING: this will OVERWRITE {device} with the contents of backup {pos[1]}.");
        Console.Error.WriteLine("All existing data on that device will be destroyed.");
        Console.Write($"Type the device path exactly to confirm: ");
        string? typed = Console.ReadLine();
        if (!string.Equals(typed?.Trim(), device, StringComparison.Ordinal))
            throw new InvalidOperationException("Confirmation did not match; restore aborted.");
    }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    repo.RestoreDiskImage(pos[1], device);
    Console.WriteLine($"Restored disk image {pos[1]} to {device}.");
    return 0;
}

static int Restore(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 3) { Console.Error.WriteLine("error: restore <repo> <backup-id> <dest-dir> [--store URL]"); return 2; }
    string? storeUrl = GetOption(args, "--store");
    using var repo = storeUrl is not null
        ? BackupRepository.OpenWithStore(pos[0], GetPassphrase(args), new UsenetBackup.Core.Lan.HttpBlobStore(storeUrl))
        : BackupRepository.Open(pos[0], GetPassphrase(args));
    repo.Restore(pos[1], pos[2]);
    Console.WriteLine($"Restored backup {pos[1]} to {Path.GetFullPath(pos[2])}.");
    return 0;
}

static int Verify(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 2) { Console.Error.WriteLine("error: verify <repo> <backup-id>"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    var errors = repo.Verify(pos[1]);
    if (errors.Count == 0)
    {
        Console.WriteLine($"Backup {pos[1]} verified OK.");
        return 0;
    }
    foreach (var e in errors)
        Console.Error.WriteLine($"verify failed: {e}");
    return 1;
}

static int List(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 1) { Console.Error.WriteLine("error: list <repo>"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    foreach (var b in repo.ListBackups())
        Console.WriteLine($"{b.BackupId}  {b.Type,-4}  {b.CreatedUtc:O}  {b.Source}");
    return 0;
}

static string GetNntpPassword(string[] args)
{
    string? fromArg = GetOption(args, "--password");
    if (!string.IsNullOrEmpty(fromArg))
    {
        WarnSecretOnCommandLine("--password", "USENETBACKUP_NNTP_PASSWORD");
        return fromArg;
    }
    string? fromEnv = Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD");
    if (!string.IsNullOrEmpty(fromEnv))
        return fromEnv;
    return PromptInteractive("NNTP password: ", "--password", "USENETBACKUP_NNTP_PASSWORD");
}

static NntpClient ConnectNntp(string[] args)
{
    string? host = GetOption(args, "--host");
    if (string.IsNullOrEmpty(host))
        throw new InvalidOperationException("--host is required.");
    bool ssl = args.Contains("--ssl");
    int port = int.TryParse(GetOption(args, "--port"), out int p) ? p : (ssl ? 563 : 119);

    var client = new NntpClient(host, port, ssl);
    try
    {
        client.Connect();
        string? user = GetOption(args, "--user") ?? GetOption(args, "--username");
        if (!string.IsNullOrEmpty(user))
            client.Authenticate(user, GetNntpPassword(args));
        return client;
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

static int NntpCheck(string[] args)
{
    using var client = ConnectNntp(args);
    Console.WriteLine($"Connected: {client.Greeting}");
    client.Quit();
    Console.WriteLine("NNTP check OK.");
    return 0;
}

static int NntpUpload(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 2) { Console.Error.WriteLine("error: nntp-upload <repo> <backup-id> --host HOST [...]"); return 2; }
    string newsgroup = GetOption(args, "--newsgroup") ?? "alt.binaries.test";

    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    var manifest = repo.LoadManifest(pos[1]);
    // Chunk order MUST match NzbGenerator.Generate (sorted by ID) so that
    // parity groups align between upload and download reconstruction.
    string[] chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();

    using var client = ConnectNntp(args);
    try
    {
        using var store = new NntpBlobStore(client, newsgroup, repo.RepoId, repo.CatalogPath);
        int uploaded = 0, skipped = 0;
        for (int i = 0; i < chunkIds.Length; i++)
        {
            if (store.Exists(chunkIds[i]))
            {
                skipped++;
            }
            else
            {
                store.Put(chunkIds[i], repo.GetChunkBlob(chunkIds[i]));
                uploaded++;
            }
            if ((i + 1) % 25 == 0 || i + 1 == chunkIds.Length)
                Console.WriteLine($"  {i + 1}/{chunkIds.Length} chunks processed ({uploaded} uploaded, {skipped} already present)");
        }
        OperationLog.Append(Path.GetFullPath(pos[0]), "nntp-upload",
            $"id={manifest.BackupId} chunks={chunkIds.Length} uploaded={uploaded} skipped={skipped} host={GetOption(args, "--host")} newsgroup={newsgroup}");
        Console.WriteLine($"Upload complete: {uploaded} posted, {skipped} already present.");
        // Upload the encrypted manifest so the USB recovery wizard can
        // discover backups newer than the stick.
        repo.UploadManifest(manifest.BackupId, store);
        Console.WriteLine("Manifest uploaded (encrypted).");
        return 0;
    }
    finally
    {
        client.Quit();
    }
}

/// <summary>
/// Lists backups whose encrypted manifests exist on Usenet but not locally.
/// Used by the USB recovery wizard to find backups newer than the stick.
/// </summary>
static int ManifestDiscover(string[] args)
{
    var pos = args.Where(a => !a.StartsWith('-')).ToArray();
    if (pos.Length < 1) { Console.Error.WriteLine("error: manifest-discover <repo> --host HOST [...]"); return 2; }
    string newsgroup = GetOption(args, "--newsgroup") ?? "alt.binaries.test";

    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    using var client = ConnectNntp(args);
    try
    {
        using var store = new NntpBlobStore(client, newsgroup, repo.RepoId, repo.CatalogPath);
        // Use the monthly index (STAT probes) instead of LISTGROUP, which is
        // infeasible on large groups (e.g., alt.binaries.test has billions).
        var found = repo.DiscoverRemoteManifestsViaIndex(store);
        if (found.Count == 0)
        {
            Console.WriteLine("No remote manifests newer than local.");
        }
        else
        {
            foreach (var m in found)
                Console.WriteLine($"{m.BackupId}  type={m.Manifest.Type}  created={m.Manifest.CreatedUtc:u}  files={m.Manifest.Files.Count}");
        }
        return 0;
    }
    finally
    {
        client.Quit();
    }
}

static int NzbGenerate(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 3)
    {
        Console.Error.WriteLine("error: nzb-generate <repo> <backup-id> <output.nzb> [--newsgroup GROUP] [--poster POSTER]");
        return 2;
    }
    string newsgroup = GetOption(args, "--newsgroup") ?? "alt.binaries.test";
    string poster = GetOption(args, "--poster") ?? "usenet-backup";

    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    var manifest = repo.LoadManifest(pos[1]);
    using var catalog = new Catalog(repo.CatalogPath);

    int notUploaded = 0;
    string xml = NzbGenerator.Generate(
        manifest,
        chunkId => repo.GetChunkBlob(chunkId),
        chunkId =>
        {
            var t = catalog.GetUploadTimeUtc(chunkId);
            if (t is null)
                notUploaded++;
            return t;
        },
        new NzbGenerator.Options(newsgroup, poster, repo.RepoId));

    File.WriteAllText(pos[2], xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    int chunkCount = manifest.Files.SelectMany(f => f.Chunks).Distinct().Count();
    OperationLog.Append(Path.GetFullPath(pos[0]), "nzb-generate",
        $"id={manifest.BackupId} chunks={chunkCount} output={pos[2]} newsgroup={newsgroup}");
    if (notUploaded > 0)
        Console.WriteLine($"warning: {notUploaded}/{chunkCount} chunks have no upload journal record — run nntp-upload first.");
    Console.WriteLine($"NZB written to {pos[2]} ({chunkCount} chunks).");
    return 0;
}

static int Download(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 2) { Console.Error.WriteLine("error: download <repo> <nzb-file> --host HOST [...]"); return 2; }
    string newsgroup = GetOption(args, "--newsgroup") ?? "alt.binaries.test";

    NzbDocument nzb;
    try
    {
        nzb = NzbParser.ParseFile(pos[1]);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: cannot parse NZB '{pos[1]}': {ex.Message}");
        return 2;
    }
    if (nzb.Files.Count == 0)
    {
        Console.Error.WriteLine($"error: NZB '{pos[1]}' contains no files.");
        return 2;
    }
    Console.WriteLine($"NZB references {nzb.Files.Count} chunk(s)" +
        (nzb.BackupId is null ? "" : $" (backup {nzb.BackupId})") + ".");

    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    using var client = ConnectNntp(args);
    try
    {
        using var remote = new NntpBlobStore(client, newsgroup, repo.RepoId, repo.CatalogPath);
        DownloadResult result = repo.DownloadChunks(nzb, remote, (done, total) =>
        {
            if (done % 25 == 0 || done == total)
                Console.WriteLine($"  {done}/{total} chunks processed");
        });
        OperationLog.Append(Path.GetFullPath(pos[0]), "download",
            $"nzb={pos[1]} chunks={result.Total} downloaded={result.Downloaded} already_present={result.AlreadyPresent} host={GetOption(args, "--host")} newsgroup={newsgroup}");
        Console.WriteLine($"Download complete: {result.Downloaded} fetched, {result.AlreadyPresent} already present.");
        return 0;
    }
    finally
    {
        client.Quit();
    }
}

/// <summary>
/// Serves a repo's chunks over HTTP for LAN restores.
/// Usage: usenet-backup serve &lt;repo&gt; [--port PORT] [--bind ADDR]
/// Endpoints: GET/HEAD /chunks/{id}, POST /chunks/{id}, GET /count
/// </summary>
static int Serve(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 1) { Console.Error.WriteLine("error: serve <repo> [--port PORT] [--bind ADDR]"); return 2; }
    string repoPath = Path.GetFullPath(pos[0]);
    int port = int.TryParse(GetOption(args, "--port"), out int p) ? p : 8477;
    string bind = GetOption(args, "--bind") ?? "127.0.0.1";
    if (bind == "0.0.0.0")
    {
        Console.WriteLine("WARNING: Binding to all interfaces with no authentication. " +
            "Anyone on your network can read your (encrypted) chunks.");
    }

    using var repo = BackupRepository.Open(repoPath, GetPassphrase(args));

    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://{bind}:{port}/");
    try
    {
        listener.Start();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: cannot listen on {bind}:{port}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"Serving repo {repoPath} on http://{bind}:{port}/ (Ctrl+C to stop)");
    Console.WriteLine($"LAN clients: usenet-backup restore --store http://<this-ip>:{port} <backup-id> <dest>");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var ctx = listener.GetContextAsync().GetAwaiter().GetResult();
            _ = System.Threading.Tasks.Task.Run(() => HandleServeRequest(ctx, repoPath));
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        listener.Stop();
    }
    Console.WriteLine("Server stopped.");
    return 0;
}

static void HandleServeRequest(System.Net.HttpListenerContext ctx, string repoRoot)
{
    string chunksDir = Path.Combine(repoRoot, "chunks");
    string manifestsDir = Path.Combine(repoRoot, "manifests");
    try
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        string method = ctx.Request.HttpMethod;

        if (path == "/count" && method == "GET")
        {
            long count = Directory.Exists(chunksDir)
                ? Directory.GetFiles(chunksDir, "*", SearchOption.AllDirectories).Length
                : 0;
            byte[] buf = System.Text.Encoding.UTF8.GetBytes(count.ToString());
            ctx.Response.ContentType = "text/plain";
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.StatusCode = 200;
        }
        else if (path == "/manifests" && method == "GET")
        {
            // List available backup IDs (filenames without .json)
            var ids = Directory.Exists(manifestsDir)
                ? Directory.GetFiles(manifestsDir, "*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(id => id)
                    .ToArray()
                : Array.Empty<string>();
            string json = System.Text.Json.JsonSerializer.Serialize(ids);
            byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.StatusCode = 200;
        }
        else if (path.StartsWith("/manifests/", StringComparison.Ordinal) && method == "GET")
        {
            string backupId = path["/manifests/".Length..];
            // Validate: 32 hex chars (backup IDs are hex)
            if (backupId.Length != 32 || !backupId.All(Uri.IsHexDigit))
            {
                ctx.Response.StatusCode = 400;
            }
            else
            {
                string manifestPath = Path.Combine(manifestsDir, backupId + ".json");
                if (!File.Exists(manifestPath))
                {
                    ctx.Response.StatusCode = 404;
                }
                else
                {
                    byte[] data = File.ReadAllBytes(manifestPath);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.StatusCode = 200;
                }
            }
        }
        else if (path.StartsWith("/chunks/", StringComparison.Ordinal))
        {
            string chunkId = path["/chunks/".Length..];
            if (chunkId.Length != 64 || !chunkId.All(Uri.IsHexDigit))
            {
                ctx.Response.StatusCode = 400;
            }
            else
            {
                // Chunks are stored sharded: chunks/ab/cdef... (first 2 chars as dir)
                string chunkPath = Path.Combine(chunksDir, chunkId[..2], chunkId[2..]);
                if (method == "HEAD")
                {
                    ctx.Response.StatusCode = File.Exists(chunkPath) ? 200 : 404;
                }
                else if (method == "GET")
                {
                    if (!File.Exists(chunkPath))
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    else
                    {
                        byte[] data = File.ReadAllBytes(chunkPath);
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = data.Length;
                        ctx.Response.OutputStream.Write(data, 0, data.Length);
                        ctx.Response.StatusCode = 200;
                    }
                }
                else if (method == "POST")
                {
                    // LAN backup target: store incoming chunk
                    using var ms = new MemoryStream();
                    ctx.Request.InputStream.CopyTo(ms);
                    byte[] data = ms.ToArray();
                    string dir = Path.Combine(chunksDir, chunkId[..2]);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, chunkId[2..]), data);
                    ctx.Response.StatusCode = 200;
                }
                else
                {
                    ctx.Response.StatusCode = 405;
                }
            }
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    }
    catch (Exception ex)
    {
        try { ctx.Response.StatusCode = 500; } catch { }
        Console.Error.WriteLine($"serve error: {ex.Message}");
    }
    finally
    {
        try { ctx.Response.OutputStream.Close(); } catch { }
    }
}

/// <summary>
/// Checks Usenet upload expiration status.
/// Usage: usenet-backup expiration-check &lt;repo&gt; [--warn-days DAYS] [--retention-days DAYS]
/// Lists backups whose Usenet copies are expired or expiring soon.
/// </summary>
static int ExpirationCheck(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 1) { Console.Error.WriteLine("error: expiration-check <repo> [--warn-days DAYS]"); return 2; }
    string repoPath = Path.GetFullPath(pos[0]);
    int warnDays = int.TryParse(GetOption(args, "--warn-days"), out int w) ? w : 90;
    int defaultRetention = int.TryParse(GetOption(args, "--retention-days"), out int r) ? r : 1095;

    // Build per-provider retention map from service config if provided.
    // Otherwise fall back to the default for all hosts.
    var retentionByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    string? configPath = GetOption(args, "--config");
    if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
    {
        try
        {
            var svcConfig = UsenetBackup.Core.Service.ServiceConfig.Load(configPath);
            if (svcConfig.Nntp is not null && !string.IsNullOrWhiteSpace(svcConfig.Nntp.Host))
                retentionByHost[svcConfig.Nntp.Host] = svcConfig.Nntp.RetentionDays;
            foreach (var p in svcConfig.NntpProviders)
            {
                if (!string.IsNullOrWhiteSpace(p.Host))
                    retentionByHost[p.Host] = p.RetentionDays;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not load service config '{configPath}': {ex.Message}");
        }
    }

    int GetRetention(string host) =>
        retentionByHost.TryGetValue(host, out int days) ? days : defaultRetention;

    var tracker = new UsenetBackup.Core.Nntp.UsenetUploadTracker(repoPath);
    var expiring = tracker.GetExpiring(GetRetention, warnDays);

    if (expiring.Count == 0)
    {
        Console.WriteLine($"No Usenet uploads expiring within {warnDays} days.");
        return 0;
    }

    Console.WriteLine($"Usenet uploads expiring within {warnDays} days:");
    foreach (var (record, expires, daysLeft) in expiring)
    {
        int retention = GetRetention(record.ProviderHost);
        string status = daysLeft < 0 ? "EXPIRED" : $"{daysLeft}d left";
        Console.WriteLine($"  {record.BackupId[..8]}…  {record.ProviderHost}  uploaded {record.UploadedUtc:u}  expires {expires:u}  (retention {retention}d)  [{status}]");
    }
    return expiring.Any(x => x.DaysLeft < 0) ? 1 : 0;
}
