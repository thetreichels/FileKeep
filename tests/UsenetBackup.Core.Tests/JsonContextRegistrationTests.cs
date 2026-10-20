using UsenetBackup.Core.Json;
using UsenetBackup.Service;
using Xunit;

namespace UsenetBackup.Core.Tests;

/// <summary>
/// Guards the trim-published dashboard: every request-body type read via
/// ReadFromJsonAsync in Dashboard.cs must have source-generated metadata in
/// one of the contexts on the HttpJsonOptions resolver chain. A missing type
/// makes the trimmed single-file build throw NotSupportedException, which
/// the handlers surface as "Invalid JSON." — that regression broke the
/// passphrase save, authenticator verify, backup/restore operations and
/// location/provider saves in 0.08.13 (found in real-world testing,
/// 2026-10-19).
/// </summary>
public class JsonContextRegistrationTests
{
    [Fact]
    public void DashboardBodyTypes_HaveSourceGenMetadata()
    {
        Assert.NotNull(DashboardJsonContext.Default.JsonElement);
        Assert.NotNull(FileKeepJsonContext.Default.BackupJobConfig);
        Assert.NotNull(FileKeepJsonContext.Default.BackupLocation);
        Assert.NotNull(FileKeepJsonContext.Default.NntpConfig);
        Assert.NotNull(FileKeepJsonContext.Default.ListNntpConfig);
    }
}
