using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class EtlEngineTests
{
    private string _dir = "";
    private string _out = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "etltest_" + Guid.NewGuid().ToString("N"));
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

    private List<JsonElement> Run(string configJson, string strategy, int outputIndex = 0)
    {
        var config = EtlConfigLoader.FromJson(configJson.Replace("<STRATEGY>", strategy));
        var results = EtlEngine.Run(config, _dir, _out, "ts");
        var lines = File.ReadAllLines(results[outputIndex].Files[0]);
        return lines.Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    private const string ObjectConfig = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "corp":  { "file": "corp.csv",  "fields": { "customer_key": { "type": "integer" } } },
        "brand": { "file": "brand.csv", "fields": { "customer_key": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "userprops", "mode": "object", "source": "corp",
          "fields": [
            { "name": "customer_key", "from": "customer_key", "type": "integer" },
            { "name": "name", "from": "name", "type": "string" }
          ],
          "children": [
            { "source": "brand", "as": "brand_profile", "cardinality": "many",
              "on": { "parent": "customer_key", "child": "customer_key" },
              "fields": [ { "name": "brand", "from": "brand", "type": "string" } ] }
          ],
          "load": { "fileBaseName": "userprops", "splitEvery": 1000000 } }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ObjectMode_NestsChildrenAndTypesAndOmitsEmpty(string strategy)
    {
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n2,Bob\n3,\n");
        WriteFile("brand.csv", "customer_key,brand\n1,BrandA\n1,BrandB\n2,BrandC\n");

        var rows = Run(ObjectConfig, strategy);
        Assert.AreEqual(3, rows.Count);

        var byKey = rows.ToDictionary(r => r.GetProperty("customer_key").GetInt64());

        // customer_key is emitted as a JSON number (typed integer).
        Assert.AreEqual(JsonValueKind.Number, byKey[1].GetProperty("customer_key").ValueKind);
        Assert.AreEqual("Alice", byKey[1].GetProperty("name").GetString());

        var brands1 = byKey[1].GetProperty("brand_profile").EnumerateArray()
            .Select(b => b.GetProperty("brand").GetString()).ToHashSet();
        CollectionAssert.AreEquivalent(new[] { "BrandA", "BrandB" }, brands1.ToList());

        Assert.AreEqual(1, byKey[2].GetProperty("brand_profile").GetArrayLength());

        // Empty name is omitted; a childless customer omits the array property entirely (never "[]").
        Assert.IsFalse(byKey[3].TryGetProperty("name", out _));
        Assert.IsFalse(byKey[3].TryGetProperty("brand_profile", out _));
    }

    [TestMethod]
    public void RelationArray_WithNoMatchingChildren_OmitsThePropertyEntirely()
    {
        // A plain (non-keyed) array relation with zero matching rows must never write "[]" — same "omit
        // empty" convention as every other empty/null value in the output.
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n2,Bob\n");
        WriteFile("cards.csv", "customer_key,card_type\n1,Visa\n");
        const string config = """
        {
          "sources": {
            "corp":  { "file": "corp.csv",  "fields": { "customer_key": { "type": "integer" } } },
            "cards": { "file": "cards.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "userprops", "mode": "object", "source": "corp",
              "fields": [ { "name": "customer_key", "from": "customer_key", "type": "integer" } ],
              "children": [
                { "source": "cards", "as": "cards", "cardinality": "many",
                  "on": { "parent": "customer_key", "child": "customer_key" },
                  "fields": [ { "name": "card_type", "from": "card_type", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "userprops", "splitEvery": 1000000 } }
          ]
        }
        """;
        var rows = Run(config, "hashInMemory");
        var byKey = rows.ToDictionary(r => r.GetProperty("customer_key").GetInt64());

        Assert.IsTrue(byKey[1].TryGetProperty("cards", out var cards1));
        Assert.AreEqual(1, cards1.GetArrayLength());

        Assert.IsFalse(byKey[2].TryGetProperty("cards", out _), "customer 2 has no cards; the key must be absent, not an empty array");
    }

    private const string KeyedMapConfig = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "email": { "file": "email.csv", "fields": { "customer_key": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "contacts", "mode": "keyedMap", "source": "email",
          "groupBy": "customer_key",
          "keyExpr": { "pipeline": [ { "op": "concat", "values": [ { "literal": "email::" }, { "field": "email_address" } ] } ] },
          <DEDUP>
          "fields": [
            { "name": "value", "from": "email_address", "type": "string" },
            { "name": "status", "from": "status", "type": "string" }
          ],
          "load": { "fileBaseName": "contacts", "splitEvery": 1000000 } }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedMap_GroupsByKeyAndBuildsDynamicKeys(string strategy)
    {
        WriteFile("email.csv", "customer_key,email_address,status\n1,a@x.com,A\n1,b@x.com,B\n2,c@x.com,C\n");

        var rows = Run(KeyedMapConfig.Replace("<DEDUP>", ""), strategy);
        Assert.AreEqual(2, rows.Count);

        var group1 = rows.First(r => r.TryGetProperty("email::a@x.com", out _));
        Assert.AreEqual("a@x.com", group1.GetProperty("email::a@x.com").GetProperty("value").GetString());
        Assert.IsTrue(group1.TryGetProperty("email::b@x.com", out _));

        var group2 = rows.First(r => r.TryGetProperty("email::c@x.com", out _));
        Assert.AreEqual(1, group2.EnumerateObject().Count());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedMap_Dedup_KeepsMostRecentWithinAddress(string strategy)
    {
        // customer 1 has a@x.com twice; dedup keeps the row with the latest status_date.
        WriteFile("email.csv",
            "customer_key,email_address,status,status_date\n" +
            "1,a@x.com,OLD,2026-01-01\n" +
            "1,a@x.com,NEW,2026-06-01\n");

        const string dedup = "\"dedup\": { \"within\": \"email_address\", \"keepMaxOf\": \"status_date\" },";
        var rows = Run(KeyedMapConfig.Replace("<DEDUP>", dedup), strategy);

        Assert.AreEqual(1, rows.Count);
        var entry = rows[0].GetProperty("email::a@x.com");
        Assert.AreEqual("NEW", entry.GetProperty("status").GetString());
        Assert.AreEqual(1, rows[0].EnumerateObject().Count());
    }

    [TestMethod]
    public void OnError_Skip_DropsRowAndWritesRejectFile()
    {
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n2,Bob\n");
        WriteFile("brand.csv", "customer_key,brand\n1,BrandA\n2,BrandC\n");

        // 'name' is forced through a throwing integer conversion; both rows fail and are skipped.
        const string config = """
        {
          "run": { "joinStrategy": "hashInMemory", "onError": "skip" },
          "sources": { "corp": { "file": "corp.csv" }, "brand": { "file": "brand.csv" } },
          "outputs": [
            { "name": "bad", "mode": "object", "source": "corp",
              "fields": [
                { "name": "name", "from": "name", "type": "integer",
                  "pipeline": [ { "op": "toInteger", "onError": "throw" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "bad", "splitEvery": 1000000 } }
          ]
        }
        """;

        var result = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        Assert.AreEqual(0, result[0].Records);
        Assert.AreEqual(2, result[0].Rejected);
        Assert.IsTrue(File.Exists(Path.Combine(_out, "run_ts", "bad_ts_rejects.csv")));
    }

    [TestMethod]
    public void OnError_Nullify_OmitsFieldButKeepsRow()
    {
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n");

        const string config = """
        {
          "run": { "joinStrategy": "hashInMemory", "onError": "nullify" },
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "n", "mode": "object", "source": "corp",
              "fields": [
                { "name": "customer_key", "from": "customer_key", "type": "integer" },
                { "name": "name", "from": "name", "type": "integer", "pipeline": [ { "op": "toInteger", "onError": "throw" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "n", "splitEvery": 1000000 } }
          ]
        }
        """;

        var result = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        Assert.AreEqual(1, result[0].Records);
        var row = JsonDocument.Parse(File.ReadAllLines(result[0].Files[0])[0]).RootElement;
        Assert.AreEqual(1L, row.GetProperty("customer_key").GetInt64());
        Assert.IsFalse(row.TryGetProperty("name", out _)); // the bad field was nullified/omitted
    }

    [TestMethod]
    public void EmitEmpty_KeepsBlankStringsWhenEnabled()
    {
        WriteFile("corp.csv", "customer_key,name,middle\n1,Alice,\n");

        string Config(bool emit) => $$"""
        {
          "run": { "joinStrategy": "hashInMemory", "emitEmpty": {{(emit ? "true" : "false")}} },
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "c", "mode": "object", "source": "corp",
              "fields": [
                { "name": "name",   "from": "name",   "type": "string" },
                { "name": "middle", "from": "middle", "type": "string" }
              ],
              "children": [],
              "load": { "fileBaseName": "c", "splitEvery": 1000000 } }
          ]
        }
        """;

        // default: blank middle is omitted
        var off = EtlEngine.Run(EtlConfigLoader.FromJson(Config(false)), _dir, _out, "ts1");
        var rowOff = JsonDocument.Parse(File.ReadAllLines(off[0].Files[0])[0]).RootElement;
        Assert.IsFalse(rowOff.TryGetProperty("middle", out _));

        // emitEmpty: blank middle is kept as ""
        var on = EtlEngine.Run(EtlConfigLoader.FromJson(Config(true)), _dir, _out, "ts2");
        var rowOn = JsonDocument.Parse(File.ReadAllLines(on[0].Files[0])[0]).RootElement;
        Assert.IsTrue(rowOn.TryGetProperty("middle", out var m));
        Assert.AreEqual("", m.GetString());
    }

    [TestMethod]
    public void IdenticalLookupAcrossOutputs_IsBuiltOnceAndShared()
    {
        WriteFile("corp.csv", "id,brand\n1,B1\n");
        WriteFile("brand.csv", "person_id,brand,site\n1,B1,site-b1\n");

        // Two outputs, each with its own IDENTICAL lookup (same source/join/column/mode) — this is
        // exactly the situation of several event outputs all looking up the same transactions source.
        var lookupField = """
            { "name": "site", "type": "array",
              "lookup": { "source": "brand", "on": { "parent": "id", "child": "person_id" }, "column": "site", "distinct": true } }
            """;
        var config = $$"""
        {
          "run": { "joinStrategy": "hashInMemory" },
          "sources": {
            "corp": { "file": "corp.csv", "fields": { "id": { "type": "integer" } } },
            "brand": { "file": "brand.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "a", "mode": "object", "source": "corp",
              "fields": [ { "name": "id", "from": "id", "type": "integer" }, {{lookupField}} ],
              "children": [], "load": { "fileBaseName": "a", "sampling": { "enabled": false } } },
            { "name": "b", "mode": "object", "source": "corp",
              "fields": [ { "name": "id", "from": "id", "type": "integer" }, {{lookupField}} ],
              "children": [], "load": { "fileBaseName": "b", "sampling": { "enabled": false } } }
          ]
        }
        """;

        var logLines = new List<string>();
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts", logLines.Add);

        Assert.AreEqual(1, logLines.Count(l => l.Contains("lookup reused from an earlier output")),
            "the second output's identical lookup should be reused, not rebuilt");

        foreach (var r in results)
        {
            var row = JsonDocument.Parse(File.ReadAllLines(r.Files[0])[0]).RootElement;
            CollectionAssert.AreEqual(new[] { "site-b1" },
                row.GetProperty("site").EnumerateArray().Select(e => e.GetString()).ToArray());
        }
    }

    [TestMethod]
    public void SampledRun_DoesNotShareLookupsAcrossOutputs()
    {
        // Sampling scopes each output to ITS OWN primary rows, so sharing a lookup index across outputs
        // would be wrong even if the lookup config looks identical.
        WriteFile("corp.csv", "id,brand\n1,B1\n2,B1\n");
        WriteFile("brand.csv", "person_id,brand,site\n1,B1,site-b1\n2,B1,site-b1\n");

        var lookupField = """
            { "name": "site", "type": "array",
              "lookup": { "source": "brand", "on": { "parent": "id", "child": "person_id" }, "column": "site", "distinct": true } }
            """;
        var config = $$"""
        {
          "run": { "joinStrategy": "hashInMemory", "sample": { "primaryRows": 1, "keyColumn": "id" } },
          "sources": {
            "corp": { "file": "corp.csv", "fields": { "id": { "type": "integer" } } },
            "brand": { "file": "brand.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "a", "mode": "object", "source": "corp",
              "fields": [ { "name": "id", "from": "id", "type": "integer" }, {{lookupField}} ],
              "children": [], "load": { "fileBaseName": "a", "sampling": { "enabled": false } } },
            { "name": "b", "mode": "object", "source": "corp",
              "fields": [ { "name": "id", "from": "id", "type": "integer" }, {{lookupField}} ],
              "children": [], "load": { "fileBaseName": "b", "sampling": { "enabled": false } } }
          ]
        }
        """;

        var logLines = new List<string>();
        EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts", logLines.Add);
        Assert.AreEqual(0, logLines.Count(l => l.Contains("lookup reused from an earlier output")),
            "sampled runs must not share lookup indexes across outputs");
    }

    [TestMethod]
    public void EachRun_GetsItsOwnTimestampedSubfolder_SoRunsNeverComingle()
    {
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n");
        const string config = """
        {
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "c", "mode": "object", "source": "corp",
              "fields": [ { "name": "name", "from": "name", "type": "string" } ],
              "children": [],
              "load": { "fileBaseName": "c", "splitEvery": 1000000 } }
          ]
        }
        """;

        var first = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "20260101_120000");
        var second = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "20260101_130000");

        StringAssert.Contains(first[0].Files[0], Path.Combine("run_20260101_120000"));
        StringAssert.Contains(second[0].Files[0], Path.Combine("run_20260101_130000"));
        Assert.IsTrue(File.Exists(first[0].Files[0]));
        Assert.IsTrue(File.Exists(second[0].Files[0]));
        // both survive side by side under the shared fallback output folder
        Assert.AreEqual(2, Directory.GetDirectories(_out).Length);
    }

    [TestMethod]
    public void RunTimestampToken_PlacesRunFolderWhereTheConfigPutsIt_SharedAcrossOutputs()
    {
        // Two outputs each name their own subfolder ("events"/"profiles"), but both want the per-run
        // folder to be the shared parent, not nested inside each: .../out/run_<ts>/events, not
        // .../out/events/run_<ts>. The "{run_timestamp}" token in "folder" controls that placement.
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n");
        var config = $$"""
        {
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "events", "mode": "object", "source": "corp",
              "fields": [ { "name": "name", "from": "name", "type": "string" } ],
              "children": [],
              "load": { "fileBaseName": "e", "folder": "{{_out}}/{run_timestamp}/events", "splitEvery": 1000000 } },
            { "name": "profiles", "mode": "object", "source": "corp",
              "fields": [ { "name": "name", "from": "name", "type": "string" } ],
              "children": [],
              "load": { "fileBaseName": "p", "folder": "{{_out}}/{run_timestamp}/profiles", "splitEvery": 1000000 } }
          ]
        }
        """;

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "20260101_120000");

        var runFolder = Path.Combine(_out, "run_20260101_120000");
        Assert.AreEqual(Path.Combine(runFolder, "events", "e_20260101_120000.jsonl"), results[0].Files[0]);
        Assert.AreEqual(Path.Combine(runFolder, "profiles", "p_20260101_120000.jsonl"), results[1].Files[0]);
        Assert.IsTrue(File.Exists(results[0].Files[0]));
        Assert.IsTrue(File.Exists(results[1].Files[0]));
        // one run folder shared by both outputs, not one per output
        Assert.AreEqual(1, Directory.GetDirectories(_out).Length);
        CollectionAssert.AreEquivalent(new[] { "events", "profiles" }, Directory.GetDirectories(runFolder).Select(Path.GetFileName).ToList());
    }

    [TestMethod]
    public void LoadFolder_OverridesFallbackOutputFolder()
    {
        WriteFile("corp.csv", "customer_key,name\n1,Alice\n");
        var custom = Path.Combine(_dir, "custom_out");

        var config = $$"""
        {
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "c", "mode": "object", "source": "corp",
              "fields": [ { "name": "name", "from": "name", "type": "string" } ],
              "children": [],
              "load": { "fileBaseName": "c", "folder": {{System.Text.Json.JsonSerializer.Serialize(custom)}}, "splitEvery": 1000000 } }
          ]
        }
        """;

        // fallback _out is provided but must be ignored because load.folder is set
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        Assert.AreEqual(1, results[0].Files.Count);
        StringAssert.StartsWith(results[0].Files[0], custom);
        Assert.IsTrue(File.Exists(results[0].Files[0]));
    }

    [TestMethod]
    public void Filter_ExcludesRowsAndCounts()
    {
        WriteFile("corp.csv", "customer_key,dt\n1,2019-05-01\n2,2021-05-01\n3,2023-05-01\n");
        const string config = """
        {
          "sources": { "corp": { "file": "corp.csv", "fields": { "customer_key": { "type": "integer" } } } },
          "outputs": [
            { "name": "c", "mode": "object", "source": "corp",
              "filter": [ { "column": "dt", "op": "onOrAfter", "value": "2020-01-01" } ],
              "filterExcludedFile": true,
              "fields": [ { "name": "id", "from": "customer_key", "type": "integer" } ],
              "children": [],
              "load": { "fileBaseName": "c", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        Assert.AreEqual(2, results[0].Records);   // 2021, 2023 kept
        Assert.AreEqual(1, results[0].Filtered);  // 2019 dropped
        var ids = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetInt64()).ToHashSet();
        CollectionAssert.AreEquivalent(new long[] { 2, 3 }, ids.ToList());
        Assert.IsTrue(File.Exists(Path.Combine(_out, "run_ts", "c_ts_filtered.csv")));
    }

    [TestMethod]
    public void Filter_RelativeDate_Resolves()
    {
        var twoYears = bleak.Data.Transformer.Engine.RowFilter.ResolveDate("now-2years");
        Assert.IsTrue(Math.Abs((DateTime.UtcNow.AddYears(-2) - twoYears).TotalDays) < 2);
        var abs = bleak.Data.Transformer.Engine.RowFilter.ResolveDate("2020-06-01");
        Assert.AreEqual(new DateTime(2020, 6, 1), abs.Date);
    }

    [TestMethod]
    public void BadConfig_UnknownSource_Throws()
    {
        WriteFile("corp.csv", "customer_key\n1\n");
        const string config = """
        { "sources": { "corp": { "file": "corp.csv" } },
          "outputs": [ { "name": "x", "mode": "object", "source": "missing", "fields": [], "load": {} } ] }
        """;
        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(
            () => { EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"); });
    }
}
