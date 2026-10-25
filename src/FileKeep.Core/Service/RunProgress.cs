using System.Text.Json.Serialization;

namespace FileKeep.Core.Service;

/// <summary>
/// One destination's live progress within a running backup, for the
/// dashboard API. Named record (not anonymous type) so trimmed
/// publishing works. TotalUnits is -1 while the destination's total
/// is not known yet (batched runs learn it as batches flush);
/// Percent is -1 then too.
/// </summary>
public sealed record DestinationProgressSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("doneUnits")] long DoneUnits,
    [property: JsonPropertyName("totalUnits")] long TotalUnits,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("failed")] bool Failed,
    [property: JsonPropertyName("failureNote")] string? FailureNote);

/// <summary>
/// Snapshot of the currently running backup for the dashboard API:
/// the phase, real counts, per-destination progress, and a worst-case
/// TOTAL duration estimate ("No more than XX minutes total" — the
/// user's wording: a bound on the whole run, not a remaining-time
/// guess). EstimateText/DoneByText are null until at least one
/// destination's remaining work is measurable.
/// </summary>
public sealed record RunProgressSnapshot(
    [property: JsonPropertyName("jobName")] string JobName,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("filesScanned")] int FilesScanned,
    [property: JsonPropertyName("doneUnits")] long DoneUnits,
    [property: JsonPropertyName("totalUnits")] long TotalUnits,
    [property: JsonPropertyName("headlineUnit")] string HeadlineUnit,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("estimateText")] string? EstimateText,
    [property: JsonPropertyName("doneByText")] string? DoneByText,
    [property: JsonPropertyName("elapsedSecs")] int ElapsedSecs,
    [property: JsonPropertyName("destinations")] List<DestinationProgressSnapshot> Destinations);

/// <summary>Phase names published in <see cref="RunProgressSnapshot.Phase"/>.</summary>
public static class RunPhases
{
    public const string Preparing = "preparing";
    public const string Uploading = "uploading";
    public const string Verifying = "verifying";
    public const string Finishing = "finishing";
}

/// <summary>
/// The worst-case estimate model (per the approved job-card mockup):
/// for each still-active destination, remaining bytes are priced at
/// the SLOWER of the destination kind's floor rate and the rate
/// actually observed this run, then DOUBLED — one full retry of every
/// batch is budgeted. The run's remaining time is the slowest
/// destination's figure (destinations are fed sequentially per batch,
/// and a failed destination drops out, which is why the estimate is
/// rebuilt when one fails). When the run still owes an auto-verify,
/// a read-back allowance at a conservative local-disk floor is added.
/// The displayed figure is a TOTAL: elapsed + remaining, rounded UP
/// to whole minutes — "No more than XX minutes total".
/// </summary>
public static class WorstCaseEstimate
{
    // Conservative sustained-rate floors (bytes/sec) per destination
    // kind. Deliberately pessimistic: a healthy destination beats its
    // floor easily, which is what makes the total a bound, not a guess.
    public static long FloorRate(string kind) => kind switch
    {
        "nntp" => 1L * 1024 * 1024,   // 1 MiB/s — a bad Usenet day
        "smb" => 5L * 1024 * 1024,    // 5 MiB/s — weak Wi-Fi to a NAS
        "usb" => 4L * 1024 * 1024,    // 4 MiB/s — a slow USB stick
        _ => 1L * 1024 * 1024,
    };

    /// <summary>Read-back floor for the auto-verify allowance.</summary>
    public const long VerifyFloorRate = 50L * 1024 * 1024;

    /// <summary>Fallback unit sizes when a run has not moved bytes yet.</summary>
    public static long DefaultUnitBytes(string unit) => unit switch
    {
        "chunks" => 64 * 1024,        // CDC target chunk size
        "files" => 64 * 1024,
        _ => 1024 * 1024,             // "items" (volumes etc.)
    };

    /// <summary>One destination's numbers, as the tracker knows them.</summary>
    public sealed record DestinationInput(
        string Kind, string Unit, long DoneUnits, long TotalUnits,
        long BytesDone, double? ObservedBytesPerSec, bool Failed);

