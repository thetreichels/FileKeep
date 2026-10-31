using System.Security.Cryptography;
using System.Text.Json;
using FileKeep.Core;
using FileKeep.Reader;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

// FileKeep portable reader (Stealth Folders slice 3, part 3).
// Runs from a stick, no install, no registry, no host writes
// except the host apps' own copies. Two secrets, prompted every
// time, held only in memory, zeroed on lock/exit: the export
// passphrase opens the package, the vault secret (typed passphrase
// or generated secret — the same secret that unlocks the card on
// the main machine) derives the file-content key. The stick never
// carries a secret, and the package never carries credentials.
//
// Hostile host is out of scope: a typed passphrase can be
// keylogged and opened files pass through host RAM. The unlock
// screen says so and requires an explicit YES before proceeding.
//
// Usage: FileKeepReader [stick-folder] [--port N]
//   stick-folder defaults to the folder above the exe when the exe
//   lives in a FileKeepReader folder, else the current folder.

string? stickArg = null;
int? portArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
    {
        portArg = p;
        i++;
    }
    else if (stickArg is null)
    {
        stickArg = args[i];
    }
    else
    {
        Console.WriteLine($"Unknown argument: {args[i]}");
        return 1;
    }
}

string stickRoot;
if (!string.IsNullOrWhiteSpace(stickArg))
{
    stickRoot = Path.GetFullPath(stickArg);
}
else
{
    string exeDir = AppContext.BaseDirectory;
    string? parent = Directory.GetParent(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
    stickRoot = string.Equals(Path.GetFileName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
        OfflineStickLayout.ReaderFolderName, StringComparison.OrdinalIgnoreCase) && parent is not null
        ? parent
        : Directory.GetCurrentDirectory();
}

Console.WriteLine("FileKeep portable reader — Stealth Folders, no install.");
Console.WriteLine("WARNING: this host is not trusted. A typed passphrase can be");
Console.WriteLine("keylogged on a hostile machine, and files you open pass through");
Console.WriteLine("this host's memory. Only continue on a machine you trust.");
Console.Write("Type YES to continue on this host: ");
if (!string.Equals(Console.ReadLine(), "YES", StringComparison.Ordinal))
{
    Console.WriteLine("Aborted.");
    return 1;
}

var packages = OfflineStickLayout.DiscoverPackages(stickRoot);
if (packages.Count == 0)
{
    Console.WriteLine($"No export package found under {stickRoot}.");
    Console.WriteLine($"Expected {OfflineStickLayout.ReaderFolderName}\\{OfflineStickLayout.ManifestFileName},");
    Console.WriteLine("or any *.filekeep-export.json in that folder or at the stick root.");
    return 1;
}
string packagePath = packages[0];
if (packages.Count > 1)
{
    Console.WriteLine("Multiple export packages found:");
    for (int i = 0; i < packages.Count; i++)
        Console.WriteLine($"  {i + 1}. {packages[i]}");
    Console.Write("Choose one (number): ");
    if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > packages.Count)
    {
        Console.WriteLine("Aborted.");
        return 1;
    }
    packagePath = packages[choice - 1];
}
Console.WriteLine($"Package: {packagePath}");

OfflineExportEnvelope.Package envelope;
try
{
    envelope = JsonSerializer.Deserialize(
        File.ReadAllText(packagePath),
        FileKeep.Core.Json.FileKeepJsonContext.Default.Package)
        ?? throw new InvalidDataException("Export package is empty.");
}
catch (Exception ex) when (ex is JsonException or InvalidDataException)
{
    Console.WriteLine($"That file is not a FileKeep export package: {ex.Message}");
    return 1;
}

Console.Write("Export passphrase: ");
string exportPassphrase = ReadSecret();
OfflineExportEnvelope.Payload payload;
try
{
    payload = OfflineExportEnvelope.DecryptPackage(envelope, exportPassphrase);
}
catch (InvalidDataException ex)
{
    // Format mismatch refuses fail-closed, with a clear error.
    Console.WriteLine($"Cannot open this package: {ex.Message}");
    return 1;
}
catch (CryptographicException)
{
    Console.WriteLine("Cannot open this package: wrong export passphrase, or the file is damaged.");
    return 1;
}

Console.WriteLine($"Folder: {payload.Folder} ({payload.Files.Count} files, location: {payload.Location.Type} '{payload.Location.Name}').");
Console.Write("Vault secret (typed passphrase or generated secret): ");
string vaultSecret = ReadSecret();
byte[] contentKey;
try
{
    contentKey = OfflineChunkStore.DeriveContentKey(vaultSecret);
}
finally
{
    // The vault secret string itself cannot be zeroed (strings are
    // immutable); it is a transient local, never logged or stored.
    vaultSecret = "";
}

