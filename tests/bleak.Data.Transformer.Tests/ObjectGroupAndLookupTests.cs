using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class ObjectGroupAndLookupTests
{
    private string _dir = "";
    private string _out = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "etlgrp_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_out);

        File.WriteAllText(Path.Combine(_dir, "people.csv"), "id,name,tags\n1,Alice,x;y;z\n");
        File.WriteAllText(Path.Combine(_dir, "emails.csv"),
            "person_id,addr,brand,email_key\n1,alice@e.com,B1,EK1\n1,alice@e.com,B2,EK1\n");
        File.WriteAllText(Path.Combine(_dir, "phones.csv"), "person_id,number\n1,555-1212\n");
        File.WriteAllText(Path.Combine(_dir, "loyalty.csv"), "email_key,tier\nEK1,GOLD\n");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string Config = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "people":  { "file": "people.csv",  "fields": { "id": { "type": "integer" } } },
        "emails":  { "file": "emails.csv",   "fields": { "person_id": { "type": "integer" } } },
        "phones":  { "file": "phones.csv",   "fields": { "person_id": { "type": "integer" } } },
        "loyalty": { "file": "loyalty.csv" }
      },
      "outputs": [
        {
          "name": "people", "mode": "object", "source": "people",
          "fields": [
            { "name": "id",   "from": "id",   "type": "integer" },
            { "name": "name", "from": "name", "type": "string" },
            { "name": "tags", "from": "tags", "type": "array", "pipeline": [ { "op": "split", "separator": ";" } ] },
            { "name": "unique_ids", "type": "object",
              "fields": [
                { "name": "email_keys", "type": "array",
                  "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "column": "email_key", "distinct": true } }
              ] },
            { "name": "first_brand", "type": "string",
              "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "column": "brand", "single": true } }
          ],
          "children": [
            { "source": "emails", "as": "contacts", "cardinality": "many",
              "on": { "parent": "id", "child": "person_id" },
              "fields": [
                { "name": "contact_type",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "email" } ] } ] },
                { "name": "contact_value", "from": "addr", "type": "string" },
                { "name": "contact_properties", "type": "object",
                  "fields": [
                    { "name": "brand", "from": "brand", "type": "string" },
                    { "name": "email_key", "from": "email_key", "type": "string" }
                  ],
                  "children": [
                    { "source": "loyalty", "as": "loyalty", "cardinality": "many",
                      "on": { "parent": "email_key", "child": "email_key" },
                      "fields": [ { "name": "tier", "from": "tier", "type": "string" } ] }
                  ] }
              ] },
            { "source": "phones", "as": "contacts", "cardinality": "many",
              "on": { "parent": "id", "child": "person_id" },
              "fields": [
                { "name": "contact_type",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "phone" } ] } ] },
                { "name": "contact_value", "from": "number", "type": "string" }
              ] }
          ],
          "load": { "fileBaseName": "people", "splitEvery": 1000000 }
        }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ObjectGroups_Lookups_Split_And_SharedAs(string strategy)
    {
        var config = EtlConfigLoader.FromJson(Config.Replace("<STRATEGY>", strategy));
        var results = EtlEngine.Run(config, _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0).ToList();
        Assert.AreEqual(1, lines.Count);
        var r = JsonDocument.Parse(lines[0]).RootElement;

        // split -> array
        CollectionAssert.AreEqual(new[] { "x", "y", "z" },
            r.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray());

        // object-group wrapping an array lookup (distinct)
        var keys = r.GetProperty("unique_ids").GetProperty("email_keys").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "EK1" }, keys);

        // single lookup ("first available")
        Assert.AreEqual("B1", r.GetProperty("first_brand").GetString());

        // shared 'as': two email contacts + one phone contact, concatenated into one array
        var contacts = r.GetProperty("contacts").EnumerateArray().ToList();
        Assert.AreEqual(3, contacts.Count);

        Assert.AreEqual("email", contacts[0].GetProperty("contact_type").GetString());
        Assert.AreEqual("alice@e.com", contacts[0].GetProperty("contact_value").GetString());
        // object-group with a nested relationship (loyalty) inside it
        var cp = contacts[0].GetProperty("contact_properties");
        Assert.AreEqual("B1", cp.GetProperty("brand").GetString());
        Assert.AreEqual("GOLD", cp.GetProperty("loyalty")[0].GetProperty("tier").GetString());

        var phone = contacts.Single(c => c.GetProperty("contact_type").GetString() == "phone");
        Assert.AreEqual("555-1212", phone.GetProperty("contact_value").GetString());
        Assert.IsFalse(phone.TryGetProperty("contact_properties", out _)); // phone has no contact_properties
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void LookupExists_WithFilter_YieldsBool(string strategy)
    {
        // Alice has two email rows (B1, B2); neither is marked marketable=false, so "exists" should be true.
        // Add a second person with no marketable email at all to prove the false case.
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "id,name,tags\n1,Alice,x;y;z\n2,Bob,\n");
        File.WriteAllText(Path.Combine(_dir, "emails.csv"),
            "person_id,addr,brand,email_key,marketable\n1,alice@e.com,B1,EK1,true\n1,alice@e.com,B2,EK1,false\n2,bob@e.com,B1,EK2,false\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "is_marketable", "type": "bool",
                  "lookup": {
                    "source": "emails", "on": { "parent": "id", "child": "person_id" },
                    "mode": "exists",
                    "filter": [ { "column": "marketable", "op": "eq", "value": "true" } ]
                  } }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();

        var alice = lines.Single(r => r.GetProperty("id").GetInt32() == 1);
        var bob = lines.Single(r => r.GetProperty("id").GetInt32() == 2);
        Assert.IsTrue(alice.GetProperty("is_marketable").GetBoolean()); // B1 row is marketable=true
        Assert.IsFalse(bob.GetProperty("is_marketable").GetBoolean()); // only row is marketable=false
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ArrayLookup_WithMapPipeline_TranslatesEachElement(string strategy)
    {
        // site_ids: a distinct array lookup over raw brand codes, each translated via "map".
        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array",
                  "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "column": "brand", "distinct": true },
                  "pipeline": [ { "op": "map", "cases": { "B1": "site-b1", "B2": "site-b2" } } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var sites = r.GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "site-b1", "site-b2" }, sites);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ScientificNotationDecimal_ParsesAndWarns_RecordStillWritten(string strategy)
    {
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "id,name,score\n1,Alice,1.5e-05\n2,Bob,0.42\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" }, "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "score", "from": "score", "type": "decimal" }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");

        Assert.AreEqual(0, results[0].Rejected, "scientific notation must not reject the row");
        Assert.AreEqual(2, results[0].Records);
        Assert.AreEqual(1, results[0].Warnings);

        var lines = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var alice = lines.Single(r => r.GetProperty("id").GetInt32() == 1);
        Assert.AreEqual(1.5e-05, alice.GetProperty("score").GetDouble(), 1e-20);

        var warningsFile = Directory.GetFiles(Path.Combine(_out, "run_ts"), "*_warnings.csv").Single();
        var warningText = File.ReadAllText(warningsFile);
        StringAssert.Contains(warningText, "score");
        StringAssert.Contains(warningText, "1.5e-05");
        StringAssert.Contains(warningText, "scientific notation");
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void FieldOnError_WarnAndNull_KeepsRecordAndOmitsOnlyThatField(string strategy)
    {
        // date_of_birth is sometimes month-day-only with no year (e.g. "2-29") — a real, common shape
        // in the source, not an anomaly. It must not sink the whole record.
        File.WriteAllText(Path.Combine(_dir, "people.csv"),
            "id,name,date_of_birth\n1,Alice,2-29\n2,Bob,1990-05-01\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>", "onError": "skip" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" }, "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "name", "from": "name", "type": "string" },
                { "name": "date_of_birth", "from": "date_of_birth", "type": "dateTime", "onError": "warnAndNull" }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");

        Assert.AreEqual(0, results[0].Rejected, "the bad date must not reject the whole record");
        Assert.AreEqual(2, results[0].Records, "both records must survive");
        Assert.AreEqual(1, results[0].Warnings);

        var lines = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var alice = lines.Single(r => r.GetProperty("id").GetInt32() == 1);
        var bob = lines.Single(r => r.GetProperty("id").GetInt32() == 2);
        Assert.IsFalse(alice.TryGetProperty("date_of_birth", out _), "unparseable date_of_birth is omitted, not fabricated");
        Assert.AreEqual("Alice", alice.GetProperty("name").GetString(), "the rest of the record survives");
        Assert.AreEqual("1990-05-01T00:00:00Z", bob.GetProperty("date_of_birth").GetString());

        var warningsFile = Directory.GetFiles(Path.Combine(_out, "run_ts"), "*_warnings.csv").Single();
        var text = File.ReadAllText(warningsFile);
        StringAssert.Contains(text, "date_of_birth");
        StringAssert.Contains(text, "2-29");
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void OutputDistinctBy_CollapsesPrimaryRows_ButChildrenStillSeeAll(string strategy)
    {
        // Two "purchased" line items for the same order; distinctBy collapses the primary stream to one
        // event per order_id, while the "products" child (joined independently) still sees both items.
        File.WriteAllText(Path.Combine(_dir, "people.csv"),
            "order_id,product_id,status\nORD1,P1,ok\nORD1,P2,ok\nORD2,P3,ok\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv" }
          },
          "outputs": [
            { "name": "events", "mode": "object", "source": "people",
              "distinctBy": "order_id",
              "fields": [ { "name": "order_id", "from": "order_id", "type": "string" } ],
              "children": [
                { "source": "people", "as": "products", "cardinality": "many",
                  "on": { "parent": "order_id", "child": "order_id" },
                  "fields": [ { "name": "product_id", "from": "product_id", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "events", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");

        Assert.AreEqual(2, results[0].Records, "one event per distinct order_id, not per line item");
        var lines = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var ord1 = lines.Single(r => r.GetProperty("order_id").GetString() == "ORD1");
        var products = ord1.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("product_id").GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "P1", "P2" }, products);
    }

    private const string RequireRelationsConfig = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
        "apps":   { "file": "apps.csv",   "fields": { "person_id": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "people", "mode": "object", "source": "people",
          "requireRelations": [ "<REQUIRED>" ],
          "filterExcludedFile": true,
          "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
          "children": [
            { "source": "apps", "as": "devices", "cardinality": "many",
              "on": { "parent": "id", "child": "person_id" },
              "filter": [ { "column": "platform", "op": "in", "value": [ "ios", "android" ] } ],
              "fields": [ { "name": "device_id", "from": "device_id", "type": "string" } ] },
            { "source": "apps", "as": "scoped", "cardinality": "many",
              "on": { "parent": "id", "child": "person_id" },
              "filter": [ { "column": "platform", "op": "in", "value": [ "ios", "android" ] } ],
              "keyBy": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "b1-site" } } ] },
              "fields": [ { "name": "device_id", "from": "device_id", "type": "string" } ] }
          ],
          "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory", "devices")]
    [DataRow("externalMerge", "devices")]
    [DataRow("hashInMemory", "scoped")]
    [DataRow("externalMerge", "scoped")]
    public void RequireRelations_DropsRecordsWithoutContent(string strategy, string required)
    {
        // 1: has an ios row (kept). 2: no app rows at all (dropped). 3: only a row the child filter
        // excludes (dropped). 4: an android row whose brand doesn't map to a site key — content for the
        // plain relation, but none for the keyed one.
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "id,name\n1,A\n2,B\n3,C\n4,D\n");
        File.WriteAllText(Path.Combine(_dir, "apps.csv"),
            "person_id,device_id,platform,brand\n1,D1,ios,B1\n3,D3,web,B1\n4,D4,android,XX\n");

        var config = RequireRelationsConfig.Replace("<STRATEGY>", strategy).Replace("<REQUIRED>", required);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");

        var ids = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetInt64()).ToArray();
        var expected = required == "devices" ? new long[] { 1, 4 } : new long[] { 1 };
        CollectionAssert.AreEquivalent(expected, ids);
        Assert.AreEqual(4 - expected.Length, results[0].Filtered);
    }

    [TestMethod]
    public void RequireRelations_UnknownRelation_FailsAtCompile()
    {
        var config = RequireRelationsConfig.Replace("<STRATEGY>", "hashInMemory").Replace("<REQUIRED>", "nope");
        File.WriteAllText(Path.Combine(_dir, "apps.csv"), "person_id,device_id,platform,brand\n");
        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(
            () => EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"));
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ChildFilter_OnlyIncludesMatchingChildren(string strategy)
    {
        File.WriteAllText(Path.Combine(_dir, "people.csv"),
            "id,name\n1,Alice\n");
        File.WriteAllText(Path.Combine(_dir, "emails.csv"),
            "person_id,addr,status\n1,keep@e.com,active\n1,drop@e.com,inactive\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "emails", "as": "emails", "cardinality": "many",
                  "on": { "parent": "id", "child": "person_id" },
                  "filter": [ { "column": "status", "op": "eq", "value": "active" } ],
                  "fields": [ { "name": "addr", "from": "addr", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var addrs = r.GetProperty("emails").EnumerateArray().Select(e => e.GetProperty("addr").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "keep@e.com" }, addrs);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void InlineChild_SplicesFieldsAsSiblings_NoWrapperKey(string strategy)
    {
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "order_id,item_name\nORD1,Shirt\n");
        File.WriteAllText(Path.Combine(_dir, "orders.csv"), "order_id,order_total,order_date\nORD1,42.5,2026-01-01\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv" },
            "orders": { "file": "orders.csv" }
          },
          "outputs": [
            { "name": "events", "mode": "object", "source": "people",
              "fields": [
                { "name": "properties", "type": "object",
                  "fields": [ { "name": "item_name", "from": "item_name", "type": "string" } ],
                  "children": [
                    { "source": "orders", "as": "order_flat", "cardinality": "one", "inline": true,
                      "on": { "parent": "order_id", "child": "order_id" },
                      "fields": [
                        { "name": "order_total", "from": "order_total", "type": "decimal" },
                        { "name": "order_date", "from": "order_date", "type": "dateTime" }
                      ] }
                  ] }
              ],
              "children": [],
              "load": { "fileBaseName": "events", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var props = r.GetProperty("properties");

        Assert.IsFalse(props.TryGetProperty("order_flat", out _), "no wrapper key should appear");
        Assert.AreEqual("Shirt", props.GetProperty("item_name").GetString());
        Assert.AreEqual(42.5, props.GetProperty("order_total").GetDouble());
        Assert.AreEqual("2026-01-01T00:00:00Z", props.GetProperty("order_date").GetString());
    }

    [TestMethod]
    public void InlineChild_WithManyCardinality_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "orders.csv"), "person_id,x\n1,a\n1,b\n");
        var config = """
        {
          "sources": { "people": { "file": "people.csv" }, "orders": { "file": "orders.csv" } },
          "outputs": [
            { "name": "events", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "string" } ],
              "children": [
                { "source": "orders", "as": "bad", "cardinality": "many", "inline": true,
                  "on": { "parent": "id", "child": "person_id" },
                  "fields": [ { "name": "x", "from": "x", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "events" } }
          ]
        }
        """;
        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(
            () => EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"));
    }

    [TestMethod]
    public void DateTimeField_RendersCompactIso8601WithZ()
    {
        // Raw source formats vary (space-separated, date-only, with/without offset); output must always
        // normalize to compact ISO 8601 truncated to whole seconds, e.g. "2013-08-02T00:00:00Z".
        File.WriteAllText(Path.Combine(_dir, "people.csv"),
            "id,acquisition_date,loyalty_acquisition_date\n1,2013-08-02,2022-05-07 07:29:55\n");

        var config = """
        {
          "run": { "joinStrategy": "hashInMemory" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "acquisition_date", "from": "acquisition_date", "type": "dateTime" },
                { "name": "loyalty_acquisition_date", "from": "loyalty_acquisition_date", "type": "dateTime" }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        Assert.AreEqual("2013-08-02T00:00:00Z", r.GetProperty("acquisition_date").GetString());
        Assert.AreEqual("2022-05-07T07:29:55Z", r.GetProperty("loyalty_acquisition_date").GetString());
    }

    [TestMethod]
    public void Sampling_DefaultsOn_WritesPrettyFilesNamedByConfiguredFields()
    {
        // people.csv (from Setup) has one row: id=1, name=Alice, tags=x;y;z.
        var config = """
        {
          "run": { "joinStrategy": "hashInMemory" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "name", "from": "name", "type": "string" }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000,
                "sampling": { "count": 10, "fileNameFields": [ "id", "name" ] } } }
          ]
        }
        """;
        EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");

        var sampleDir = Path.Combine(_out, "run_ts", "sampling");
        Assert.IsTrue(Directory.Exists(sampleDir), "sampling folder should be created by default");
        var files = Directory.GetFiles(sampleDir);
        Assert.AreEqual(1, files.Length); // only 1 record exists, so reservoir holds just that one
        Assert.AreEqual("1_Alice.json", Path.GetFileName(files[0]));
        var text = File.ReadAllText(files[0]);
        Assert.IsTrue(text.Contains('\n'), "sample file should be pretty-printed (multi-line)");
        var parsed = JsonDocument.Parse(text).RootElement;
        Assert.AreEqual(1, parsed.GetProperty("id").GetInt32());
        Assert.AreEqual("Alice", parsed.GetProperty("name").GetString());
    }

    [TestMethod]
    public void Sampling_DisabledExplicitly_WritesNoSamplingFolder()
    {
        var config = """
        {
          "run": { "joinStrategy": "hashInMemory" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        Assert.IsFalse(Directory.Exists(Path.Combine(_out, "run_ts", "sampling")));
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void ArrayLookup_WithExpr_CombinesMultipleColumnsPerRow(string strategy)
    {
        // site_ids: brand alone isn't enough (market varies per row); expr combines both columns
        // from the LOOKUP source's own row (not the root row), the same way "brand" alone couldn't.
        File.WriteAllText(Path.Combine(_dir, "emails.csv"),
            "person_id,addr,brand,email_key,market\n1,alice@e.com,B1,EK1,US\n1,alice@e.com,B2,EK1,CA\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array",
                  "lookup": { "source": "emails", "on": { "parent": "id", "child": "person_id" }, "distinct": true,
                    "expr": [ { "op": "concat", "values": [
                      { "literal": "site-" }, { "field": "market" }, { "literal": "-" }, { "field": "brand" } ] },
                      { "op": "lower" } ] } }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var sites = r.GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "site-us-b1", "site-ca-b2" }, sites);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedRelation_GroupsChildrenByDerivedKey(string strategy)
    {
        // Two email rows for Alice under different brands (B1, B2) -> pivot into an object keyed by
        // a mapped site_id, each holding an array of contacts for that site.
        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } },
            "phones": { "file": "phones.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "emails", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "person_id" },
                  "keyBy": { "from": "brand", "pipeline": [
                    { "op": "map", "cases": { "B1": "site-b1", "B2": "site-b2" } } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "email" } ] } ] },
                    { "name": "contact_value", "from": "addr", "type": "string" }
                  ] },
                { "source": "phones", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "person_id" },
                  "keyBy": { "from": "brand", "pipeline": [
                    { "op": "map", "cases": { "B1": "site-b1", "B2": "site-b2" } } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "phone" } ] } ] },
                    { "name": "contact_value", "from": "number", "type": "string" }
                  ] }
              ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);

        File.WriteAllText(Path.Combine(_dir, "phones.csv"), "person_id,number,brand\n1,555-1212,B1\n");

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var r = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var scoped = r.GetProperty("site_contacts");

        var b1 = scoped.GetProperty("site-b1").EnumerateArray().ToList();
        Assert.AreEqual(2, b1.Count); // one email + one phone, both brand B1
        Assert.IsTrue(b1.Any(c => c.GetProperty("contact_type").GetString() == "email"));
        Assert.IsTrue(b1.Any(c => c.GetProperty("contact_type").GetString() == "phone"));

        var br = scoped.GetProperty("site-b2").EnumerateArray().ToList();
        Assert.AreEqual(1, br.Count); // only the email row is brand B2
        Assert.AreEqual("email", br[0].GetProperty("contact_type").GetString());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void InlineRelationField_ArrayOfObjects_WithDistinctAndLimit(string strategy)
    {
        // An inline relationship field placed among the scalar fields, rendering an array of {name,value}.
        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "client_ids", "type": "array", "source": "emails",
                  "on": { "parent": "id", "child": "person_id" },
                  "distinctBy": "email_key", "limit": 1,
                  "fields": [
                    { "name": "name",  "type": "string", "pipeline": [ { "op": "concat", "values": [ { "literal": "ext_id" } ] } ] },
                    { "name": "value", "from": "addr", "type": "string" }
                  ] },
                { "name": "name", "from": "name", "type": "string" }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        // client_ids renders between id and name (config order), as an array of objects
        var keys = d.EnumerateObject().Select(p => p.Name).ToList();
        Assert.IsTrue(keys.IndexOf("client_ids") > keys.IndexOf("id") && keys.IndexOf("client_ids") < keys.IndexOf("name"));

        var ids = d.GetProperty("client_ids").EnumerateArray().ToList();
        Assert.AreEqual(1, ids.Count); // both email rows share email_key EK1 -> distinct -> 1
        Assert.AreEqual("ext_id", ids[0].GetProperty("name").GetString());
        Assert.AreEqual("alice@e.com", ids[0].GetProperty("value").GetString());
    }

    private const string CoalesceConfig = """
    {
      "run": { "joinStrategy": "<STRATEGY>" },
      "sources": {
        "orders": { "file": "orders.csv", "fields": { "customer_key": { "type": "integer" } } },
        "phones": { "file": "customer_phones.csv", "fields": { "customer_key": { "type": "integer" } } }
      },
      "outputs": [
        { "name": "orders", "mode": "object", "source": "orders",
          "fields": [
            { "name": "order_id", "from": "order_id", "type": "string" },
            { "name": "contact_value", "type": "string",
              "coalesce": [
                { "from": "email_address" },
                { "lookup": { "source": "phones", "on": { "parent": "customer_key", "child": "customer_key" }, "column": "phone_number" } }
              ] },
            { "name": "contact_type", "type": "string", "coalesceEmit": "label",
              "coalesce": [
                { "from": "email_address", "label": "email" },
                { "lookup": { "source": "phones", "on": { "parent": "customer_key", "child": "customer_key" }, "column": "phone_number" }, "label": "phone" }
              ] }
          ],
          "load": { "fileBaseName": "orders", "splitEvery": 1000000 } }
      ]
    }
    """;

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void Coalesce_PrefersColumnThenFallsBackToLookup_LabelsWhichWon(string strategy)
    {
        File.WriteAllText(Path.Combine(_dir, "orders.csv"),
            "order_id,customer_key,email_address\n1,10,alice@x.com\n2,20,\n3,30,\n");
        File.WriteAllText(Path.Combine(_dir, "customer_phones.csv"), "customer_key,phone_number\n20,555-2222\n");

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(CoalesceConfig.Replace("<STRATEGY>", strategy)), _dir, _out, "ts");
        var rows = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("order_id").GetString()!);

        // Order 1: email present on the row -> wins over the lookup.
        Assert.AreEqual("alice@x.com", rows["1"].GetProperty("contact_value").GetString());
        Assert.AreEqual("email", rows["1"].GetProperty("contact_type").GetString());

        // Order 2: email empty -> falls back to the phone lookup.
        Assert.AreEqual("555-2222", rows["2"].GetProperty("contact_value").GetString());
        Assert.AreEqual("phone", rows["2"].GetProperty("contact_type").GetString());

        // Order 3: neither candidate has a value -> both fields omitted (null is never emitted).
        Assert.IsFalse(rows["3"].TryGetProperty("contact_value", out _));
        Assert.IsFalse(rows["3"].TryGetProperty("contact_type", out _));
    }

    [TestMethod]
    public void Coalesce_CandidateWithBothFromAndLookup_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "orders.csv"), "order_id,customer_key,email_address\n1,10,a@x.com\n");
        File.WriteAllText(Path.Combine(_dir, "customer_phones.csv"), "customer_key,phone_number\n10,555-1111\n");
        var config = """
        {
          "sources": {
            "orders": { "file": "orders.csv" },
            "phones": { "file": "customer_phones.csv" }
          },
          "outputs": [
            { "name": "orders", "mode": "object", "source": "orders",
              "fields": [
                { "name": "contact_value", "type": "string",
                  "coalesce": [
                    { "from": "email_address", "lookup": { "source": "phones", "on": { "parent": "customer_key", "child": "customer_key" }, "column": "phone_number" } }
                  ] }
              ],
              "load": { "fileBaseName": "orders" } }
          ]
        }
        """;
        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(
            () => EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"));
    }

    [TestMethod]
    public void Coalesce_LabelEmitWithoutEveryCandidateLabeled_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "orders.csv"), "order_id,customer_key,email_address\n1,10,a@x.com\n");
        File.WriteAllText(Path.Combine(_dir, "customer_phones.csv"), "customer_key,phone_number\n10,555-1111\n");
        var config = """
        {
          "sources": {
            "orders": { "file": "orders.csv" },
            "phones": { "file": "customer_phones.csv" }
          },
          "outputs": [
            { "name": "orders", "mode": "object", "source": "orders",
              "fields": [
                { "name": "contact_type", "type": "string", "coalesceEmit": "label",
                  "coalesce": [
                    { "from": "email_address", "label": "email" },
                    { "lookup": { "source": "phones", "on": { "parent": "customer_key", "child": "customer_key" }, "column": "phone_number" } }
                  ] }
              ],
              "load": { "fileBaseName": "orders" } }
          ]
        }
        """;
        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(
            () => EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"));
    }

    [TestMethod]
    public void InlineRelationField_Filter_ExcludesNonMatchingRows()
    {
        // emails.csv (from Setup): person_id 1 has both a B1 and a B2 row with the same address.
        // Filtering the inline array to brand=B1 should keep only the B1 one.
        var config = """
        {
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "emails": { "file": "emails.csv", "fields": { "person_id": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "b1_emails", "type": "array", "source": "emails",
                  "on": { "parent": "id", "child": "person_id" },
                  "filter": [ { "column": "brand", "op": "eq", "value": "B1" } ],
                  "fields": [ { "name": "value", "from": "addr", "type": "string" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        Assert.AreEqual(1, d.GetProperty("b1_emails").GetArrayLength());
    }

    [TestMethod]
    public void FieldLookups_UnionsExtraLookupsIntoPrimaryArray_Deduped()
    {
        // Primary lookup: per-brand site ids from "brands". Extra lookups: a conditional constant
        // ("global") that only appears when a qualifying US row exists in "emails" or "phones" — two
        // different sources that could both independently produce it, which must collapse to one copy.
        File.WriteAllText(Path.Combine(_dir, "people2.csv"), "id\n1\n2\n3\n");
        File.WriteAllText(Path.Combine(_dir, "brands.csv"), "customer_key,brand\n1,B1\n1,B2\n2,B4\n");
        File.WriteAllText(Path.Combine(_dir, "emails2.csv"), "customer_key,market\n1,US\n3,US\n");
        File.WriteAllText(Path.Combine(_dir, "phones2.csv"), "customer_key,market\n1,US\n");

        var config = """
        {
          "sources": {
            "people2": { "file": "people2.csv", "fields": { "id": { "type": "integer" } } },
            "brands": { "file": "brands.csv", "fields": { "customer_key": { "type": "integer" } } },
            "emails2": { "file": "emails2.csv", "fields": { "customer_key": { "type": "integer" } } },
            "phones2": { "file": "phones2.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people2", "mode": "object", "source": "people2",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array",
                  "lookup": {
                    "source": "brands", "on": { "parent": "id", "child": "customer_key" }, "distinct": true,
                    "expr": [ { "op": "concat", "values": [ { "literal": "site-" }, { "field": "brand" } ] } ]
                  },
                  "lookups": [
                    { "source": "emails2", "on": { "parent": "id", "child": "customer_key" }, "distinct": true,
                      "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                      "expr": [ { "op": "literal", "default": "global" } ] },
                    { "source": "phones2", "on": { "parent": "id", "child": "customer_key" }, "distinct": true,
                      "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                      "expr": [ { "op": "literal", "default": "global" } ] }
                  ] }
              ],
              "children": [],
              "load": { "fileBaseName": "people2", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var rows = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("id").GetInt64());

        // Customer 1: two brand-derived ids + "global" once (from BOTH emails and phones, deduped).
        var ids1 = rows[1].GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "site-B1", "site-B2", "global" }, ids1);

        // Customer 2: only the brand-derived id; no qualifying email/phone row at all.
        var ids2 = rows[2].GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "site-B4" }, ids2);

        // Customer 3: no brand row (primary lookup empty), but a qualifying email -> "global" alone.
        var ids3 = rows[3].GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "global" }, ids3);
    }

    [TestMethod]
    public void KeyBy_MapWithFrom_TranslatesCodeAndDropsUnmappedWithWarning()
    {
        // Customer 1 has three brand rows: B1 and B4 map to known sites; XX has no case and no default,
        // so that one row must be dropped (not crash the run), with a warning recorded.
        File.WriteAllText(Path.Combine(_dir, "people3.csv"), "id\n1\n2\n");
        File.WriteAllText(Path.Combine(_dir, "brands3.csv"), "customer_key,brand\n1,B1\n1,B4\n1,XX\n2,B1\n");
        var config = """
        {
          "sources": {
            "people3": { "file": "people3.csv", "fields": { "id": { "type": "integer" } } },
            "brands3": { "file": "brands3.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people3", "mode": "object", "source": "people3",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "brands3", "as": "sites", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [
                    { "op": "map", "cases": { "B1": "b1", "B4": "b4" } },
                    { "op": "concat", "values": [ { "literal": "site-" }, { "value": true } ] }
                  ] },
                  "fields": [ { "name": "brand", "from": "brand", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var rows = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("id").GetInt32());

        var sites1 = rows[1].GetProperty("sites");
        CollectionAssert.AreEquivalent(new[] { "site-b1", "site-b4" }, sites1.EnumerateObject().Select(p => p.Name).ToList());
        Assert.IsFalse(rows[2].GetProperty("sites").TryGetProperty("site-xx", out _));

        Assert.AreEqual(1, results[0].Warnings, "the unmapped XX brand must be warned, not silently dropped");
    }

    [TestMethod]
    public void LookupExpr_MapWithFrom_TranslatesCodeAndSkipsUnmappedRowsGracefully()
    {
        // Same idea as the keyBy test, but for a lookup's "expr" (e.g. the site_ids pattern): the
        // lookup source has a row with an unmapped brand, which must not crash index building.
        File.WriteAllText(Path.Combine(_dir, "people4.csv"), "id\n1\n2\n");
        File.WriteAllText(Path.Combine(_dir, "brands4.csv"), "customer_key,brand\n1,B1\n1,XX\n2,B4\n");
        var config = """
        {
          "sources": {
            "people4": { "file": "people4.csv", "fields": { "id": { "type": "integer" } } },
            "brands4": { "file": "brands4.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people4", "mode": "object", "source": "people4",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array",
                  "lookup": {
                    "source": "brands4", "on": { "parent": "id", "child": "customer_key" }, "distinct": true,
                    "from": "brand",
                    "expr": [
                      { "op": "map", "cases": { "B1": "b1", "B4": "b4" } },
                      { "op": "concat", "values": [ { "literal": "site-" }, { "value": true } ] }
                    ]
                  } }
              ],
              "children": [],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var rows = File.ReadAllLines(results[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("id").GetInt32());

        // Customer 1: B1 maps fine; XX has no case/default and must not crash the run — just skipped.
        CollectionAssert.AreEquivalent(new[] { "site-b1" },
            rows[1].GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList());
        CollectionAssert.AreEquivalent(new[] { "site-b4" },
            rows[2].GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_MergesRowsSharingAValue_WithPrefixedProperties()
    {
        // Customer 1 has the same email under B1 and B4 (must merge into one entry, properties prefixed
        // b1_/b4_) plus a third row under an unmapped brand XX (must be dropped from the merge, warned,
        // not fatal), and a second, different email under B1 alone (stays its own entry, b1_ prefixed).
        File.WriteAllText(Path.Combine(_dir, "contacts4.csv"),
            "customer_key,brand,email,flag\n1,B1,alice@x.com,N\n1,B4,alice@x.com,Y\n1,XX,alice@x.com,Q\n1,B1,bob@x.com,M\n");
        var config = """
        {
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "contacts4": { "file": "contacts4.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "contacts4", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "mergePrefix": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "b1", "B4": "b4" } } ] },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "parent-dev" } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object",
                      "fields": [ { "name": "flag", "from": "flag", "type": "string" } ] }
                  ] }
              ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        var entries = d.GetProperty("site_contacts").GetProperty("parent-dev").EnumerateArray().ToList();
        Assert.AreEqual(2, entries.Count, "one merged entry for alice, one plain entry for bob");

        var alice = entries.Single(e => e.GetProperty("contact_value").GetString() == "alice@x.com");
        Assert.AreEqual("email", alice.GetProperty("contact_type").GetString());
        var aliceProps = alice.GetProperty("contact_properties");
        Assert.AreEqual("N", aliceProps.GetProperty("b1_flag").GetString());
        Assert.AreEqual("Y", aliceProps.GetProperty("b4_flag").GetString());
        Assert.IsFalse(aliceProps.TryGetProperty("flag", out _), "unprefixed key must not appear");
        Assert.IsFalse(aliceProps.TryGetProperty("xx_flag", out _), "the unmapped XX row must be dropped from the merge");
        Assert.AreEqual(2, aliceProps.EnumerateObject().Count());

        var bob = entries.Single(e => e.GetProperty("contact_value").GetString() == "bob@x.com");
        var bobProps = bob.GetProperty("contact_properties");
        Assert.AreEqual("M", bobProps.GetProperty("b1_flag").GetString());
        Assert.AreEqual(1, bobProps.EnumerateObject().Count());

        Assert.AreEqual(1, results[0].Warnings, "the unmapped XX brand must be warned when dropped from the merge");
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_NestedRelationRendersOnceFromFirstRow()
    {
        // A relation nested under a merged (MergeBy) entry joins on a column shared by the whole group
        // (contact_value = email), so it must render once, not once per merged row.
        File.WriteAllText(Path.Combine(_dir, "contacts5.csv"),
            "customer_key,brand,email\n1,B1,alice@x.com\n1,B4,alice@x.com\n");
        File.WriteAllText(Path.Combine(_dir, "family5.csv"),
            "loyalty_email,name\nalice@x.com,Kif\nalice@x.com,Summer\nbob@x.com,Nobody\n");
        var config = """
        {
          "sources": {
            "people": { "file": "people.csv", "fields": { "id": { "type": "integer" } } },
            "contacts5": { "file": "contacts5.csv", "fields": { "customer_key": { "type": "integer" } } },
            "family5": { "file": "family5.csv" }
          },
          "outputs": [
            { "name": "people", "mode": "object", "source": "people",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "contacts5", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "mergePrefix": { "from": "brand", "pipeline": [ { "op": "lower" } ] },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "parent-dev" } ] },
                  "fields": [
                    { "name": "contact_value", "from": "email", "type": "string" }
                  ],
                  "children": [
                    { "source": "family5", "as": "family_members", "cardinality": "many",
                      "on": { "parent": "email", "child": "loyalty_email" },
                      "fields": [ { "name": "name", "from": "name", "type": "string" } ] }
                  ] }
              ],
              "load": { "fileBaseName": "people", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        var alice = d.GetProperty("site_contacts").GetProperty("parent-dev").EnumerateArray().Single();
        var members = alice.GetProperty("family_members").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "Kif", "Summer" }, members);
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void CaseInsensitiveJoin_MatchesDespiteDifferentCasing_ButNotWithoutTheFlag(string strategy)
    {
        // Typical case: the join column holds the "same" email with
        // different casing on each side. Off by default (ordinal); the flag makes it match.
        File.WriteAllText(Path.Combine(_dir, "people6.csv"), "id,email\n1,Alice@Example.com\n2,bob@example.com\n");
        File.WriteAllText(Path.Combine(_dir, "family6.csv"), "loyalty_email,name\nalice@example.com,Kid1\nBOB@EXAMPLE.COM,Kid2\n");

        string Config(bool caseInsensitive) => $$"""
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people6": { "file": "people6.csv", "fields": { "id": { "type": "integer" } } },
            "family6": { "file": "family6.csv" }
          },
          "outputs": [
            { "name": "people6", "mode": "object", "source": "people6",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "family6", "as": "family_members", "cardinality": "many",
                  "caseInsensitiveJoin": {{(caseInsensitive ? "true" : "false")}},
                  "on": { "parent": "email", "child": "loyalty_email" },
                  "fields": [ { "name": "name", "from": "name", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "people6", "splitEvery": 1000000 } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);

        var resultsSensitive = EtlEngine.Run(EtlConfigLoader.FromJson(Config(false)), _dir, _out, "ts1");
        var caseSensitive = File.ReadAllLines(resultsSensitive[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.IsFalse(caseSensitive[1].TryGetProperty("family_members", out _), "ordinal comparison must not match differently-cased emails");
        Assert.IsFalse(caseSensitive[2].TryGetProperty("family_members", out _));

        var resultsInsensitive = EtlEngine.Run(EtlConfigLoader.FromJson(Config(true)), _dir, _out, "ts2");
        var caseInsensitive = File.ReadAllLines(resultsInsensitive[0].Files[0]).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.AreEqual("Kid1", caseInsensitive[1].GetProperty("family_members").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.AreEqual("Kid2", caseInsensitive[2].GetProperty("family_members").EnumerateArray().Single().GetProperty("name").GetString());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedRelation_NestedChild_BrandScopedDynamicButUnscopedUnderMergedParent(string strategy)
    {
        // Typical shape: one contact shared across two brands (B3, B4) with a matching
        // family member each. The per-brand dynamic site should only see its own brand's family member;
        // the merged parent-dev site (unscoped, single-column join) should see both.
        File.WriteAllText(Path.Combine(_dir, "people7.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "contacts7.csv"), "customer_key,brand,email\n1,B3,shared@x.com\n1,B4,shared@x.com\n");
        File.WriteAllText(Path.Combine(_dir, "family7.csv"), "brand,loyalty_email,name\nB3,shared@x.com,Christine\nB4,shared@x.com,Jeremiah\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people7":  { "file": "people7.csv",  "fields": { "id": { "type": "integer" } } },
            "contacts7": { "file": "contacts7.csv", "fields": { "customer_key": { "type": "integer" } } },
            "family7": { "file": "family7.csv" }
          },
          "outputs": [
            { "name": "people7", "mode": "object", "source": "people7",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "contacts7", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [ { "op": "concat", "values": [ { "literal": "site-" }, { "value": true } ] } ] },
                  "fields": [ { "name": "contact_value", "from": "email", "type": "string" } ],
                  "children": [
                    { "source": "family7", "as": "household_member", "cardinality": "many",
                      "on": { "parents": [ "email", "brand" ], "children": [ "loyalty_email", "brand" ] },
                      "fields": [ { "name": "name", "from": "name", "type": "string" } ] }
                  ] },
                { "source": "contacts7", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "parent-dev" } ] },
                  "fields": [ { "name": "contact_value", "from": "email", "type": "string" } ],
                  "children": [
                    { "source": "family7", "as": "household_member", "cardinality": "many",
                      "on": { "parent": "email", "child": "loyalty_email" },
                      "fields": [ { "name": "name", "from": "name", "type": "string" } ] }
                  ] }
              ],
              "load": { "fileBaseName": "people7", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var sc = d.GetProperty("site_contacts");

        var b3Site = sc.GetProperty("site-B3").EnumerateArray().Single();
        CollectionAssert.AreEquivalent(new[] { "Christine" },
            b3Site.GetProperty("household_member").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList());

        var b4Site = sc.GetProperty("site-B4").EnumerateArray().Single();
        CollectionAssert.AreEquivalent(new[] { "Jeremiah" },
            b4Site.GetProperty("household_member").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList());

        var parentDev = sc.GetProperty("parent-dev").EnumerateArray().Single();
        CollectionAssert.AreEquivalent(new[] { "Christine", "Jeremiah" },
            parentDev.GetProperty("household_member").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedRelation_MergeBy_AcrossDifferentlyShapedSources_MergesFieldsAndKeepsNestedRelation(string strategy)
    {
        // Two DIFFERENT source relations (different contact_properties fields; only one has a nested
        // relation) sharing the same site key and the same mergeBy value must merge into ONE entry with
        // BOTH sources' properties and the nested relation preserved — regardless of which row lands
        // first in the group. The profile-style source (no nested relation, different fields) is declared
        // FIRST here specifically to catch a "first row's shape wins" regression.
        File.WriteAllText(Path.Combine(_dir, "people8.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "email8.csv"), "customer_key,email,email_key\n1,shared@x.com,EK1\n");
        File.WriteAllText(Path.Combine(_dir, "profile8.csv"), "customer_key,email,seg_seg\n1,shared@x.com,SEG1\n");
        File.WriteAllText(Path.Combine(_dir, "family8.csv"), "loyalty_email,name\nshared@x.com,Kid1\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people8": { "file": "people8.csv", "fields": { "id": { "type": "integer" } } },
            "email8":  { "file": "email8.csv",   "fields": { "customer_key": { "type": "integer" } } },
            "amp8":    { "file": "profile8.csv",     "fields": { "customer_key": { "type": "integer" } } },
            "family8": { "file": "family8.csv" }
          },
          "outputs": [
            { "name": "people8", "mode": "object", "source": "people8",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "amp8", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "site1" } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object",
                      "fields": [ { "name": "seg_seg", "from": "seg_seg", "type": "string" } ] }
                  ] },
                { "source": "email8", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "site1" } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object",
                      "fields": [ { "name": "email_key", "from": "email_key", "type": "string" } ] }
                  ],
                  "children": [
                    { "source": "family8", "as": "household_member", "cardinality": "many",
                      "on": { "parent": "email", "child": "loyalty_email" },
                      "fields": [ { "name": "name", "from": "name", "type": "string" } ] }
                  ] }
              ],
              "load": { "fileBaseName": "people8", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var entry = d.GetProperty("site_contacts").GetProperty("site1").EnumerateArray().Single();

        Assert.AreEqual("email", entry.GetProperty("contact_type").GetString());
        Assert.AreEqual("shared@x.com", entry.GetProperty("contact_value").GetString());

        var props = entry.GetProperty("contact_properties");
        Assert.AreEqual("EK1", props.GetProperty("email_key").GetString());
        Assert.AreEqual("SEG1", props.GetProperty("seg_seg").GetString());
        Assert.AreEqual(2, props.EnumerateObject().Count());

        Assert.IsTrue(entry.TryGetProperty("household_member", out var fam), "the nested relation from the email8 row must survive even though amp8's row came first");
        Assert.AreEqual("Kid1", fam.EnumerateArray().Single().GetProperty("name").GetString());
    }

    [TestMethod]
    [DataRow("hashInMemory")]
    [DataRow("externalMerge")]
    public void KeyedRelation_MergeBy_DuplicateRowsFromSameSourceAndPrefix_WriteEachKeyOnce(string strategy)
    {
        // A source with no distinctBy of its own (e.g. a profile snapshot) has TWO rows for the
        // same customer+email (a repeated/historical snapshot) — merging must not write "seg_seg" twice
        // (the second occurrence would silently clobber the first in the JSON, and some readers reject
        // duplicate keys outright). Keep the first row for that (source, prefix) pair.
        File.WriteAllText(Path.Combine(_dir, "people9.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "profile9.csv"), "customer_key,email,seg_seg\n1,shared@x.com,FIRST\n1,shared@x.com,SECOND\n");

        var config = """
        {
          "run": { "joinStrategy": "<STRATEGY>" },
          "sources": {
            "people9": { "file": "people9.csv", "fields": { "id": { "type": "integer" } } },
            "amp9":    { "file": "profile9.csv",     "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "people9", "mode": "object", "source": "people9",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "amp9", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "mergeBy": "email",
                  "mergePrefix": { "pipeline": [ { "op": "literal", "default": "b1" } ] },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "site1" } ] },
                  "fields": [
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object",
                      "fields": [ { "name": "seg_seg", "from": "seg_seg", "type": "string" } ] }
                  ] }
              ],
              "load": { "fileBaseName": "people9", "splitEvery": 1000000, "sampling": { "enabled": false } } }
          ]
        }
        """.Replace("<STRATEGY>", strategy);

        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var entry = d.GetProperty("site_contacts").GetProperty("site1").EnumerateArray().Single();
        var props = entry.GetProperty("contact_properties");

        Assert.AreEqual(1, props.EnumerateObject().Count(p => p.Name == "b1_seg_seg"), "b1_seg_seg must appear exactly once, not once per duplicate row");
        Assert.AreEqual("FIRST", props.GetProperty("b1_seg_seg").GetString());
    }

    [TestMethod]
    public void InlineRelationField_FilterAndDistinctByTogether_BothApply()
    {
        // Regression test: Filter()'s distinctBy branch once re-derived from the raw "children" list
        // instead of the already-filtered "seq", silently discarding the filter whenever both were set
        // on the same relation. A CA-market row must not leak through just because distinctBy is also
        // configured on this field.
        File.WriteAllText(Path.Combine(_dir, "peopleA.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "contactsA.csv"), "customer_key,email,market\n1,ca@x.com,CA\n1,us@x.com,US\n");
        var config = """
        {
          "sources": {
            "peopleA": { "file": "peopleA.csv", "fields": { "id": { "type": "integer" } } },
            "contactsA": { "file": "contactsA.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleA", "mode": "object", "source": "peopleA",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "external_ids", "type": "array", "source": "contactsA",
                  "on": { "parent": "id", "child": "customer_key" },
                  "distinctBy": "email",
                  "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                  "fields": [ { "name": "value", "from": "email", "type": "string" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "peopleA", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var ids = d.GetProperty("external_ids").EnumerateArray().Select(e => e.GetProperty("value").GetString()).ToList();

        CollectionAssert.AreEquivalent(new[] { "us@x.com" }, ids, "the CA row must be filtered out even though distinctBy is also set");
    }

    [TestMethod]
    public void SnakeCase_LowercasesAndReplacesNonAlphanumericRunsWithUnderscore()
    {
        File.WriteAllText(Path.Combine(_dir, "modelsA.csv"), "id,model_name\n1,Model One\n2,Model_Two\n");
        var config = """
        {
          "sources": { "modelsA": { "file": "modelsA.csv", "fields": { "id": { "type": "integer" } } } },
          "outputs": [
            { "name": "modelsA", "mode": "object", "source": "modelsA",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "key", "from": "model_name", "type": "string", "pipeline": [ { "op": "snakeCase" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "modelsA", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]);
        var keys = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("key").GetString()).ToList();

        CollectionAssert.AreEquivalent(new[] { "model_one", "model_two" }, keys);
    }

    [TestMethod]
    public void KeyedSingle_BestOf_KeepsChildWithSmallestValue_NotJustFirstSeen()
    {
        // Regression scenario for model details: the SAME (customer, model_name) key can have
        // multiple rows (e.g. a model emitting one row per ranked category); when
        // keyed as a single object, keyedSingleBestOf should keep the best-ranked (lowest rank_overall)
        // row, not just whichever happened to be joined first.
        File.WriteAllText(Path.Combine(_dir, "peopleB.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "modelsB.csv"),
            "customer_key,model_name,rank_overall,placeholder1\n1,Model One,22,X\n1,Model One,19,Y\n");
        var config = """
        {
          "sources": {
            "peopleB": { "file": "peopleB.csv", "fields": { "id": { "type": "integer" } } },
            "modelsB": { "file": "modelsB.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleB", "mode": "object", "source": "peopleB",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "modelsB", "as": "customer_model_details", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "model_name", "pipeline": [ { "op": "snakeCase" } ] },
                  "keyedSingle": true,
                  "keyedSingleBestOf": "rank_overall",
                  "fields": [
                    { "name": "rank_overall", "from": "rank_overall", "type": "integer" },
                    { "name": "placeholder1", "from": "placeholder1", "type": "string" }
                  ] }
              ],
              "load": { "fileBaseName": "peopleB", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var winner = d.GetProperty("customer_model_details").GetProperty("model_one");

        Assert.AreEqual(19, winner.GetProperty("rank_overall").GetInt32(), "the lower rank_overall (better-ranked) row should win, not the first-seen one");
        Assert.AreEqual("Y", winner.GetProperty("placeholder1").GetString());
    }

    [TestMethod]
    public void RichSampling_KeepsTheMostFullyPopulatedRecords_NotARandomSample()
    {
        // Three customers: one with no nested relation populated, one with one, one with two. With
        // richSampling count=1, only the record with both nested relations should be written out.
        File.WriteAllText(Path.Combine(_dir, "peopleC.csv"), "id,name\n1,Sparse\n2,Medium\n3,Rich\n");
        File.WriteAllText(Path.Combine(_dir, "cardsC.csv"), "customer_key,card_id\n2,C1\n3,C2\n");
        File.WriteAllText(Path.Combine(_dir, "loyaltyC.csv"), "customer_key,member_id\n3,L1\n");
        var config = """
        {
          "sources": {
            "peopleC": { "file": "peopleC.csv", "fields": { "id": { "type": "integer" } } },
            "cardsC": { "file": "cardsC.csv", "fields": { "customer_key": { "type": "integer" } } },
            "loyaltyC": { "file": "loyaltyC.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleC", "mode": "object", "source": "peopleC",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "name", "from": "name", "type": "string" }
              ],
              "children": [
                { "source": "cardsC", "as": "payment_cards", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "fields": [ { "name": "card_id", "from": "card_id", "type": "string" } ] },
                { "source": "loyaltyC", "as": "household_member", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "fields": [ { "name": "member_id", "from": "member_id", "type": "string" } ] }
              ],
              "load": { "fileBaseName": "peopleC", "splitEvery": 1000000,
                "sampling": { "enabled": false },
                "richSampling": { "enabled": true, "count": 1, "folder": "sampling/full" } } }
          ]
        }
        """;
        EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var richDir = Path.Combine(_out, "run_ts", "sampling", "full");
        var files = Directory.GetFiles(richDir, "*.json");

        Assert.AreEqual(1, files.Length, "only the single richest record should be written");
        var kept = JsonDocument.Parse(File.ReadAllText(files[0])).RootElement;
        Assert.AreEqual("Rich", kept.GetProperty("name").GetString());
    }

    [TestMethod]
    public void NumberFormatThenToDecimal_RoundsToTheConfiguredScale()
    {
        // A "decimal" field whose source has more precision than the target schema allows (e.g. a model
        // score column with 15+ decimal digits feeding a DECIMAL(10,3) column) can be rounded by chaining
        // numberFormat (format as text to N places) -> toDecimal (parse it back) in the field's pipeline.
        File.WriteAllText(Path.Combine(_dir, "scoresD.csv"), "id,score\n1,0.123456789\n2,0.0077777\n");
        var config = """
        {
          "sources": { "scoresD": { "file": "scoresD.csv", "fields": { "id": { "type": "integer" } } } },
          "outputs": [
            { "name": "scoresD", "mode": "object", "source": "scoresD",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "score", "from": "score", "type": "decimal",
                  "pipeline": [ { "op": "numberFormat", "format": "F3" }, { "op": "toDecimal" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "scoresD", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]);
        var scores = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("score").GetDecimal()).OrderBy(x => x).ToList();

        CollectionAssert.AreEqual(new[] { 0.008m, 0.123m }, scores);
    }

    [TestMethod]
    public void EmitNull_WritesExplicitJsonNull_InsteadOfOmittingTheField()
    {
        File.WriteAllText(Path.Combine(_dir, "peopleE.csv"), "id\n1\n");
        var config = """
        {
          "sources": { "peopleE": { "file": "peopleE.csv", "fields": { "id": { "type": "integer" } } } },
          "outputs": [
            { "name": "peopleE", "mode": "object", "source": "peopleE",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "properties", "type": "string", "emitNull": true, "pipeline": [ { "op": "literal" } ] }
              ],
              "children": [],
              "load": { "fileBaseName": "peopleE", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        Assert.IsTrue(d.TryGetProperty("properties", out var properties), "the field must be present, not omitted");
        Assert.AreEqual(JsonValueKind.Null, properties.ValueKind, "the value must be a literal JSON null, not a string or omitted");
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_LoyaltyRowsMergeIntoMatchingContact_NotStandaloneEntries()
    {
        // Regression scenario for "loyalty_details into site_contacts": a loyalty row must MERGE into
        // the existing email-keyed contact sharing its (site, contact_value) — via mergeBy, same as
        // other contact sources merging into "email"'s contacts — not render as its own standalone
        // array entry. Three behaviors under test:
        //  1) A loyalty row whose email matches an existing brand-scoped contact merges into it.
        //  2) A loyalty row with no matching contact still creates one (its own contact_type/value).
        //  3) At parent-dev, two different brands' loyalty rows sharing one email merge into ONE contact,
        //     with contact_properties prefixed per brand (B1O -> b1fs) so their loyalty_id keys don't collide.
        File.WriteAllText(Path.Combine(_dir, "peopleG.csv"), "id\n1\n2\n");
        File.WriteAllText(Path.Combine(_dir, "emailsG.csv"), "customer_key,email,brand\n1,a@x.com,B1\n");
        File.WriteAllText(Path.Combine(_dir, "loyaltyG.csv"),
            "customer_key,loyalty_id,loyalty_brand,market,loyalty_email\n" +
            "1,LB1,B1,US,a@x.com\n" +   // merges into the existing B1 email contact at b1-dev
            "1,LB1O,B1O,US,b@x.com\n" +   // no existing b1fs-dev contact for b@x.com -> creates its own
            "1,LB12,B1,US,b@x.com\n" +  // shares b@x.com with LB1O -> both merge at parent-dev, prefixed
            "2,LB13,B1,US,a@x.com\n");  // customer 2 has no email source row at all -> creates its own
        var config = """
        {
          "sources": {
            "peopleG": { "file": "peopleG.csv", "fields": { "id": { "type": "integer" } } },
            "emailsG": { "file": "emailsG.csv", "fields": { "customer_key": { "type": "integer" } } },
            "loyaltyG": { "file": "loyaltyG.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleG", "mode": "object", "source": "peopleG",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "emailsG", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "example-us-b1-dev" } } ] },
                  "mergeBy": "email",
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" }
                  ] },
                { "source": "loyaltyG", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "loyalty_brand", "pipeline": [
                    { "op": "map", "cases": { "B1": "example-us-b1-dev", "B1O": "example-us-b1fs-dev" } }
                  ] },
                  "mergeBy": "loyalty_email",
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "loyalty_email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "loyalty_id", "from": "loyalty_id", "type": "string" }
                    ] }
                  ] },
                { "source": "loyaltyG", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "example-us-parent-dev" } ] },
                  "mergeBy": "loyalty_email",
                  "mergePrefix": { "from": "loyalty_brand", "pipeline": [ { "op": "map", "cases": { "B1": "b1", "B1O": "b1fs" } } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "loyalty_email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "loyalty_id", "from": "loyalty_id", "type": "string" }
                    ] }
                  ] }
              ],
              "load": { "fileBaseName": "peopleG", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]).Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(e => e.GetProperty("id").GetInt32());

        var customer1 = lines[1].GetProperty("site_contacts");

        // 1) LB1 (brand B1, email a@x.com) merges into the EXISTING B1 email contact at b1-dev, rather
        // than adding a second standalone entry for the same contact_value. LB12 is also brand B1 but a
        // different email (b@x.com), so it correctly creates its OWN separate contact at b1-dev: two
        // distinct contact_values, not two entries for the same one.
        var b1DevSite = customer1.GetProperty("example-us-b1-dev").EnumerateArray().ToList();
        Assert.AreEqual(2, b1DevSite.Count, "a@x.com (merged) and b@x.com (LB12, its own contact) are distinct contact_values");
        var aAtB1Dev = b1DevSite.Single(e => e.GetProperty("contact_value").GetString() == "a@x.com");
        Assert.AreEqual(1, b1DevSite.Count(e => e.GetProperty("contact_value").GetString() == "a@x.com"), "LB1 must merge into the existing a@x.com contact, not add a second entry for it");
        Assert.AreEqual("email", aAtB1Dev.GetProperty("contact_type").GetString());
        Assert.AreEqual("LB1", aAtB1Dev.GetProperty("contact_properties").GetProperty("loyalty_id").GetString());

        // 2) LB1O has no matching b1fs-dev contact, so it creates its own (its own contact_type/value).
        var b1fsSite = customer1.GetProperty("example-us-b1fs-dev").EnumerateArray().ToList();
        Assert.AreEqual(1, b1fsSite.Count);
        Assert.AreEqual("b@x.com", b1fsSite[0].GetProperty("contact_value").GetString());
        Assert.AreEqual("LB1O", b1fsSite[0].GetProperty("contact_properties").GetProperty("loyalty_id").GetString());

        // 3) At parent-dev, LB1O (brand B1O) and LB12 (brand B1) share b@x.com and must merge into ONE
        // contact, with contact_properties prefixed per brand so "loyalty_id" doesn't collide.
        var parentDev = customer1.GetProperty("example-us-parent-dev").EnumerateArray().ToList();
        var bContact = parentDev.Single(e => e.GetProperty("contact_value").GetString() == "b@x.com");
        var bProps = bContact.GetProperty("contact_properties");
        Assert.AreEqual("LB1O", bProps.GetProperty("b1fs_loyalty_id").GetString());
        Assert.AreEqual("LB12", bProps.GetProperty("b1_loyalty_id").GetString());
        Assert.IsFalse(bProps.TryGetProperty("loyalty_id", out _), "unprefixed 'loyalty_id' must not appear once brand-prefixed keys are used");

        var aContact = parentDev.Single(e => e.GetProperty("contact_value").GetString() == "a@x.com");
        Assert.AreEqual("LB1", aContact.GetProperty("contact_properties").GetProperty("b1_loyalty_id").GetString());

        // Customer 2 has no email source row at all; the loyalty row still creates its own contact.
        var customer2 = lines[2].GetProperty("site_contacts");
        var customer2B1Dev = customer2.GetProperty("example-us-b1-dev").EnumerateArray().ToList();
        Assert.AreEqual(1, customer2B1Dev.Count);
        Assert.AreEqual("LB13", customer2B1Dev[0].GetProperty("contact_properties").GetProperty("loyalty_id").GetString());
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_NestedArrayFieldInsideContactProperties_EnrichesOnly_NeverCreates()
    {
        // Regression scenario for "payment_cards into site_contacts.contact_properties": a plain
        // inline array field (like payment_cards/loyalty_details at the root) can now be nested
        // INSIDE a merged contact_properties GroupItem too, joined against the contributing row's own
        // columns. Three behaviors under test:
        //  1) At the dynamic (unprefixed) site, it enriches the existing contact with matching cards.
        //  2) At parent-dev, the SAME nested field renders under its plain name (never brand-prefixed) —
        //     one array per merged contact; see KeyedRelation_MergeBy_NestedArray_ParentUnionsDistinctCards.
        //  3) A card whose loyalty_email_address matches NO contact anywhere creates nothing (enrich-only).
        File.WriteAllText(Path.Combine(_dir, "peopleH.csv"), "id\n1\n2\n");
        File.WriteAllText(Path.Combine(_dir, "emailsH.csv"), "customer_key,email,brand\n1,a@x.com,B1\n");
        File.WriteAllText(Path.Combine(_dir, "cardsH.csv"),
            "customer_key,loyalty_email_address,card_last_4,market\n" +
            "1,a@x.com,1111,US\n" +   // matches -> should enrich both b1-dev and parent-dev's a@x.com contact
            "1,a@x.com,2222,CA\n" +   // wrong market -> filtered out, must not appear
            "2,zzz@nowhere.com,9999,US\n"); // no matching contact anywhere -> must create nothing
        var config = """
        {
          "sources": {
            "peopleH": { "file": "peopleH.csv", "fields": { "id": { "type": "integer" } } },
            "emailsH": { "file": "emailsH.csv", "fields": { "customer_key": { "type": "integer" } } },
            "cardsH": { "file": "cardsH.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleH", "mode": "object", "source": "peopleH",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "emailsH", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "example-us-b1-dev" } } ] },
                  "mergeBy": "email",
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "payment_cards", "type": "array", "source": "cardsH",
                        "on": { "parent": "email", "child": "loyalty_email_address" },
                        "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                        "fields": [ { "name": "card_last_4", "from": "card_last_4", "type": "string" } ] }
                    ] }
                  ] },
                { "source": "emailsH", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "example-us-parent-dev" } ] },
                  "mergeBy": "email",
                  "mergePrefix": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "b1" } } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "payment_cards", "type": "array", "source": "cardsH",
                        "on": { "parent": "email", "child": "loyalty_email_address" },
                        "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                        "fields": [ { "name": "card_last_4", "from": "card_last_4", "type": "string" } ] }
                    ] }
                  ] }
              ],
              "load": { "fileBaseName": "peopleH", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var lines = File.ReadAllLines(results[0].Files[0]).Select(l => JsonDocument.Parse(l).RootElement).ToDictionary(e => e.GetProperty("id").GetInt32());

        var customer1 = lines[1].GetProperty("site_contacts");

        // 1) Dynamic site: unprefixed "payment_cards", only the US card, not the CA one.
        var b1DevContact = customer1.GetProperty("example-us-b1-dev").EnumerateArray().Single();
        var cards = b1DevContact.GetProperty("contact_properties").GetProperty("payment_cards").EnumerateArray().ToList();
        Assert.AreEqual(1, cards.Count, "only the US-market card should be included, the CA one is filtered out");
        Assert.AreEqual("1111", cards[0].GetProperty("card_last_4").GetString());

        // 2) parent-dev: plain "payment_cards", never "b1_payment_cards".
        var parentDevContact = customer1.GetProperty("example-us-parent-dev").EnumerateArray().Single();
        var props = parentDevContact.GetProperty("contact_properties");
        Assert.IsFalse(props.TryGetProperty("b1_payment_cards", out _), "a nested array must never be brand-prefixed");
        var parentCards = props.GetProperty("payment_cards").EnumerateArray().ToList();
        Assert.AreEqual(1, parentCards.Count);
        Assert.AreEqual("1111", parentCards[0].GetProperty("card_last_4").GetString());

        // 3) Customer 2's card matches no contact anywhere; nothing should be created for them at all.
        Assert.IsFalse(lines[2].TryGetProperty("site_contacts", out var c2Contacts) && c2Contacts.EnumerateObject().Any(),
            "a card with no matching contact must not create a standalone contact (enrich-only)");
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_NestedArray_ParentUnionsDistinctCards()
    {
        // One email shared by B1 and B4 rows merges into ONE parent-dev contact. Its payment_cards
        // must be a single unprefixed array holding every distinct card (by external_card_account_id)
        // matching the email case-insensitively — not b1_payment_cards + b4_payment_cards.
        File.WriteAllText(Path.Combine(_dir, "peopleU.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "emailsU.csv"),
            "customer_key,email,brand,email_key\n" +
            "1,a@x.com,B1,k-b1\n" +
            "1,a@x.com,B4,k-b4\n");
        File.WriteAllText(Path.Combine(_dir, "cardsU.csv"),
            "customer_key,loyalty_email_address,external_card_account_id,card_last_4,market\n" +
            "1,a@x.com,acct-1,1111,US\n" +
            "1,A@X.COM,acct-2,2222,US\n" +   // different case -> still matches
            "1,a@x.com,acct-1,3333,US\n" +   // same account as the first -> deduped away
            "1,a@x.com,acct-4,4444,CA\n");   // wrong market -> filtered out
        var config = """
        {
          "sources": {
            "peopleU": { "file": "peopleU.csv", "fields": { "id": { "type": "integer" } } },
            "emailsU": { "file": "emailsU.csv", "fields": { "customer_key": { "type": "integer" } } },
            "cardsU": { "file": "cardsU.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleU", "mode": "object", "source": "peopleU",
              "fields": [ { "name": "id", "from": "id", "type": "integer" } ],
              "children": [
                { "source": "emailsU", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "example-us-parent-dev" } ] },
                  "mergeBy": "email",
                  "mergePrefix": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B1": "b1", "B4": "b4" } } ] },
                  "fields": [
                    { "name": "contact_value", "from": "email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "email_key", "from": "email_key", "type": "string" },
                      { "name": "payment_cards", "type": "array", "source": "cardsU",
                        "on": { "parent": "email", "child": "loyalty_email_address" },
                        "caseInsensitiveJoin": true,
                        "distinctBy": "external_card_account_id",
                        "filter": [ { "column": "market", "op": "eq", "value": "US" } ],
                        "fields": [ { "name": "card_last_4", "from": "card_last_4", "type": "string" } ] }
                    ] }
                  ] }
              ],
              "load": { "fileBaseName": "peopleU", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var record = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0]).Single()).RootElement;

        var contact = record.GetProperty("site_contacts").GetProperty("example-us-parent-dev").EnumerateArray().Single();
        var props = contact.GetProperty("contact_properties");

        // Scalar fields are still brand-prefixed.
        Assert.AreEqual("k-b1", props.GetProperty("b1_email_key").GetString());
        Assert.AreEqual("k-b4", props.GetProperty("b4_email_key").GetString());

        // The nested array is not.
        Assert.IsFalse(props.TryGetProperty("b1_payment_cards", out _));
        Assert.IsFalse(props.TryGetProperty("b4_payment_cards", out _));
        var cards = props.GetProperty("payment_cards").EnumerateArray()
            .Select(c => c.GetProperty("card_last_4").GetString()).ToList();
        CollectionAssert.AreEqual(new[] { "1111", "2222" }, cards);
    }

    [TestMethod]
    public void KeysOf_ReflectsEveryKeyTheNamedRelationsActuallyProduce_EvenWhenALookupWouldHaveMissedOne()
    {
        // Regression scenario for a drift bug: site_ids was computed via its own
        // independent lookup on "brand" + a couple of literal-parent-dev
        // extraLookups, while site_contacts derives its keys from "email"/"brand_phone_number"/etc — sources
        // "brand" doesn't cover. A customer can have an B3-brand email (so site_contacts gets an
        // "b3" site) with no B3 row in the brand profile at all (so the old site_ids lookup
        // would never include "b3") — which the target platform would reject on import. keysOf instead reads the
        // keyed relations' OWN keys directly, so it can't miss one.
        File.WriteAllText(Path.Combine(_dir, "peopleI.csv"), "id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "brandI.csv"), "customer_key,brand,market\n1,B4,US\n"); // no B3 row here
        File.WriteAllText(Path.Combine(_dir, "emailI.csv"),
            "customer_key,email,brand,market\n1,shared@x.com,B4,US\n1,shared@x.com,B3,US\n"); // but B3 email exists
        var config = """
        {
          "sources": {
            "peopleI": { "file": "peopleI.csv", "fields": { "id": { "type": "integer" } } },
            "brandI": { "file": "brandI.csv", "fields": { "customer_key": { "type": "integer" } } },
            "emailI": { "file": "emailI.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "peopleI", "mode": "object", "source": "peopleI",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array", "keysOf": [ "site_contacts", "site_properties" ] }
              ],
              "children": [
                { "source": "brandI", "as": "site_properties", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B4": "example-us-b4-dev" } } ] },
                  "fields": [ { "name": "market", "from": "market", "type": "string" } ] },
                { "source": "emailI", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "id", "child": "customer_key" },
                  "keyBy": { "from": "brand", "pipeline": [ { "op": "map", "cases": { "B4": "example-us-b4-dev", "B3": "example-us-b3-dev" } } ] },
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "email", "type": "string" }
                  ] }
              ],
              "load": { "fileBaseName": "peopleI", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;
        var subSiteIds = d.GetProperty("site_ids").EnumerateArray().Select(e => e.GetString()).ToList();

        CollectionAssert.AreEquivalent(new[] { "example-us-b4-dev", "example-us-b3-dev" }, subSiteIds,
            "site_ids must include example-us-b3-dev because site_contacts actually has that key, regardless of the brand profile never having an B3 row");

        // site_contacts itself must actually contain the b3-dev key, confirming the premise of the test.
        Assert.IsTrue(d.GetProperty("site_contacts").TryGetProperty("example-us-b3-dev", out _));
    }

    [TestMethod]
    public void KeysOf_UnknownRelationName_FailsAtCompileTime()
    {
        File.WriteAllText(Path.Combine(_dir, "peopleJ.csv"), "id\n1\n");
        var config = """
        {
          "sources": { "peopleJ": { "file": "peopleJ.csv", "fields": { "id": { "type": "integer" } } } },
          "outputs": [
            { "name": "peopleJ", "mode": "object", "source": "peopleJ",
              "fields": [
                { "name": "id", "from": "id", "type": "integer" },
                { "name": "site_ids", "type": "array", "keysOf": [ "site_contakts" ] }
              ],
              "children": [],
              "load": { "fileBaseName": "peopleJ", "splitEvery": 1000000 } }
          ]
        }
        """;

        Assert.ThrowsExactly<bleak.Data.Transformer.Transforms.EtlConfigException>(() => EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts"));
    }

    [TestMethod]
    public void KeyedRelation_MergeBy_NoMergePrefix_TwoRowsFromSameRelationSharingAKey_FirstWinsFlatUnprefixed()
    {
        // Regression scenario for loyalty_details at site_contacts.example-us-parent-dev: fields must be
        // flat and unprefixed there (matching the brand-scoped sites), not wrapped in their own array and
        // not brand-prefixed. The real risk this creates: two loyalty rows from DIFFERENT brands can share
        // one email at parent-dev, and flattening both unprefixed would collide on field names. With no
        // mergePrefix configured, WriteMergedObject's existing per-(relation, prefix) dedup means every row
        // from this relation computes the SAME (relation, null) key, so only the first-seen row's fields
        // survive — no exception, no silently-doubled values, just a clean "first wins."
        File.WriteAllText(Path.Combine(_dir, "loyaltyK.csv"),
            "customer_key,loyalty_id,loyalty_brand,market,loyalty_email\n" +
            "1,L-B4,B4,US,shared@x.com\n" +
            "1,L-B1,B1,US,shared@x.com\n");
        var config = """
        {
          "sources": {
            "loyaltyK": { "file": "loyaltyK.csv", "fields": { "customer_key": { "type": "integer" } } }
          },
          "outputs": [
            { "name": "loyaltyK", "mode": "object", "source": "loyaltyK", "distinctBy": "customer_key",
              "fields": [ { "name": "id", "from": "customer_key", "type": "integer" } ],
              "children": [
                { "source": "loyaltyK", "as": "site_contacts", "cardinality": "many",
                  "on": { "parent": "customer_key", "child": "customer_key" },
                  "keyBy": { "pipeline": [ { "op": "literal", "default": "example-us-parent-dev" } ] },
                  "mergeBy": "loyalty_email",
                  "fields": [
                    { "name": "contact_type", "type": "string", "pipeline": [ { "op": "literal", "default": "email" } ] },
                    { "name": "contact_value", "from": "loyalty_email", "type": "string" },
                    { "name": "contact_properties", "type": "object", "fields": [
                      { "name": "loyalty_id", "from": "loyalty_id", "type": "string" },
                      { "name": "loyalty_brand", "from": "loyalty_brand", "type": "string" }
                    ] }
                  ] }
              ],
              "load": { "fileBaseName": "loyaltyK", "splitEvery": 1000000 } }
          ]
        }
        """;
        var results = EtlEngine.Run(EtlConfigLoader.FromJson(config), _dir, _out, "ts");
        var d = JsonDocument.Parse(File.ReadAllLines(results[0].Files[0])[0]).RootElement;

        var parentDev = d.GetProperty("site_contacts").GetProperty("example-us-parent-dev").EnumerateArray().ToList();
        Assert.AreEqual(1, parentDev.Count, "the two loyalty rows share one email, so they must merge into ONE contact, not two");

        var props = parentDev[0].GetProperty("contact_properties");
        Assert.AreEqual("L-B4", props.GetProperty("loyalty_id").GetString(), "the first-seen loyalty row must win");
        Assert.AreEqual("B4", props.GetProperty("loyalty_brand").GetString());
        Assert.IsFalse(props.TryGetProperty("b1_loyalty_id", out _), "no brand-prefixed keys should appear");
        Assert.IsFalse(props.TryGetProperty("b4_loyalty_id", out _), "no brand-prefixed keys should appear");
    }
}