    /// <summary>
    /// Worst-case seconds REMAINING for the run, or null when no
    /// active destination has a known total yet. Pure: unit-tested.
    /// </summary>
    public static double? RemainingWorstCaseSeconds(
        IReadOnlyList<DestinationInput> destinations,
        bool expectVerify, long verifyBytes)
    {
        double worst = 0;
        bool any = false;
        foreach (var d in destinations)
        {
            if (d.Failed || d.TotalUnits < 0)
                continue;
            long remainingUnits = d.TotalUnits - d.DoneUnits;
            if (remainingUnits <= 0)
            {
                any = true; // a finished destination still bounds nothing
                continue;
            }
            double avgUnitBytes = d.DoneUnits > 0 && d.BytesDone > 0
                ? (double)d.BytesDone / d.DoneUnits
                : DefaultUnitBytes(d.Unit);
            double remainingBytes = remainingUnits * avgUnitBytes;
            double floor = FloorRate(d.Kind);
            double rate = d.ObservedBytesPerSec is > 0
                ? Math.Min(floor, d.ObservedBytesPerSec.Value)
                : floor;
            double seconds = remainingBytes / rate * 2.0; // one retry per batch
            if (seconds > worst)
                worst = seconds;
            any = true;
        }
        if (!any)
            return null;
        if (expectVerify && verifyBytes > 0)
            worst += (double)verifyBytes / VerifyFloorRate;
        return worst;
    }

    /// <summary>
    /// The user-facing total-duration bound. Null when the remaining
    /// time is not measurable yet.
    /// </summary>
    public static string? FormatTotal(double elapsedSeconds, double? remainingWorstCaseSeconds)
    {
        if (remainingWorstCaseSeconds is null)
            return null;
        double total = elapsedSeconds + remainingWorstCaseSeconds.Value;
        long minutes = Math.Max(1, (long)Math.Ceiling(total / 60.0));
        if (minutes < 60)
            return minutes == 1
                ? "No more than 1 minute total"
                : $"No more than {minutes} minutes total";
        long hours = minutes / 60;
        long rest = minutes % 60;
        string hourPart = hours == 1 ? "1 hour" : $"{hours} hours";
        string restPart = rest == 1 ? "1 minute" : $"{rest} minutes";
        return rest == 0
            ? $"No more than {hourPart} total"
            : $"No more than {hourPart} {restPart} total";
    }
}

/// <summary>
/// Thread-safe live progress for the one backup run in flight (runs
/// are serialized by the scheduler's operation lock, so a single
/// tracker suffices). The scheduler's run paths publish into it; the
/// dashboard reads <see cref="Snapshot"/> on request threads.
/// </summary>
public sealed class RunProgressTracker
{
    private sealed class Destination
    {
        public required string Name;
        public required string Kind;
        public required string Unit;
        public long DoneUnits;
        public long TotalUnits = -1;
        public long BytesDone;
        public double? ObservedBytesPerSec;
        public bool Failed;
        public string? FailureNote;
    }

    private readonly object _lock = new();
    private readonly List<Destination> _destinations = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private string _phase = RunPhases.Preparing;
    private int _filesScanned;
    private bool _expectVerify;

    public RunProgressTracker(string jobName) => JobName = jobName;

    public string JobName { get; }

    public void SetPhase(string phase)
    {
        lock (_lock) _phase = phase;
    }

    public void AddFilesScanned(int count)
    {
        lock (_lock) _filesScanned += count;
    }

    /// <summary>True once the run owes an auto-verify at the end
    /// (non-batched runs); feeds the estimate's verify allowance.</summary>
    public void SetExpectVerify(bool expect)
    {
        lock (_lock) _expectVerify = expect;
    }

