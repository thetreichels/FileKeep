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
        "restore" => Restore(args[1..]),
        "verify" => Verify(args[1..]),
        "list" => List(args[1..]),
        "nntp-check" => NntpCheck(args[1..]),
        "nntp-upload" => NntpUpload(args[1..]),
        "nzb-generate" => NzbGenerate(args[1..]),
        "download" => Download(args[1..]),
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
          usenet-backup backup <repo> <source-dir> [--parent <backup-id>]
          usenet-backup restore <repo> <backup-id> <dest-dir>
          usenet-backup verify <repo> <backup-id>
          usenet-backup list <repo>
          usenet-backup nntp-check --host HOST [--port PORT] [--ssl] [--user USER]
          usenet-backup nntp-upload <repo> <backup-id> --host HOST [--port PORT]
              [--ssl] [--user USER] [--newsgroup GROUP]
          usenet-backup nzb-generate <repo> <backup-id> <output.nzb>
              [--newsgroup GROUP] [--poster POSTER]
          usenet-backup download <repo> <nzb-file> --host HOST [--port PORT]
              [--ssl] [--user USER] [--newsgroup GROUP]

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
        return fromArg;
    string? fromEnv = Environment.GetEnvironmentVariable("USENETBACKUP_PASSPHRASE");
    if (!string.IsNullOrEmpty(fromEnv))
        return fromEnv;
    Console.Write("Passphrase: ");
    string? typed = Console.ReadLine();
    if (string.IsNullOrEmpty(typed))
        throw new InvalidOperationException("A passphrase is required.");
    return typed;
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
    if (pos.Length < 2) { Console.Error.WriteLine("error: backup <repo> <source-dir> [--parent <backup-id>]"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    string? parent = GetOption(args, "--parent");
    var manifest = parent is null
        ? repo.BackupDirectory(pos[1])
        : repo.BackupIncremental(pos[1], parent);
    Console.WriteLine($"{manifest.Type} backup {manifest.BackupId}" +
        (manifest.ParentId is null ? "" : $" (parent {manifest.ParentId})") +
        $": {manifest.Files.Count} files, {manifest.Files.Sum(f => f.Chunks.Count)} chunk refs, {repo.StoredChunkCount()} unique chunks stored.");
    return 0;
}

static int Restore(string[] args)
{
    var pos = Positionals(args);
    if (pos.Length < 3) { Console.Error.WriteLine("error: restore <repo> <backup-id> <dest-dir>"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
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
        return fromArg;
    string? fromEnv = Environment.GetEnvironmentVariable("USENETBACKUP_NNTP_PASSWORD");
    if (!string.IsNullOrEmpty(fromEnv))
        return fromEnv;
    Console.Write("NNTP password: ");
    return Console.ReadLine() ?? "";
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
        string? user = GetOption(args, "--user");
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
    string[] chunkIds = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToArray();

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
