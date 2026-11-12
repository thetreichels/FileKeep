namespace FileKeep.Core.Redundancy;

/// <summary>
/// Kind-keyed parity dispatch (audit D5): the single place that maps a
/// <see cref="RedundancyKind"/> to the XOR / PAR2 implementation. Upload
/// sites (<see cref="Nntp.NntpBatchUploader"/> and the scheduler's NNTP
/// leg) call these four methods instead of repeating the same four
/// ternaries; the download side keeps its own XOR-then-PAR2 order in
/// <c>BackupRepository.TryReconstruct</c>, which is a different shape
/// (reconstruction, not generation) and does not go through here.
/// </summary>
public static class Parity
{
    /// <summary>
    /// Generates parity blocks for <paramref name="ids"/> under
    /// <paramref name="kind"/>: one XOR block per group, or three PAR2
    /// shards per group. <see cref="RedundancyKind.None"/> (and any
    /// unknown kind) yields no blocks. <paramref name="xorGroupSize"/>
    /// applies to XOR only; PAR2 groups are fixed at
    /// <see cref="Par2Redundancy.DataShards"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> GenerateParity(
        RedundancyKind kind,
        IReadOnlyList<string> ids,
        Func<string, byte[]> getBytes,
        int xorGroupSize = XorParity.DefaultGroupSize)
        => kind switch
        {
            RedundancyKind.Xor => XorParity.GenerateParity(ids, getBytes, null, xorGroupSize),
            RedundancyKind.Par2 => Par2Redundancy.GenerateParity(ids, getBytes),
            _ => new Dictionary<string, byte[]>(),
        };

    /// <summary>
    /// The parity groups formed over <paramref name="ids"/> under
    /// <paramref name="kind"/>: consecutive slices of
    /// <paramref name="xorGroupSize"/> for XOR, of
    /// <see cref="Par2Redundancy.DataShards"/> for PAR2; empty for
    /// <see cref="RedundancyKind.None"/>.
    /// </summary>
    public static List<List<string>> GroupsFor(
        RedundancyKind kind,
        IReadOnlyList<string> ids,
        int xorGroupSize = XorParity.DefaultGroupSize)
        => kind switch
        {
            RedundancyKind.Xor => XorParity.GroupsFor(ids, xorGroupSize),
            RedundancyKind.Par2 => Par2Redundancy.GroupsFor(ids),
            _ => new List<List<string>>(),
        };

    /// <summary>Parity units produced per data group: 1 for XOR, 3 for PAR2, 0 for none.</summary>
    public static int ParityUnitsPerGroup(RedundancyKind kind)
        => kind switch
        {
            RedundancyKind.Xor => 1,
            RedundancyKind.Par2 => Par2Redundancy.ParityShards,
            _ => 0,
        };

    /// <summary>Short label for logs: "XOR", "PAR2", or "none".</summary>
    public static string Label(RedundancyKind kind)
        => kind switch
        {
            RedundancyKind.Xor => "XOR",
            RedundancyKind.Par2 => "PAR2",
            _ => "none",
        };

    /// <summary>
    /// Lenient parse of a redundancy mode string: unknown, empty, or null
    /// input maps to <see cref="RedundancyKind.None"/> instead of throwing.
    /// For <see cref="Nntp.NntpBatchUploader"/> only: its redundancy string
    /// is a public-constructor argument and does not always arrive from
    /// validated config — <c>BackupScheduler</c> never calls
    /// <c>ServiceConfig.Validate</c> (validation happens only on load/save
    /// via <c>ServiceConfigPersistence</c> / <c>ServiceConfigBuilder</c>),
    /// so an unvalidated job/location/provider string can reach the
    /// uploader. Today's behavior for an unknown mode there is silent
    /// no-parity, and this preserves it. Validated paths (the scheduler's
    /// own NNTP leg) keep the throwing
    /// <see cref="RedundancyKindParser.Parse"/> instead.
    /// </summary>
    public static RedundancyKind FromString(string? value)
    {
        try
        {
            return RedundancyKindParser.Parse(value);
        }
        catch (ArgumentException)
        {
            return RedundancyKind.None;
        }
    }
}
