using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsenetBackup.Core;
using UsenetBackup.Core.Nntp;
using UsenetBackup.Core.Security;
using UsenetBackup.Core.Service;

namespace UsenetBackup.Service;

/// <summary>
/// DashboardApi — WinRE recovery-media builder endpoints and build state.
/// </summary>
public static partial class DashboardApi
{
    /// <summary>WinRE USB build state.</summary>
    private static string? _winReBuildStatus;
    private static string? _winReBuildError;
    private static string? _winReBuildDrive;

    /// <summary>Handle of the PowerShell build process while a build runs (for cancel/timeout).</summary>
    private static System.Diagnostics.Process? _winReBuildProcess;
    private static volatile bool _winReBuildCancelRequested;

    /// <summary>Per-step progress for the running WinRE USB build.</summary>
    private static readonly List<Dictionary<string, object>> _winReBuildSteps = new();
    private static readonly object _winReBuildStepsLock = new();
    private static DateTime _winReBuildStartedAt;

    /// <summary>
    /// Checks prerequisites for WinRE USB creation: Windows, admin rights,
    /// reagentc, DISM, bcdboot, and WinRE enabled with a locatable WIM.
    /// ADK is NOT required.
    /// </summary>
    public static object CheckWinRePrerequisites()
    {
        var result = new Dictionary<string, object>();
        result["isWindows"] = OperatingSystem.IsWindows();
        if (!OperatingSystem.IsWindows())
        {
            result["ready"] = false;
            result["error"] = "WinRE USB creation requires Windows.";
            return result;
        }
        bool isAdmin = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        result["isAdmin"] = isAdmin;

        bool hasReagentc = FindSystemTool("reagentc.exe") is not null;
        bool hasDism = FindSystemTool("dism.exe") is not null;
        bool hasBcdboot = FindSystemTool("bcdboot.exe") is not null;
        result["reagentc"] = hasReagentc;
        result["dism"] = hasDism;
        result["bcdboot"] = hasBcdboot;

