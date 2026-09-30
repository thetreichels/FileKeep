using UsenetBackup.Core;

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
        usenet-backup — Milestone 1: local repository engine

        Usage:
          usenet-backup init <repo> [--chunk-size BYTES]
          usenet-backup backup <repo> <source-dir>
          usenet-backup restore <repo> <backup-id> <dest-dir>
          usenet-backup verify <repo> <backup-id>
          usenet-backup list <repo>

        The passphrase is read from --passphrase, the USENETBACKUP_PASSPHRASE
        environment variable, or an interactive prompt (in that order).
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
    if (pos.Length < 2) { Console.Error.WriteLine("error: backup <repo> <source-dir>"); return 2; }
    using var repo = BackupRepository.Open(pos[0], GetPassphrase(args));
    var manifest = repo.BackupDirectory(pos[1]);
    Console.WriteLine($"Backup {manifest.BackupId}: {manifest.Files.Count} files, {manifest.Files.Sum(f => f.Chunks.Count)} chunk refs, {repo.StoredChunkCount()} unique chunks stored.");
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