    public void RegisterDestination(string name, string kind, string unit)
    {
        lock (_lock)
        {
            if (_destinations.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return;
            _destinations.Add(new Destination { Name = name, Kind = kind, Unit = unit });
        }
    }

    private Destination? Find(string name)
        => _destinations.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void SetDestinationTotal(string name, long totalUnits)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is not null && totalUnits > d.TotalUnits)
                d.TotalUnits = totalUnits;
        }
    }

    public void AddDestinationDone(string name, long units, long bytes = 0)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is null) return;
            d.DoneUnits += units;
            d.BytesDone += bytes;
            if (d.TotalUnits >= 0 && d.DoneUnits > d.TotalUnits)
                d.TotalUnits = d.DoneUnits;
        }
    }

    public void SetDestinationDone(string name, long doneUnits)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is null) return;
            if (doneUnits > d.DoneUnits)
                d.DoneUnits = doneUnits;
            if (d.TotalUnits >= 0 && d.DoneUnits > d.TotalUnits)
                d.TotalUnits = d.DoneUnits;
        }
    }

    /// <summary>Marks a destination fully delivered (its bar sits at
    /// its total) — the non-batched paths call this when a target's
    /// transfer returns successfully.</summary>
    public void CompleteDestination(string name)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is not null && d.TotalUnits >= 0)
                d.DoneUnits = d.TotalUnits;
        }
    }

    public void SetDestinationObservedRate(string name, double bytesPerSec)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is not null && bytesPerSec > 0)
                d.ObservedBytesPerSec = bytesPerSec;
        }
    }

    public void FailDestination(string name, string? note)
    {
        lock (_lock)
        {
            var d = Find(name);
            if (d is null) return;
            d.Failed = true;
            d.FailureNote = string.IsNullOrWhiteSpace(note)
                ? null
                : note.Length > 160 ? note.Substring(0, 157) + "…" : note;
        }
    }

    public RunProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            var elapsed = DateTime.UtcNow - _startedUtc;
            var dests = _destinations.Select(d => new DestinationProgressSnapshot(
                d.Name, d.Kind, d.Unit, d.DoneUnits, d.TotalUnits,
                d.TotalUnits > 0 ? Math.Round(100.0 * d.DoneUnits / d.TotalUnits, 1) : -1,
                d.Failed, d.FailureNote)).ToList();

            // Headline counts: chunk-based destinations when the run
            // has them (the common case — batched runs count chunks at
            // every destination); otherwise the first destination with
            // a known total; otherwise nothing measurable yet.
            var headlinePool = _destinations.Where(d => d.Unit == "chunks").ToList();
            if (headlinePool.Count == 0)
                headlinePool = _destinations.Where(d => d.TotalUnits >= 0).Take(1).ToList();
            long done = headlinePool.Sum(d => d.DoneUnits);
            long total = headlinePool.Count > 0 && headlinePool.All(d => d.TotalUnits >= 0)
                ? headlinePool.Sum(d => d.TotalUnits) : -1;
            // Several chunk destinations mirror the SAME chunks; the
            // headline total is one destination's plan, not the sum.
            if (headlinePool.Count > 1 && headlinePool.All(d => d.Unit == "chunks"))
            {
                total = headlinePool.Max(d => d.TotalUnits);
                done = headlinePool.Min(d => d.DoneUnits);
            }
            double percent = total > 0 ? Math.Round(100.0 * done / total, 1) : -1;
            string headlineUnit = headlinePool.Count > 0 ? headlinePool[0].Unit : "chunks";

            // Estimate inputs from the destinations (failed ones are
            // excluded inside the model — the estimate rebuilds around
            // whoever is left, per the mockup's degraded scenario).
            var inputs = _destinations.Select(d => new WorstCaseEstimate.DestinationInput(
                d.Kind, d.Unit, d.DoneUnits, d.TotalUnits, d.BytesDone,
                d.ObservedBytesPerSec, d.Failed)).ToList();
            // The verify pass reads back ONE copy of the data — the
            // largest destination's plan, priced at its unit size.
            long verifyBytes = 0;
            foreach (var d in _destinations)
            {
                if (d.TotalUnits < 0) continue;
                double avg = d.DoneUnits > 0 && d.BytesDone > 0
                    ? (double)d.BytesDone / d.DoneUnits
                    : WorstCaseEstimate.DefaultUnitBytes(d.Unit);
                verifyBytes = Math.Max(verifyBytes, (long)(d.TotalUnits * avg));
            }
            // The allowance only counts while the run is still ahead
            // of the verify pass.
            bool verifyPending = _expectVerify &&
                (_phase == RunPhases.Preparing || _phase == RunPhases.Uploading);
            double? remaining = WorstCaseEstimate.RemainingWorstCaseSeconds(
                inputs, verifyPending, verifyPending ? verifyBytes : 0);
            string? estimate = WorstCaseEstimate.FormatTotal(elapsed.TotalSeconds, remaining);
            string? doneBy = remaining is null
                ? null
                : DateTime.Now.AddSeconds(remaining.Value)
                    .ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture);

            return new RunProgressSnapshot(
                JobName, _phase, _filesScanned, done, total, headlineUnit, percent,
                _destinations.Any(d => d.Failed) && _destinations.Any(d => !d.Failed),
                estimate, doneBy, (int)elapsed.TotalSeconds, dests);
        }
    }
}
