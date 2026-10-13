using System.IO.Compression;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Sources;

namespace bleak.Data.Transformer.Tests;

/// <summary>
/// Covers the portable ".gz" (and plain-passthrough) paths of EncryptedSourceCache via the public
/// SourceReaderFactory entry point — pure .NET, no external dependency. The PGP path shells out to the
/// system "gpg" binary and needs a real private key in the local keyring, so it isn't covered here; it was
/// validated by hand against real encrypted `*.csv.gz.pgp` extracts instead.
/// </summary>
[TestClass]
public class EncryptedSourceCacheTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "encsrctest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteGzip(string path, string content)
    {
        using var fileStream = File.Create(path);
        using var gzip = new GZipStream(fileStream, CompressionMode.Compress);
        using var writer = new StreamWriter(gzip);
        writer.Write(content);
    }

    [TestMethod]
    public void PlainCsv_PassesThroughUnchanged()
    {
        var path = Path.Combine(_dir, "plain.csv");
        File.WriteAllText(path, "a,b\n1,2\n");

        using var reader = SourceReaderFactory.Open(new SourceConfig { File = path }, _dir);
        var rows = reader.ReadRows().ToList();

        CollectionAssert.AreEqual(new[] { "a", "b" }, reader.Headers.ToList());
        Assert.AreEqual(1, rows.Count);
        CollectionAssert.AreEqual(new[] { "1", "2" }, rows[0]);
    }

    [TestMethod]
    public void GzippedCsv_IsTransparentlyDecompressed()
    {
        var path = Path.Combine(_dir, "data.csv.gz");
        WriteGzip(path, "a,b\n1,2\n3,4\n");

        using var reader = SourceReaderFactory.Open(new SourceConfig { File = path }, _dir);
        var rows = reader.ReadRows().ToList();

        CollectionAssert.AreEqual(new[] { "a", "b" }, reader.Headers.ToList());
        Assert.AreEqual(2, rows.Count);
        CollectionAssert.AreEqual(new[] { "1", "2" }, rows[0]);
        CollectionAssert.AreEqual(new[] { "3", "4" }, rows[1]);
    }

    [TestMethod]
    public void GzippedCsv_MaterializesIntoADurableSiblingSubfolder()
    {
        // Durable and discoverable on purpose: "<file name>_decrypted/<plain name>", next to the source,
        // not a throwaway temp file — so the user can browse it afterward.
        var path = Path.Combine(_dir, "data.csv.gz");
        WriteGzip(path, "a,b\n1,2\n");

        using var reader = SourceReaderFactory.Open(new SourceConfig { File = path }, _dir);
        Assert.AreEqual(1, reader.ReadRows().Count());

        var expectedPlainPath = Path.Combine(_dir, "data.csv.gz_decrypted", "data.csv");
        Assert.IsTrue(File.Exists(expectedPlainPath), $"expected a durable plain copy at {expectedPlainPath}");
        Assert.AreEqual("a,b\n1,2\n", File.ReadAllText(expectedPlainPath).Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void GzippedCsv_OpenedTwice_ReusesTheCachedPlainFile()
    {
        // A source is typically opened many times within one run; the second open must not choke on
        // (or re-decompress from) whatever the first open already materialized.
        var path = Path.Combine(_dir, "data.csv.gz");
        WriteGzip(path, "a,b\n1,2\n");

        using (var first = SourceReaderFactory.Open(new SourceConfig { File = path }, _dir))
        {
            Assert.AreEqual(1, first.ReadRows().Count());
        }
        using (var second = SourceReaderFactory.Open(new SourceConfig { File = path }, _dir))
        {
            Assert.AreEqual(1, second.ReadRows().Count());
        }
    }
}
