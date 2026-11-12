using FileKeep.Core.Nntp;
using Xunit;

namespace FileKeep.Core.Tests.Nntp;

/// <summary>
/// Verifies UsenetUploadTracker merge-on-save and its corrupt-file
/// handling (W-1): a re-read failure in Save() must never overwrite
/// the on-disk file with only this instance's records.
/// </summary>
public sealed class UsenetUploadTrackerTests : IDisposable
{
    private const string Newsgroup = "alt.binaries.test";

    private readonly string _workDir;

    public UsenetUploadTrackerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "ub-tracker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void Save_CorruptOnDiskFile_ThrowsAndPreservesTornCopy()
    {
        // W-1: the on-disk file turned corrupt between Load() and
        // Save(). The old code silently overwrote it with only this
        // instance's records. Now: the torn file is preserved with a
        // .corrupt- suffix and Save() throws instead of overwriting.
        var tracker = new UsenetUploadTracker(_workDir);
        tracker.RecordUpload("backup-one", "news.example.com", Newsgroup);
        string path = Path.Combine(_workDir, "usenet-uploads.json");
        Assert.True(File.Exists(path));
        File.WriteAllText(path, "{ this is not json");

        var ex = Assert.Throws<InvalidDataException>(() =>
            tracker.RecordUpload("backup-two", "news.example.com", Newsgroup));
        Assert.Contains("corrupt", ex.Message);
        Assert.False(File.Exists(path));
        string[] preserved = Directory.GetFiles(_workDir, "usenet-uploads.json.corrupt-*");
        Assert.Single(preserved);
        Assert.Equal("{ this is not json", File.ReadAllText(preserved[0]));
    }

    [Fact]
    public void Save_MergesOtherInstanceRecords()
    {
        // Merge-on-save still works: a second instance's records
        // survive a save from the first instance.
        var first = new UsenetUploadTracker(_workDir);
        first.RecordUpload("backup-one", "news.example.com", Newsgroup);
        var second = new UsenetUploadTracker(_workDir);
        second.RecordUpload("backup-two", "news.example.com", Newsgroup);
        first.RecordUpload("backup-three", "news.example.com", Newsgroup);

        var reloaded = new UsenetUploadTracker(_workDir);
        Assert.Equal(3, reloaded.GetAll().Count);
    }
}
