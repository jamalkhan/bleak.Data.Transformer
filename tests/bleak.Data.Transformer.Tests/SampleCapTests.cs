using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Tests;

/// <summary>
/// Covers the parent-scoped sample (<c>run.sample</c>): the first N primary rows plus ALL of their
/// descendants at every depth, and the <c>run.maxRecords</c> output cap.
/// </summary>
[TestClass]
public class SampleCapTests
{
    private string _dir = "";
    private string _out = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "etlsample_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_out);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    /// <summary>
    /// corp: 4 customers. brand: customer 1 has 5 rows — deliberately placed so that customer 1's later
    /// brand rows sit BELOW customer 4's, i.e. a naive per-source row cap would truncate them.
    /// loyalty: a grandchild joined on loyalty_id (not customer_key) that still carries customer_key.
    /// </summary>
    private void WriteSources()
    {
        WriteFile("corp.csv", "customer_key,name\n1,A\n2,B\n3,C\n4,D\n");
        WriteFile("brand.csv",
            "customer_key,brand,loyalty_id\n" +
            "1,BrandA,L1\n" +
            "2,B4,L2\n" +
            "4,B2,L4\n" +      // customer 4 (outside a 3-parent sample) appears early
            "1,BrandB,L1\n" + // ...and customer 1's remaining rows come after it
            "1,B2,L1\n" +
            "1,B4,L1\n" +
            "1,B3,L1\n" +
            "3,BrandA,L3\n");
        WriteFile("loyalty.csv",
            "customer_key,loyalty_id,tier\n" +
            "4,L4,Gold\n" +
            "1,L1,Silver\n" +
            "2,L2,Bronze\n" +
            "3,L3,Gold\n");
    }

    private const string Config = """
    {
      "run": { "joinStrategy": "<STRATEGY>"<RUNEXTRA> },
      "sources": {
        "corp":    { "file": "corp.csv",    "fields": { "customer_key": { "type": "integer" } } },
        "brand":   { "file": "brand.csv",   "fields": { "customer_key": { "type": "integer" } } },
        "loyalty": { "file": "loyalty.csv", "fields": { "customer_key": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "userprops", "mode": "object", "source": "corp",
          "fields": [
            { "name": "customer_key", "from": "customer_key", "type": "integer" },
            { "name": "dob_lookup", "type": "string",
              "lookup": { "source": "brand", "on": { "parent": "customer_key", "child": "customer_key" },
                          "column": "brand", "single": true } }
          ],
          "children": [
            { "source": "brand", "as": "brand_profile", "cardinality": "many",
              "on": { "parent": "customer_key", "child": "customer_key" },
              "fields": [ { "name": "brand", "from": "brand", "type": "string" } ],
              "children": [
                { "source": "loyalty", "as": "loyalty", "cardinality": "many",
                  "on": { "parent": "loyalty_id", "child": "loyalty_id" },
                  "fields": [ { "name": "tier", "from": "tier", "type": "string" } ] }
              ] }
          ],
          "load": { "fileBaseName": "userprops", "splitEvery": <SPLIT> } }
      ]
    }
    """;

    private static readonly string Sample3 =
        ", \"sample\": { \"primaryRows\": 3, \"keyColumn\": \"customer_key\" }";

