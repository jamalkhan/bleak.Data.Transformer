using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Tests;

/// <summary>The warnings file reports a generic <c>record_key</c> derived from the join key, not a hardcoded column.</summary>
[TestClass]
public class RecordKeyTests
{
    private string _dir = "";
    private string _out = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "etlkey_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_out);
        // Deliberately no column named customer_key anywhere: the key columns are person_ref / id.
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "person_ref,score\nA7,1.5e-05\nB9,0.42\n");
        File.WriteAllText(Path.Combine(_dir, "orders.csv"), "person_ref,order_id,amount\nA7,1,2.5e-03\nB9,2,10\n");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string[] RunAndReadWarnings(string outputJson)
    {
        var config = """
        {
          "sources": {
            "people": { "file": "people.csv" },
            "orders": { "file": "orders.csv" }
          },
          "outputs": [ <OUTPUT> ]
        }
        """.Replace("<OUTPUT>", outputJson);
        EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var file = Directory.GetFiles(Path.Combine(_out, "run_ts"), "*_warnings.csv").Single();
        return File.ReadAllLines(file);
    }

    [TestMethod]
    public void WarningsHeader_UsesRecordKey()
    {
        var lines = RunAndReadWarnings("""
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "ref", "from": "person_ref" }, { "name": "score", "from": "score", "type": "decimal" } ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
            """);

        Assert.AreEqual("record_key\tfield\treason\traw_value", lines[0]);
    }

    [TestMethod]
    public void PrimaryRecord_KeyIsTheJoinKeyTowardsItsChildren()
    {
        var lines = RunAndReadWarnings("""
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "ref", "from": "person_ref" }, { "name": "score", "from": "score", "type": "decimal" } ],
              "children": [ { "source": "orders", "as": "orders", "cardinality": "many",
                              "on": { "parent": "person_ref", "child": "person_ref" },
                              "fields": [ { "name": "order_id", "from": "order_id", "type": "integer" } ] } ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
            """);

        var row = lines.Single(l => l.Contains("score"));
        Assert.IsTrue(row.StartsWith("A7\t"), row);
    }

    [TestMethod]
    public void ChildRecord_KeyIsItsJoinKeyTowardsTheParent()
    {
        var lines = RunAndReadWarnings("""
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "ref", "from": "person_ref" } ],
              "children": [ { "source": "orders", "as": "orders", "cardinality": "many",
                              "on": { "parent": "person_ref", "child": "person_ref" },
                              "fields": [ { "name": "amount", "from": "amount", "type": "decimal" } ] } ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
            """);

        var row = lines.Single(l => l.Contains("amount"));
        Assert.IsTrue(row.StartsWith("A7\t"), row);
    }

    [TestMethod]
    public void RecordWithoutAnyKnownKey_HasBlankRecordKey()
    {
        var lines = RunAndReadWarnings("""
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "ref", "from": "person_ref" }, { "name": "score", "from": "score", "type": "decimal" } ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
            """);

        var row = lines.First(l => l.Contains("score") && l != lines[0]);
        Assert.IsTrue(row.StartsWith("\t"), row);
    }
}