string packageDir = Path.GetDirectoryName(packagePath) ?? stickRoot;
var stickStore = new LocalBlobStore(packageDir);
var original = ReaderSession.ResolveOriginalStore(payload.Location,
    _ => ReadSecretPrompt("SMB password"),
    () => Prompt("SMB username (blank for current user)"),
    out var originalDisposables);
Console.WriteLine(original is not null
    ? "Original location reached — missing stick chunks will fall back to it."
    : "Original location not reached — only stick chunks are available.");

ReaderSession? session = null;
try
{
    session = new ReaderSession(packagePath, payload, contentKey, exportPassphrase,
        stickStore, original, originalDisposables);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        session?.Lock();
        Console.WriteLine();
        Console.WriteLine("Interrupted — session locked.");
        Environment.Exit(0);
    };

    while (session is not null)
    {
        var handler = new ReaderDavHandler(session);
        var builder = WebApplication.CreateBuilder();
        // Retrieve writes synchronously, chunk by chunk, to the
        // stream it is given; Kestrel disallows synchronous writes
        // by default, so the loopback-only reader opts in. (The
        // main app's DAV adapter wraps the same call in Task.Run,
        // which does NOT bypass this ban — its through-Kestrel GET
        // path is unproven and likely shares the failure; flagged
        // in the slice 3 report, not changed here.)
        builder.WebHost.ConfigureKestrel(o => o.AllowSynchronousIO = true);
        // Loopback-only, fail-closed: the reader serves decrypted content
        // with no authentication. The guard runs even though the bind is
        // currently hardcoded, so a future change that makes it
        // configurable cannot silently open it to the network.
        string readerBind = "127.0.0.1";
        FileKeep.Core.Service.LoopbackGuard.RequireLoopbackBind(readerBind);
        builder.WebHost.UseUrls(portArg is not null
            ? $"http://{readerBind}:{portArg.Value}"
            : $"http://{readerBind}:0");
        var app = builder.Build();
        app.Map("{**path}", async (HttpContext ctx) =>
        {
            var segments = (ctx.Request.RouteValues["path"] as string ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString).ToList();
            byte[] body = Array.Empty<byte>();
            if (ctx.Request.ContentLength is > 0)
            {
                using var ms = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                body = ms.ToArray();
            }
            string? lockToken = ctx.Request.Headers.TryGetValue("Lock-Token", out var lt)
                ? lt.ToString()
                : null;
            if (lockToken is null && ctx.Request.Headers.TryGetValue("If", out var ifHeader))
            {
                string ifText = ifHeader.ToString();
                int at = ifText.IndexOf("opaquelocktoken:", StringComparison.Ordinal);
                if (at >= 0)
                {
                    string rest = ifText[(at + "opaquelocktoken:".Length)..];
                    int end = rest.IndexOfAny(new[] { '>', ' ', ')' });
                    lockToken = "opaquelocktoken:" + (end >= 0 ? rest[..end] : rest);
                }
            }
            var response = handler.Handle(new ReaderDavHandler.Request
            {
                Method = ctx.Request.Method,
                Segments = segments,
                Depth = ctx.Request.Headers.TryGetValue("Depth", out var d) ? d.ToString() : "1",
                Body = body,
                LockToken = lockToken,
            });
            ctx.Response.StatusCode = response.Status;
            foreach (var h in response.Headers)
                ctx.Response.Headers[h.Key] = h.Value;
            if (response.ContentType is not null)
                ctx.Response.ContentType = response.ContentType;
            if (response.StreamBody is not null)
            {
                try { await response.StreamBody(ctx.Response.Body); }
                catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException)
                {
                    // Pre-flight Exists checks make a mid-stream failure
                    // rare (lock during transfer); the partial stream is
                    // the host app's problem, and the message goes to the
                    // console, not into the file.
                    Console.WriteLine($"Stream aborted: {ex.Message}");
                }
            }
            else if (response.Body is not null)
            {
                await ctx.Response.Body.WriteAsync(response.Body);
            }
        });
        await app.StartAsync();
        string davRoot = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:0";
        Console.WriteLine($"Share running on loopback only: {davRoot}/");
        Console.WriteLine("Commands: list | open <n> | decrypt-to <n> <folder> | lock | exit");
        bool relock = false;
        bool quit = false;
        while (!relock && !quit)
        {
            Console.Write("reader> ");
            string? line = Console.ReadLine();
            if (line is null) { quit = true; break; }
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            switch (parts[0].ToLowerInvariant())
            {
                case "list":
                    if (session.Files.Count == 0) { Console.WriteLine("(no files)"); break; }
                    for (int i = 0; i < session.Files.Count; i++)
                    {
                        var f = session.Files[i];
                        int present = f.ChunkIds.Count(c => session.Store.Exists(c));
                        string avail = present == f.ChunkIds.Count ? "complete"
                            : $"incomplete ({present}/{f.ChunkIds.Count} chunks present)";
                        Console.WriteLine($"{i + 1}. {f.Path} ({f.Size} bytes) — {avail}");
                    }
                    break;
                case "open":
                    if (!TryFile(parts, out var openFile)) break;
                    Console.WriteLine($"Open in File Explorer or your app: {davRoot}/{string.Join("/", openFile!.Path.Split('/').Select(Uri.EscapeDataString))}");
                    Console.WriteLine("Opened files pass through this host's memory; nothing is written to disk by the reader.");
                    break;
                case "decrypt-to":
                    if (parts.Length < 3) { Console.WriteLine("Usage: decrypt-to <n> <folder>"); break; }
                    if (!TryFile(parts, out var decFile)) break;
                    Console.WriteLine("WARNING: this writes a PLAINTEXT copy to the host. FileKeep does not track, reload, or clean it after.");
                    try
                    {
                        Directory.CreateDirectory(parts[2]);
                        string dest = Path.Combine(parts[2], Path.GetFileName(decFile!.Path.Replace('/', Path.DirectorySeparatorChar)));
                        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
                            OfflineChunkStore.Retrieve(decFile.ChunkIds, session.Store, session.ContentKey, fs, () => session.IsUnlocked);
                        Console.WriteLine($"Wrote {dest} ({decFile.Size} bytes).");
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or IOException)
                    {
                        // Never leave a partial/garbled file behind.
                        try
                        {
                            string dest = Path.Combine(parts[2], Path.GetFileName(decFile!.Path.Replace('/', Path.DirectorySeparatorChar)));
                            if (File.Exists(dest)) File.Delete(dest);
                        }
                        catch { /* best effort */ }
                        Console.WriteLine($"Could not decrypt '{decFile!.Path}': {ex.Message}");
                    }
                    break;
                case "lock":
                    relock = true;
                    break;
                case "exit":
                case "quit":
                    quit = true;
                    break;
                case "help":
                    Console.WriteLine("list — files in the manifest, with chunk completeness");
                    Console.WriteLine("open <n> — print the loopback share URL for a file (memory-only)");
                    Console.WriteLine("decrypt-to <n> <folder> — write a plaintext copy to a folder (one-way, untracked)");
                    Console.WriteLine("lock — lock the session (keys zeroed); unlock again or exit");
                    Console.WriteLine("exit — lock and quit");
                    break;
                default:
                    Console.WriteLine("Unknown command. Try: list | open <n> | decrypt-to <n> <folder> | lock | exit");
                    break;
            }

            bool TryFile(string[] p, out ReaderSession.Entry? found)
            {
                found = null;
                if (p.Length < 2 || !int.TryParse(p[1], out int n) || n < 1 || n > session!.Files.Count)
                {
                    Console.WriteLine("Usage: <command> <n> — see 'list' for numbers.");
                    return false;
                }
                found = session.Files[n - 1];
                return true;
            }
        }
        await app.StopAsync();
        handler.DropLocks();
        session.Lock();
        if (quit) break;
        Console.Write("Unlock again? (y/n): ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase))
            break;
        // Re-read the package (PUT/DELETE may have re-encrypted it),
        // then re-prompt both secrets.
        try
        {
            envelope = JsonSerializer.Deserialize(
                File.ReadAllText(packagePath),
                FileKeep.Core.Json.FileKeepJsonContext.Default.Package)
                ?? throw new InvalidDataException("Export package is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            Console.WriteLine($"Cannot re-open the package: {ex.Message}");
            break;
        }
        Console.Write("Export passphrase: ");
        exportPassphrase = ReadSecret();
        try { payload = OfflineExportEnvelope.DecryptPackage(envelope, exportPassphrase); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            Console.WriteLine($"Cannot open this package: {ex.Message}");
            break;
        }
        Console.Write("Vault secret: ");
        string vault2 = ReadSecret();
        byte[] key2;
        try { key2 = OfflineChunkStore.DeriveContentKey(vault2); }
        finally { vault2 = ""; }
        var original2 = ReaderSession.ResolveOriginalStore(payload.Location,
            _ => ReadSecretPrompt("SMB password"),
            () => Prompt("SMB username (blank for current user)"),
            out var disposables2);
        session.Dispose();
        session = new ReaderSession(packagePath, payload, key2, exportPassphrase,
            stickStore, original2, disposables2);
    }
}
finally
{
    session?.Dispose();
    // The content key is zeroed by Lock/Dispose; the passphrase
    // strings are transient locals, never logged or stored.
}

return 0;

static string Prompt(string label)
{
    Console.Write($"{label}: ");
    return Console.ReadLine() ?? "";
}

static string ReadSecretPrompt(string label)
{
    Console.Write($"{label}: ");
    return ReadSecret();
}

static string ReadSecret()
{
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? "";
    var chars = new List<char>();
    try
    {
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                    Console.Write("\b \b");
                }
                continue;
            }
            chars.Add(key.KeyChar);
            Console.Write("*");
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
    finally
    {
        for (int i = 0; i < chars.Count; i++) chars[i] = '\0';
        chars.Clear();
    }
}
