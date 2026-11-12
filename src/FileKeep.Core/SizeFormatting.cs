namespace FileKeep.Core;

/// <summary>
/// Unit style for human-readable byte sizes (audit §3.2 C1).
/// The styles are DELIBERATELY different: the decimal-style labels vs
/// binary labels are existing user-visible output, not a mistake to "fix".
/// Each style reproduces its original call site's output byte-for-byte;
/// pick the style that matches the call site, never re-label.
/// </summary>
public enum SizeUnitStyle
{
    /// <summary>
    /// Binary IEC labels: "1.5 GiB" / "2.0 MiB" / "3.0 KiB" / "42 B"
    /// (F1 above bytes). Was <c>SmbSync.FormatBytes</c>.
    /// </summary>
    BinaryIec,

    /// <summary>
    /// Decimal-style labels, GB/MB only: "1.5 GB" (F1) / "512 MB" (F0).
    /// The divisors stay 1024-based, exactly as the original site —
    /// the labels are decimal-style but the math is binary; do not "fix"
    /// either half. Was <c>PhysicalDriveInfo.FormatSize</c>.
    /// </summary>
    DecimalLabelsGbMb,

    /// <summary>
    /// Throughput: "1.5 MB/s" (F1) or "512 KB/s" (F0). Only meaningful
    /// for the <c>double</c> overload. Was the inline rate in
    /// <c>UploadProgressTracker</c>.
    /// </summary>
    RateDecimalLabels,
}

/// <summary>
/// One size formatter for the whole tree (audit §3.2 C1), with an
/// explicit <see cref="SizeUnitStyle"/> so no call site silently
/// re-labels its output. The two sites NOT migrated here keep their own
/// formatters deliberately: <c>BackupRepository.FormatBytes</c>
/// (B/KB/MB/GB/TB, <c>0.#</c>) and
/// <c>BackupScheduler.FormatBatchSize</c> (GiB/MiB/KiB plus the
/// "whole backup" sentinel) — both files were dirty when this was
/// written, second pass.
/// </summary>
public static class SizeFormatting
{
    public static string Format(long bytes, SizeUnitStyle style) => style switch
    {
        SizeUnitStyle.BinaryIec => FormatBinaryIec(bytes),
        SizeUnitStyle.DecimalLabelsGbMb => FormatDecimalLabelsGbMb(bytes),
        SizeUnitStyle.RateDecimalLabels => throw new ArgumentOutOfRangeException(
            nameof(style), style, "A rate needs a bytes-per-second value; use the double overload."),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    public static string Format(ulong bytes, SizeUnitStyle style) => style switch
    {
        SizeUnitStyle.BinaryIec => FormatBinaryIec(bytes),
        SizeUnitStyle.DecimalLabelsGbMb => FormatDecimalLabelsGbMb(bytes),
        SizeUnitStyle.RateDecimalLabels => throw new ArgumentOutOfRangeException(
            nameof(style), style, "A rate needs a bytes-per-second value; use the double overload."),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };

    /// <summary>Throughput formatting; only <see cref="SizeUnitStyle.RateDecimalLabels"/> is supported.</summary>
    public static string Format(double bytesPerSecond, SizeUnitStyle style) => style switch
    {
        SizeUnitStyle.RateDecimalLabels => bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / (1024 * 1024):F1} MB/s"
            : $"{bytesPerSecond / 1024:F0} KB/s",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Only RateDecimalLabels applies to a rate."),
    };

    // Byte-identical to the former SmbSync.FormatBytes.
    private static string FormatBinaryIec(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KiB";
        return $"{bytes} B";
    }

    // Same ladder in ulong arithmetic (drive sizes are ulong).
    private static string FormatBinaryIec(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F1} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KiB";
        return $"{bytes} B";
    }

    // Byte-identical to the former PhysicalDriveInfo.FormatSize.
    private static string FormatDecimalLabelsGbMb(long bytes)
    {
        const long GB = 1024L * 1024 * 1024;
        if (bytes >= GB)
            return $"{bytes / (double)GB:F1} GB";
        return $"{bytes / (1024.0 * 1024):F0} MB";
    }

    // Same in ulong arithmetic.
    private static string FormatDecimalLabelsGbMb(ulong bytes)
    {
        const ulong GB = 1024UL * 1024 * 1024;
        if (bytes >= GB)
            return $"{bytes / (double)GB:F1} GB";
        return $"{bytes / (1024.0 * 1024):F0} MB";
    }
}
