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
    // P2-C13: build generation. A build task may write terminal state
    // only while it is still the current generation — otherwise a slow
    // cleanup in an old (killed) build could stamp its outcome onto a
    // newer build that already took over the status.
    private static long _winReBuildGeneration;
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
    public static WinRePrerequisitesDto CheckWinRePrerequisites()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinRePrerequisitesDto(
                IsWindows: false, Ready: false,
                Error: "WinRE USB creation requires Windows.",
                IsAdmin: false, Reagentc: false, Dism: false, Bcdboot: false,
                WinReEnabled: false, WinReLocation: "", ScriptFound: false,
                Missing: new List<string>());
        }
        bool isAdmin = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }

        bool hasReagentc = FindSystemTool("reagentc.exe") is not null;
        bool hasDism = FindSystemTool("dism.exe") is not null;
        bool hasBcdboot = FindSystemTool("bcdboot.exe") is not null;

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
            var (_, output) = RunToolCapture(psi, 10000);
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

        // Check for the build script (installed layout or source tree)
        string scriptPath = FindWinReScript();
        bool scriptFound = !string.IsNullOrEmpty(scriptPath);

        var missing = new List<string>();
        if (!isAdmin) missing.Add("Administrator rights");
        if (!hasReagentc) missing.Add("reagentc.exe");
        if (!hasDism) missing.Add("dism.exe");
        if (!hasBcdboot) missing.Add("bcdboot.exe");
        if (!winReEnabled) missing.Add("WinRE enabled (reagentc /info)");
        if (!scriptFound) missing.Add("build-winre-usb.ps1");

        return new WinRePrerequisitesDto(
            IsWindows: true, Ready: missing.Count == 0, Error: "",
            IsAdmin: isAdmin, Reagentc: hasReagentc, Dism: hasDism,
            Bcdboot: hasBcdboot, WinReEnabled: winReEnabled,
            WinReLocation: winReLocation, ScriptFound: scriptFound,
            Missing: missing);
    }

    /// <summary>
    /// P2-C17: runs a short-lived tool capturing stdout, draining BOTH
    /// streams concurrently and enforcing the timeout for real. The
    /// old pattern (ReadToEnd on stdout, then WaitForExit) deadlocks
    /// when a child fills its stderr pipe first (it never reaches EOF)
    /// and makes the WaitForExit timeout unreachable — the same bug
    /// SF-8 fixed for the build itself, in the three helpers around it.
    /// Returns (exitCode, stdout); exitCode is -1 on timeout.
    /// </summary>
    private static (int ExitCode, string Output) RunToolCapture(
        System.Diagnostics.ProcessStartInfo psi, int timeoutMs)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(5000); } catch { }
            return (-1, stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : "");
        }
        string output = stdoutTask.Wait(TimeSpan.FromSeconds(5)) ? stdoutTask.Result : "";
        try { _ = stderrTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        return (proc.ExitCode, output);
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
                CreateNoWindow = true,
            };
            var (_, output) = RunToolCapture(psi, 5000);
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

    /// <summary>
    /// Current WinRE USB build status for the dashboard, with per-step
    /// progress. Returns the named <see cref="WinReBuildStatusDto"/> —
    /// an anonymous type here made the endpoint return HTTP 500 under
    /// the trimmed service (WALK-9; same trap as the bc4158f fix).
    /// </summary>
    public static WinReBuildStatusDto GetWinReBuildStatus()
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

        // Convert the loosely-typed step snapshots into the named step
        // DTO. startedAt/completedAt bookkeeping stays internal — the
        // dashboard polls only step/name/state/percent/eta.
        static int StepInt(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is int i ? i : 0;
        static string StepStr(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) ? v as string ?? "" : "";
        var stepDtos = steps.Select(s => new WinReBuildStepDto(
            StepInt(s, "step"), StepStr(s, "name"), StepStr(s, "state"),
            StepInt(s, "percent"), StepInt(s, "estimatedRemainingSec"))).ToList();

        return new WinReBuildStatusDto(
            status ?? "idle",
            error ?? "",
            drive ?? "",
            percent,
            currentStepName,
            etaSec,
            (int)elapsedSec,
            stepDtos);
    }

    /// <summary>
    /// Starts a WinRE recovery media build in the background. Returns 202 if accepted.
    /// USB mode (driveLetter or diskNumber): destructive, the target USB drive is formatted.
    /// Image mode (imagePath): builds a bootable .vhdx disk image file instead.
    /// </summary>
    public static (int Status, object Payload) StartWinReBuild(string driveLetter, int diskNumber, string imagePath, string? sourceDir, string bootMode, int imageSizeMB,
        string? expectedModel = null, long? expectedSizeBytes = null)
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
                UsenetBackup.Core.Recovery.UsbDrives.UsbDrive? drive;
                try
                {
                    drive = UsenetBackup.Core.Recovery.UsbDrives.List()
                        .FirstOrDefault(d => d.Number == diskNumber);
                }
                catch
                {
                    drive = null;
                }
                if (drive is null)
                    return (400, new ErrorDto($"Disk {diskNumber} is not a USB-attached drive." ));
                // P2-L2: a disk NUMBER is not an identity — disks renumber
                // when devices are added or removed. When the page sent
                // the identity of the disk the user confirmed, refuse if
                // the number now names a different disk; either way the
                // verified identity is forwarded to the script, which
                // re-checks it immediately before formatting.
                if (!string.IsNullOrWhiteSpace(expectedModel) &&
                    (!string.Equals(drive.Model, expectedModel, StringComparison.Ordinal) ||
                     (expectedSizeBytes.HasValue && drive.SizeBytes != expectedSizeBytes.Value)))
                    return (400, new ErrorDto(
                        $"The disk at number {diskNumber} changed since you selected it " +
                        $"(now: {drive.Model}, {drive.SizeBytes} bytes). Re-select the target disk and confirm again."));
                expectedModel = drive.Model;
                expectedSizeBytes = drive.SizeBytes;
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

        var prereq = CheckWinRePrerequisites();
        if (!prereq.Ready)
        {
            string missing = prereq.Missing.Count > 0
                ? string.Join(", ", prereq.Missing)
                : "prerequisites not met";
            return (400, new ErrorDto($"Prerequisites not met: {missing}"));
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
            _winReBuildGeneration++;
        }
        long buildGeneration = _winReBuildGeneration;

        Task.Run(() =>
        {
            // Set when the build process is killed out from under the
            // script (user cancel or the 60-minute backstop): the
            // script's own cleanup never runs then (SF-7).
            bool buildWasKilled = false;
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
                    if (!string.IsNullOrWhiteSpace(expectedModel))
                    {
                        psi.ArgumentList.Add("-ExpectedModel");
                        psi.ArgumentList.Add(expectedModel);
                    }
                    if (expectedSizeBytes.HasValue)
                    {
                        psi.ArgumentList.Add("-ExpectedSizeBytes");
                        psi.ArgumentList.Add(expectedSizeBytes.Value.ToString());
                    }
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
                    // SF-8: the run goes through RunBuildProcess, which
                    // streams stdout (FKPROGRESS markers update the
                    // dashboard in real time) while draining stderr
                    // concurrently and enforcing the 60-minute backstop
                    // for real — reading stdout to EOF *before*
                    // WaitForExit, as this used to do, made the timeout
                    // unreachable (a hung DISM/diskpart never EOFs, so
                    // the status wedged at "running" until the service
                    // restarted). The dashboard also offers Cancel for
                    // user-initiated stops.
                    var (timedOut, exitCode, err, outputText) = RunBuildProcess(
                        proc, TimeSpan.FromMinutes(60),
                        line =>
                        {
                            if (line.StartsWith("FKPROGRESS:", StringComparison.Ordinal))
                                ParseWinReProgress(line.Substring("FKPROGRESS:".Length).Trim());
                        });
                    if (_winReBuildCancelRequested)
                    {
                        buildWasKilled = true;
                        throw new OperationCanceledException("WinRE build cancelled.");
                    }
                    if (timedOut)
                    {
                        buildWasKilled = true;
                        throw new TimeoutException("WinRE build timed out after 60 minutes and was stopped.");
                    }
                    if (exitCode != 0)
                        throw new InvalidOperationException($"build-winre-usb.ps1 exited {exitCode}: {err}{outputText}");
                    lock (_winReBuildStepsLock)
                    {
                        if (_winReBuildGeneration == buildGeneration)
                            _winReBuildStatus = "complete";
                    }
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
                string message = ex.Message;
                if (buildWasKilled)
                {
                    // SF-7: a killed build never runs the script's
                    // finally, which is what re-enables the host PC's
                    // WinRE after the script disabled it to copy the
                    // WIM. Restore it from here, and say plainly in the
                    // status what state WinRE is in.
                    string winReNote = ReenableWinReAfterKilledBuild();
                    message += " " + winReNote;
                    LogWinReBuild($"target={_winReBuildDrive}: build stopped mid-run; {winReNote}");
                }
                // Cap the message: a failed build can dump megabytes of
                // DISM/diskpart output; the tail is where the error is.
                string errorText = TruncateTail(message, 4096);
                // P2-C13: the terminal write happens only NOW — after
                // the killed-build WinRE re-enable above (which can take
                // 30 s) — and only if this build is still the current
                // generation. Writing "failed" before the re-enable let
                // a new build start while this task still ran reagentc
                // against it, and this task's error then stamped over
                // the new build's status.
                lock (_winReBuildStepsLock)
                {
                    if (_winReBuildGeneration == buildGeneration)
                    {
                        _winReBuildStatus = "failed";
                        _winReBuildError = errorText;
                    }
                }
                LogWinReBuild($"target={_winReBuildDrive}: FAILED: {errorText}");
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

    /// <summary>
    /// Runs an already-started process (stdout and stderr redirected) to
    /// completion with a REAL timeout (SF-8): stdout streams line-by-line
    /// to <paramref name="onStdoutLine"/> from a reader task while stderr
    /// drains concurrently, and the wait itself enforces
    /// <paramref name="timeout"/> — on expiry the process tree is killed
    /// and the result reports <c>TimedOut</c>. (Reading stdout to EOF
    /// before calling WaitForExit, the previous shape, only evaluates
    /// the timeout after the process exits on its own, and deadlocks
    /// outright if the child fills the stderr pipe.) After exit or kill
    /// the drain is bounded, so a grandchild holding a pipe open cannot
    /// wedge the caller either. The exit code is -1 when timed out.
    /// </summary>
    public static (bool TimedOut, int ExitCode, string StdErr, string StdOut)
        RunBuildProcess(System.Diagnostics.Process proc, TimeSpan timeout,
            Action<string> onStdoutLine)
    {
        var stdout = new System.Text.StringBuilder();
        var stdoutTask = System.Threading.Tasks.Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                lock (stdout) stdout.AppendLine(line);
                try { onStdoutLine(line); } catch { /* progress is best-effort */ }
            }
        });
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            System.Threading.Tasks.Task.WaitAll(
                new System.Threading.Tasks.Task[] { stdoutTask, stderrTask },
                TimeSpan.FromSeconds(10));
            string partialErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            lock (stdout) return (true, -1, partialErr, stdout.ToString());
        }
        System.Threading.Tasks.Task.WaitAll(
            new System.Threading.Tasks.Task[] { stdoutTask, stderrTask },
            TimeSpan.FromSeconds(30));
        string err = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        lock (stdout) return (false, proc.ExitCode, err, stdout.ToString());
    }

    /// <summary>
    /// SF-7: best-effort <c>reagentc /enable</c> after a WinRE build was
    /// killed. The build script disables the host PC's WinRE early (to
    /// copy its WIM) and re-enables it in a <c>finally</c> — which never
    /// runs when the process tree is killed, leaving the PC's recovery
    /// environment silently off. Returns a sentence for the build status
    /// and log stating what state WinRE is in; when the re-enable did
    /// not succeed it gives the exact manual restore command. Never
    /// throws.
    /// </summary>
    public static string ReenableWinReAfterKilledBuild()
    {
        const string manual = "run `reagentc /enable` from an elevated prompt to restore it";
        try
        {
            if (!OperatingSystem.IsWindows())
                return "This PC's Windows Recovery Environment may still be disabled " +
                       $"(the stopped build had turned it off) — {manual}.";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "reagentc.exe",
                Arguments = "/enable",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var (exitCode, output) = RunToolCapture(psi, 30000);
            if (exitCode == 0)
                return "Windows Recovery Environment was re-enabled on this PC.";
            return "Windows Recovery Environment could not be re-enabled automatically " +
                   $"(reagentc exited {exitCode}: {output.Trim()}) — {manual}.";
        }
        catch (Exception ex)
        {
            return "Windows Recovery Environment could not be re-enabled automatically " +
                   $"({ex.Message}) — {manual}.";
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