    private static string KeyFilter(params string[] values) =>
        $", \"keyFilter\": {{ \"keyColumn\": \"customer_key\", \"values\": [{string.Join(",", values.Select(v => $"\"{v}\""))}] }}";

    private EtlOutputResult Run(string strategy, string runExtra, int split = 1000000)
    {
        var json = Config
            .Replace("<STRATEGY>", strategy)
            .Replace("<RUNEXTRA>", runExtra)
            .Replace("<SPLIT>", split.ToString());
        return EtlEngine.Run(EtlConfigLoader.FromJson(json), _dir, _out, "ts")[0];
    }

    private static Dictionary<long, JsonElement> ByKey(EtlOutputResult result)
        => result.Files
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToDictionary(r => r.GetProperty("customer_key").GetInt64());

    private static List<string> Brands(JsonElement record)
        => record.GetProperty("brand_profile").EnumerateArray()
            .Select(b => b.GetProperty("brand").GetString()!)
            .ToList();

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void Sample_KeepsAllChildRowsForTheSampledParents(string strategy)
    {
        WriteSources();

        var byKey = ByKey(Run(strategy, Sample3));

        // 3 primary rows -> customers 1..3; customer 4 is excluded entirely.
        Assert.AreEqual(3, byKey.Count);
        CollectionAssert.AreEquivalent(new[] { 1L, 2L, 3L }, byKey.Keys.ToList());

        // The whole point: all 5 of customer 1's brand rows survive, including those that appear in the
        // file after customer 4's row (a per-source row cap would have lost them).
        CollectionAssert.AreEquivalent(new[] { "BrandA", "BrandB", "B2", "B4", "B3" }, Brands(byKey[1]));
        CollectionAssert.AreEquivalent(new[] { "B4" }, Brands(byKey[2]));
        CollectionAssert.AreEquivalent(new[] { "BrandA" }, Brands(byKey[3]));
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void Sample_ScopesGrandchildrenJoinedOnANonSampleKey(string strategy)
    {
        WriteSources();

        var byKey = ByKey(Run(strategy, Sample3));

        // loyalty joins on loyalty_id, but carries customer_key, so it is scoped and still nests correctly.
        foreach (var brand in byKey[1].GetProperty("brand_profile").EnumerateArray())
        {
            var tiers = brand.GetProperty("loyalty").EnumerateArray()
                .Select(l => l.GetProperty("tier").GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "Silver" }, tiers);
        }
        Assert.AreEqual("Bronze",
            byKey[2].GetProperty("brand_profile")[0].GetProperty("loyalty")[0].GetProperty("tier").GetString());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void Sample_ScopesLookupSourcesToo(string strategy)
    {
        WriteSources();

        var byKey = ByKey(Run(strategy, Sample3));

        // The lookup over brand still resolves for sampled parents...
        Assert.AreEqual("BrandA", byKey[1].GetProperty("dob_lookup").GetString());
        Assert.AreEqual("B4", byKey[2].GetProperty("dob_lookup").GetString());
        // ...and customer 4 isn't in the output at all, so its lookup row was never indexed.
        Assert.IsFalse(byKey.ContainsKey(4));
    }

    [TestMethod]
    public void Sample_UnknownKeyColumnOnPrimary_Throws()
    {
        WriteSources();

        var ex = Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() =>
            Run("externalMerge", ", \"sample\": { \"primaryRows\": 3, \"keyColumn\": \"nope\" }"));
        StringAssert.Contains(ex.Message, "nope");
    }

    [TestMethod]
    public void Sample_MissingKeyColumn_Throws()
    {
        WriteSources();

        var ex = Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() =>
            Run("externalMerge", ", \"sample\": { \"primaryRows\": 3 }"));
        StringAssert.Contains(ex.Message, "keyColumn");
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void MaxRecords_StopsOutputAtTheCap(string strategy)
    {
        WriteSources();

        var result = Run(strategy, ", \"maxRecords\": 2");

        Assert.AreEqual(2, result.Records);
        Assert.AreEqual(2, ByKey(result).Count);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void MaxRecordsEqualToSplitEvery_ProducesExactlyOneBatchFile(string strategy)
    {
        WriteSources();

        // The full feed is 4 records; capping at 2 with splitEvery 2 must not roll a second file.
        var result = Run(strategy, ", \"maxRecords\": 2", split: 2);

        Assert.AreEqual(2, result.Records);
        Assert.AreEqual(1, result.Files.Count, "a cap equal to splitEvery must not open a second part file");
        Assert.IsTrue(result.Files[0].EndsWith("userprops_ts.jsonl", StringComparison.Ordinal),
            $"single-file runs drop the ordinal; got {result.Files[0]}");
    }

    [TestMethod]
    public void NoSample_ReadsAndEmitsEverything()
    {
        WriteSources();

        var result = Run("externalMerge", "");

        Assert.AreEqual(4, result.Records);
        var byKey = ByKey(result);
        Assert.AreEqual(5, Brands(byKey[1]).Count);
        Assert.AreEqual(1, Brands(byKey[4]).Count);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyFilter_SelectsExactlyTheGivenKey_EvenOutsideTheFirstNRows(string strategy)
    {
        WriteSources();

        // Customer 4 is deliberately the LAST primary row and its brand row sits ABOVE customer 1's
        // remaining rows in brand.csv — a run.sample primaryRows:3 would exclude it entirely. keyFilter
        // must still find it, since it targets a specific value, not a row-count prefix.
        var byKey = ByKey(Run(strategy, KeyFilter("4")));

        Assert.AreEqual(1, byKey.Count);
        CollectionAssert.AreEquivalent(new[] { "B2" }, Brands(byKey[4]));
        Assert.AreEqual("Gold", byKey[4].GetProperty("brand_profile")[0].GetProperty("loyalty")[0].GetProperty("tier").GetString());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyFilter_MultipleValues_SelectsAllAndOnlyThose(string strategy)
    {
        WriteSources();

        var byKey = ByKey(Run(strategy, KeyFilter("2", "4")));

        CollectionAssert.AreEquivalent(new[] { 2L, 4L }, byKey.Keys.ToList());
        CollectionAssert.AreEquivalent(new[] { "B4" }, Brands(byKey[2]));
        CollectionAssert.AreEquivalent(new[] { "B2" }, Brands(byKey[4]));
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyFilter_ScopesLookupSourcesToo(string strategy)
    {
        WriteSources();

        var byKey = ByKey(Run(strategy, KeyFilter("2")));

        Assert.AreEqual("B4", byKey[2].GetProperty("dob_lookup").GetString());
        Assert.IsFalse(byKey.ContainsKey(1));
    }

    [TestMethod]
    public void KeyFilter_UnknownKeyColumnOnPrimary_Throws()
    {
        WriteSources();

        var ex = Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() =>
            Run("externalMerge", ", \"keyFilter\": { \"keyColumn\": \"nope\", \"values\": [\"1\"] }"));
        StringAssert.Contains(ex.Message, "nope");
    }

    [TestMethod]
    public void KeyFilter_MissingKeyColumn_Throws()
    {
        WriteSources();

        var ex = Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() =>
            Run("externalMerge", ", \"keyFilter\": { \"values\": [\"1\"] }"));
        StringAssert.Contains(ex.Message, "keyColumn");
    }

    [TestMethod]
    public void SampleAndKeyFilterTogether_Throws()
    {
        WriteSources();

        var ex = Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() =>
            Run("externalMerge", Sample3 + KeyFilter("1")));
        StringAssert.Contains(ex.Message, "mutually exclusive");
    }
}
