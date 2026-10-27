// Regression test for the 2026-10-27 laptop GUI failure: a native-picker
// upload stored its chunk on the USB drive but the catalog never gained
// the file, because OfflineFoldersCatalog.Save serialized a JsonArray of
// chunk-ID strings, which throws under trimming ("JsonTypeInfo metadata
// for type 'System.String' was not provided"). The sandbox suite missed
// it because untrimmed JsonNode serialization works; the fix writes the
// catalog plaintext with Utf8JsonWriter instead. This test pins the
// roundtrip (save with chunks, load back identical); the trim behavior
// itself was verified with a trimmed publish, not this test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

public class OfflineFoldersCatalogSaveTests
{
    [Fact]
    public void SaveWithChunkedFileRoundtrips()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var path = Path.Combine(Path.GetTempPath(), "fk-catalog-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var entries = new List<OfflineFolderEntry>
            {
                new OfflineFolderEntry("Test", "Test USB", DateTimeOffset.UtcNow)
                {
                    Files = new List<OfflineFileEntry>
                    {
                        new OfflineFileEntry("stealth-test.txt", 38,
                            new DateTimeOffset(2026, 10, 26, 15, 37, 0, TimeSpan.Zero),
                            new List<string> { "8e3d02cfcda2b3d52f94afbf3a7bde653e18e50af6c2eb9241e128491f690762" })
                    }
                }
            };
            OfflineFoldersCatalog.Save(path, key, entries);
            var loaded = OfflineFoldersCatalog.Load(path, key);
            Assert.Single(loaded);
            Assert.Equal("Test", loaded[0].Name);
            Assert.Single(loaded[0].Files);
            Assert.Equal("stealth-test.txt", loaded[0].Files[0].Path);
            Assert.Equal(38, loaded[0].Files[0].Size);
            Assert.Single(loaded[0].Files[0].ChunkIds);
            Assert.Equal("8e3d02cfcda2b3d52f94afbf3a7bde653e18e50af6c2eb9241e128491f690762",
                loaded[0].Files[0].ChunkIds[0]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
