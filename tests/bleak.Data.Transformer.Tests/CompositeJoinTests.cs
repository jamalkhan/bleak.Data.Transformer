using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class CompositeJoinTests
{
    private string _dir = "";
    private string _out = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "etlcomp_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_out);
        // two customers, each with brand X — composite (id+brand) must NOT cross them
        File.WriteAllText(Path.Combine(_dir, "cust.csv"), "id,brand\n1,X\n2,X\n");
        File.WriteAllText(Path.Combine(_dir, "fav.csv"), "id,brand,page\n1,X,home\n2,X,cart\n");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string CompositeConfig = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "cust": { "file": "cust.csv", "fields": { "id": { "type": "integer" } } },
        "fav":  { "file": "fav.csv",  "fields": { "id": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "cust", "mode": "object", "source": "cust",
          "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
          "children": [
            { "source": "fav", "as": "fav", "cardinality": "many",
              "on": { "parents": ["id", "brand"], "children": ["id", "brand"] },
              "fields": [ { "name": "page", "from": "page", "type": "string" } ] }
          ],
          "load": { "fileBaseName": "cust", "splitEvery": 1000000 } }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void CompositeJoin_ScopesByBothColumns(string strategy)
    {
        var config = EtlConfigLoader.FromJson(CompositeConfig.Replace("<STRATEGY>", strategy));
        var results = EtlEngine.Run(config, _dir, _out, "ts");
        var byId = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToDictionary(r => r.GetProperty("id").GetInt64());

        // Each customer gets ONLY its own (id+brand) fav row — not the other customer's same-brand row.
        Assert.AreEqual(1, byId[1].GetProperty("fav").GetArrayLength());
        Assert.AreEqual("home", byId[1].GetProperty("fav")[0].GetProperty("page").GetString());
        Assert.AreEqual(1, byId[2].GetProperty("fav").GetArrayLength());
        Assert.AreEqual("cart", byId[2].GetProperty("fav")[0].GetProperty("page").GetString());
    }
}