        // Check WinRE status via reagentc /info
        bool winReEnabled = false;
        string winReLocation = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "reagentc.exe",
                Arguments = "/info",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            winReEnabled = output.Contains("Enabled", StringComparison.OrdinalIgnoreCase) &&
                           !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Windows RE location:", StringComparison.OrdinalIgnoreCase))
                {
                    winReLocation = line.Split(':', 2)[1].Trim();
                    break;
                }
            }
        }
        catch { }
        result["winReEnabled"] = winReEnabled;
        result["winReLocation"] = winReLocation;

        // Check for the build script (installed layout or source tree)
        string scriptPath = FindWinReScript();
        result["scriptFound"] = !string.IsNullOrEmpty(scriptPath);

        var missing = new List<string>();
        if (!isAdmin) missing.Add("Administrator rights");
        if (!hasReagentc) missing.Add("reagentc.exe");
        if (!hasDism) missing.Add("dism.exe");
        if (!hasBcdboot) missing.Add("bcdboot.exe");
        if (!winReEnabled) missing.Add("WinRE enabled (reagentc /info)");
        if (string.IsNullOrEmpty(scriptPath)) missing.Add("build-winre-usb.ps1");

        result["ready"] = missing.Count == 0;
        result["missing"] = missing;
        return result;
    }

    private static string? FindSystemTool(string name)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch { return null; }
    }

    private static string FindWinReScript()
    {
        // Installed layout: <base>/winre/build-winre-usb.ps1
        string installed = Path.Combine(AppContext.BaseDirectory, "winre", "build-winre-usb.ps1");
        if (File.Exists(installed)) return installed;
        // Source tree: walk up from base directory
        string? dir = Path.GetDirectoryName(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "winre", "build-winre-usb.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    /// <summary>Current WinRE USB build status for the dashboard, with per-step progress.</summary>
    public static object GetWinReBuildStatus()
    {
        // Snapshot everything under the lock so the status, error, target,
        // and steps are always mutually consistent.
        List<Dictionary<string, object>> steps;
        string? status;
        string? error;
        string? drive;
        DateTime startedAt;
        lock (_winReBuildStepsLock)
        {
            steps = _winReBuildSteps.Select(s => new Dictionary<string, object>(s)).ToList();
            status = _winReBuildStatus;
            error = _winReBuildError;
            drive = _winReBuildDrive;
            startedAt = _winReBuildStartedAt;
        }

        int percent = 0;
        int etaSec = 0;
        string currentStepName = "";
        foreach (var s in steps)
        {
            if (s.TryGetValue("percent", out var pv) && pv is int pi) percent = Math.Max(percent, pi);
            if (s.TryGetValue("state", out var st) && st as string == "started")
            {
                currentStepName = s.TryGetValue("name", out var n) ? n as string ?? "" : "";
                if (s.TryGetValue("estimatedRemainingSec", out var ev) && ev is int ei) etaSec = ei;
            }
        }
        if (status == "complete") percent = 100;

        double elapsedSec = status == "running"
            ? (DateTime.UtcNow - startedAt).TotalSeconds
            : 0;

        return new
        {
            status = status ?? "idle",
            error = error ?? "",
            drive = drive ?? "",
            percent,
            currentStep = currentStepName,
            estimatedRemainingSec = etaSec,
            elapsedSec = (int)elapsedSec,
            steps,
        };
    }

    /// <summary>
    /// Starts a WinRE recovery media build in the background. Returns 202 if accepted.
    /// USB mode (driveLetter or diskNumber): destructive, the target USB drive is formatted.
    /// Image mode (imagePath): builds a bootable .vhdx disk image file instead.
    /// </summary>
    public static (int Status, object Payload) StartWinReBuild(string driveLetter, int diskNumber, string imagePath, string? sourceDir, string bootMode, int imageSizeMB)
    {
        if (!OperatingSystem.IsWindows())
            return (400, new ErrorDto("WinRE recovery media creation requires Windows."));
        bootMode = (bootMode ?? "Both").Trim();
        if (bootMode != "UEFI" && bootMode != "BIOS" && bootMode != "Both")
            return (400, new ErrorDto("bootMode must be UEFI, BIOS, or Both."));

        // Validate everything BEFORE claiming the running flag: every return
        // below must leave _winReBuildStatus untouched, or a rejected request
        // wedges the builder as "running" forever (a retry then reports
        // "A WinRE build is already running." with nothing actually running).
        bool imageMode = !string.IsNullOrWhiteSpace(imagePath);
        if (imageMode && (!string.IsNullOrWhiteSpace(driveLetter) || diskNumber >= 0))
            return (400, new ErrorDto("Specify either a USB target or an image path, not both."));
        if (!imageMode)
        {
            driveLetter = driveLetter.Trim().TrimEnd(':', '\\');
            bool hasLetter = driveLetter.Length == 1 && char.IsLetter(driveLetter[0]);
            bool hasDisk = diskNumber >= 0;
            if (!hasLetter && !hasDisk)
                return (400, new ErrorDto("Select a USB drive."));
            if (hasLetter && hasDisk)
                return (400, new ErrorDto("Specify either a drive letter or a disk number, not both."));
            if (hasDisk)
            {
                // The dashboard only offers USB-bus disks; refuse anything else
                // even if the request was crafted by hand. This is checked here
                // (before the flag) rather than only in the script.
                bool knownUsb;
                try
                {
                    knownUsb = UsenetBackup.Core.Recovery.UsbDrives.List().Any(d => d.Number == diskNumber);
                }
                catch
                {
                    knownUsb = false;
                }
                if (!knownUsb)
                    return (400, new ErrorDto($"Disk {diskNumber} is not a USB-attached drive." ));
            }
        }
        else
        {
            var (normalizedPath, pathError) = NormalizeWinReImagePath(imagePath);
            if (pathError is not null)
                return (400, new ErrorDto(pathError));
            imagePath = normalizedPath!;
            if (imageSizeMB < 1024)
                return (400, new ErrorDto("Image size must be at least 1024 MB."));
        }

        var prereq = CheckWinRePrerequisites() as Dictionary<string, object>;
        if (prereq is null || !(prereq.TryGetValue("ready", out var readyObj) && readyObj is bool ready && ready))
        {
            var missing = prereq?.TryGetValue("missing", out var m) == true ? m : "prerequisites not met";
            return (400, new ErrorDto($"Prerequisites not met: {missing}" ));
        }

        lock (_winReBuildStepsLock)
        {
            if (_winReBuildStatus == "running")
                return (400, new ErrorDto("A WinRE build is already running."));
            _winReBuildStatus = "running";
            _winReBuildSteps.Clear();
            _winReBuildStartedAt = DateTime.UtcNow;
            _winReBuildProcess = null;
            _winReBuildCancelRequested = false;
            _winReBuildError = null;
            _winReBuildDrive = imageMode ? imagePath
                : diskNumber >= 0 ? $"Disk {diskNumber}" : driveLetter.ToUpperInvariant() + ":";
        }

        Task.Run(() =>
        {
            try
            {
                string scriptPath = FindWinReScript();
                if (string.IsNullOrEmpty(scriptPath))
                    throw new FileNotFoundException("build-winre-usb.ps1 not found.");

                // In the installed layout, use binary mode. Detect by the
                // presence of the installed binaries next to the script's
                // grandparent dir (...\FileKeep\winre\ -> ...\FileKeep\),
                // not by AppContext.BaseDirectory (the service exe lives in
                // ...\FileKeep\service\, so the old StartsWith check was
                // always false on installed systems).
                string? scriptGrandparent = Path.GetDirectoryName(Path.GetDirectoryName(scriptPath));
                bool isInstalledLayout = scriptGrandparent is not null &&
                    File.Exists(Path.Combine(scriptGrandparent, "cli", "FileKeep.exe"));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // Use ArgumentList, not an Arguments string: user-controlled
                // values (imagePath, sourceDir) must not be able to break out
                // of quoting and inject PowerShell statements.
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);
                if (imageMode)
                {
                    psi.ArgumentList.Add("-ImagePath");
                    psi.ArgumentList.Add(imagePath);
                }
                else if (diskNumber >= 0)
                {
                    psi.ArgumentList.Add("-DiskNumber");
                    psi.ArgumentList.Add(diskNumber.ToString());
                }
                else
                {
                    psi.ArgumentList.Add("-DriveLetter");
                    psi.ArgumentList.Add(driveLetter);
                }
                if (!imageMode)
                {
                    // The dashboard already confirmed the destructive format
                    // with the user, so the script must not prompt (it runs
                    // headless under the service, where a prompt can only fail
                    // or hang).
                    psi.ArgumentList.Add("-Force");
                }
                else
                {
                    psi.ArgumentList.Add("-ImageSizeMB");
                    psi.ArgumentList.Add(imageSizeMB.ToString());
                }
                psi.ArgumentList.Add("-BootMode");
                psi.ArgumentList.Add(bootMode);
                if (isInstalledLayout && scriptGrandparent is not null)
                {
                    psi.ArgumentList.Add("-BinaryDir");
                    psi.ArgumentList.Add(scriptGrandparent);
                }
                if (!string.IsNullOrWhiteSpace(sourceDir))
                {
                    psi.ArgumentList.Add("-SourceDir");
                    psi.ArgumentList.Add(sourceDir);
                }
                using var proc = System.Diagnostics.Process.Start(psi)!;
                lock (_winReBuildStepsLock) { _winReBuildProcess = proc; }
                try
                {
                    // Read stdout line-by-line so FKPROGRESS markers update the
                    // dashboard in real time instead of only at the end.
                    var outputLines = new System.Text.StringBuilder();
                    string? line;
                    while ((line = proc.StandardOutput.ReadLine()) is not null)
                    {
                        outputLines.AppendLine(line);
                        if (line.StartsWith("FKPROGRESS:", StringComparison.Ordinal))
                            ParseWinReProgress(line.Substring("FKPROGRESS:".Length).Trim());
                    }
                    string err = proc.StandardError.ReadToEnd();
                    if (_winReBuildCancelRequested)
                        throw new OperationCanceledException("WinRE build cancelled.");
                    if (!proc.WaitForExit(TimeSpan.FromMinutes(60)))
                    {
                        // Backstop against a hung script: a stuck DISM/diskpart
                        // would otherwise wedge the status as "running" until
                        // the service restarts. The dashboard also offers
                        // Cancel for user-initiated stops.
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        throw new TimeoutException("WinRE build timed out after 60 minutes and was stopped.");
                    }
                    if (proc.ExitCode != 0)
                        throw new InvalidOperationException($"build-winre-usb.ps1 exited {proc.ExitCode}: {err}{outputLines}");
                    _winReBuildStatus = "complete";
                    LogWinReBuild($"target={_winReBuildDrive}: complete");
                }
                finally
                {
                    lock (_winReBuildStepsLock)
                    {
                        if (_winReBuildProcess == proc) _winReBuildProcess = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Set the terminal state BEFORE logging so a logging failure
                // can never wedge the builder as "running".
                _winReBuildStatus = "failed";
                // Cap the message: a failed build can dump megabytes of
                // DISM/diskpart output; the tail is where the error is.
                _winReBuildError = TruncateTail(ex.Message, 4096);
                LogWinReBuild($"target={_winReBuildDrive}: FAILED: {_winReBuildError}");
            }
        });
        return (202, new AcceptedDto(true));
    }

    /// <summary>
    /// Forgiving normalization for the image-mode output path. Accepts a folder
    /// (an existing directory, or a path with no file name, gets the default
    /// file name) or a path without an extension (".vhdx" is appended); only a
    /// non-.vhdx extension is rejected.
    /// </summary>
    private static (string? Path, string? Error) NormalizeWinReImagePath(string raw)
    {
        string p = raw.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.Length == 0)
            return (null, "Choose where to save the recovery image (full file path, e.g. D:\\images\\filekeep-recovery.vhdx).");
        if (Directory.Exists(p) || string.IsNullOrEmpty(Path.GetFileName(p)))
            p = Path.Combine(p, "filekeep-recovery.vhdx");
        else if (string.IsNullOrEmpty(Path.GetExtension(p)))
            p += ".vhdx";
        else if (!p.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            return (null, $"The image path must end with .vhdx (got \"{p}\").");
        return (p, null);
    }

    /// <summary>FileKeep's machine-wide data dir (service config and logs live here).</summary>
    private static string FileKeepDataDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileKeep");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Appends a winre-build line to the data-dir operations log. Never throws.</summary>
    private static void LogWinReBuild(string details)
    {
        try
        {
            string line = $"{DateTime.UtcNow:O} [winre-build] {details}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(FileKeepDataDir(), OperationLog.FileName), line);
        }
        catch
        {
            // Logging must never fail or wedge a build.
        }
    }

    /// <summary>Keeps only the tail of a long message (a failed build can dump MBs of tool output).</summary>
    private static string TruncateTail(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return "[…truncated…]" + s.Substring(s.Length - maxLen);
    }

    /// <summary>
    /// Requests cancellation of the running WinRE build by killing its process
    /// tree. The build task observes the kill and reports "failed: cancelled".
    /// </summary>
    public static (int Status, object Payload) CancelWinReBuild()
    {
        lock (_winReBuildStepsLock)
        {
            if (_winReBuildStatus != "running")
                return (400, new ErrorDto("No WinRE build is running."));
            _winReBuildCancelRequested = true;
            try { _winReBuildProcess?.Kill(entireProcessTree: true); } catch { }
            return (200, new CancelledDto(true));
        }
    }

    /// <summary>Parses one FKPROGRESS JSON line from build-winre-usb.ps1.</summary>
    private static void ParseWinReProgress(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            int step = root.GetProperty("step").GetInt32();
            int totalSteps = root.GetProperty("totalSteps").GetInt32();
            string name = root.GetProperty("name").GetString() ?? $"Step {step}";
            string state = root.GetProperty("state").GetString() ?? "";
            int percent = root.TryGetProperty("percent", out var p) ? p.GetInt32() : 0;
            int etaSec = root.TryGetProperty("estimatedRemainingSec", out var e) ? e.GetInt32() : 0;

            lock (_winReBuildStepsLock)
            {
                // Ensure the list covers all steps seen so far.
                while (_winReBuildSteps.Count < totalSteps)
                    _winReBuildSteps.Add(new Dictionary<string, object>
                    {
                        ["step"] = _winReBuildSteps.Count + 1,
                        ["name"] = "",
                        ["state"] = "pending",
                    });
                var entry = _winReBuildSteps[step - 1];
                entry["name"] = name;
                entry["state"] = state;
                entry["percent"] = percent;
                entry["estimatedRemainingSec"] = etaSec;
                if (state == "started" && !entry.ContainsKey("startedAt"))
                    entry["startedAt"] = DateTime.UtcNow.ToString("o");
                if (state == "completed")
                    entry["completedAt"] = DateTime.UtcNow.ToString("o");
            }
        }
        catch
        {
            // A malformed progress line must not break the build.
        }
    }
}
