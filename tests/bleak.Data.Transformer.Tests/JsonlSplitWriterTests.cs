using System.Text;
using bleak.Data.Transformer.Output;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class JsonlSplitWriterTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jsonltest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private static void Write(JsonlSplitWriter w, string line) => w.WriteLine(Encoding.UTF8.GetBytes(line));

    [TestMethod]
    public void Single_file_has_no_ordinal_suffix()
    {
        string[] finalPaths;
        using (var w = new JsonlSplitWriter(_dir, "userprops", "20260616_120000", recordsPerFile: 10))
        {
            Write(w, "{\"a\":1}");
            Write(w, "{\"a\":2}");
            w.Dispose();
            finalPaths = w.FinalPaths.ToArray();
        }

        Assert.AreEqual(1, finalPaths.Length);
        Assert.AreEqual(Path.Combine(_dir, "userprops_20260616_120000.jsonl"), finalPaths[0]);
        var lines = File.ReadAllLines(finalPaths[0]);
        CollectionAssert.AreEqual(new[] { "{\"a\":1}", "{\"a\":2}" }, lines);
    }

    [TestMethod]
    public void Splits_into_ordinal_named_files_when_exceeding_records_per_file()
    {
        string[] finalPaths;
        using (var w = new JsonlSplitWriter(_dir, "contacts", "20260616_120000", recordsPerFile: 2))
        {
            for (var i = 0; i < 5; i++)
            {
                Write(w, $"{{\"i\":{i}}}");
            }
            w.Dispose();
            finalPaths = w.FinalPaths.ToArray();
        }

        Assert.AreEqual(3, finalPaths.Length); // 2 + 2 + 1
        Assert.AreEqual(Path.Combine(_dir, "contacts_20260616_120000_1.jsonl"), finalPaths[0]);
        Assert.AreEqual(Path.Combine(_dir, "contacts_20260616_120000_2.jsonl"), finalPaths[1]);
        Assert.AreEqual(Path.Combine(_dir, "contacts_20260616_120000_3.jsonl"), finalPaths[2]);

        Assert.AreEqual(2, File.ReadAllLines(finalPaths[0]).Length);
        Assert.AreEqual(2, File.ReadAllLines(finalPaths[1]).Length);
        Assert.AreEqual(1, File.ReadAllLines(finalPaths[2]).Length);
    }

    [TestMethod]
    public void Tracks_total_records_written()
    {
        using var w = new JsonlSplitWriter(_dir, "x", "ts", recordsPerFile: 2);
        Write(w, "{}");
        Write(w, "{}");
        Write(w, "{}");
        Assert.AreEqual(3L, w.RecordsWritten);
    }
}
