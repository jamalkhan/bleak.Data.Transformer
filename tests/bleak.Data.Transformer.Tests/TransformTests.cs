using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Tests;

[TestClass]
public class TransformTests
{
    private readonly TransformRegistry _registry = TransformRegistry.CreateDefault();

    private static JsonElement E(object v) => JsonSerializer.SerializeToElement(v);

    private object? Run(StepConfig step, object? start, IRowContext? row = null)
        => _registry.CompileStep(step).Run(start, row ?? Row());

    private static IRowContext Row(params (string Name, object? Value)[] fields)
        => new DictRow(fields.ToDictionary(f => f.Name, f => f.Value));

    private sealed class DictRow : IRowContext
    {
        private readonly Dictionary<string, object?> _fields;
        public DictRow(Dictionary<string, object?> fields) => _fields = fields;
        public object? GetField(string column) => _fields.TryGetValue(column, out var v) ? v : null;
    }

    [TestMethod]
    public void Conversions_ParseScalars()
    {
        Assert.AreEqual(42L, Run(new StepConfig { Op = "toInteger" }, "42"));
        Assert.AreEqual(1.5m, Run(new StepConfig { Op = "toDecimal" }, "1.5"));
        Assert.AreEqual(2.5d, Run(new StepConfig { Op = "toDouble" }, "2.5"));
        Assert.AreEqual(true, Run(new StepConfig { Op = "toBool" }, "true"));
        Assert.AreEqual(true, Run(new StepConfig { Op = "toBool" }, "1"));
        Assert.AreEqual(new DateTime(2026, 6, 11), Run(new StepConfig { Op = "toDate", Format = "yyyy-MM-dd" }, "2026-06-11"));
    }

    [TestMethod]
    public void ToDateTime_RejectsBareMonthDay_InsteadOfFabricatingCurrentYear()
    {
        // .NET's DateTime.Parse silently fills in the CURRENT year for a bare "M-D" (e.g. a birthdate
        // truncated for privacy) — "09-03" becomes "<this year>-09-03", not an error, just wrong data.
        // Must throw instead of shipping a fabricated year.
        Assert.ThrowsExactly<EtlRowException>(() => Run(new StepConfig { Op = "toDateTime" }, "09-03"));
        Assert.ThrowsExactly<EtlRowException>(() => Run(new StepConfig { Op = "toDateTime" }, "2-29"));
        Assert.ThrowsExactly<EtlRowException>(() => Run(new StepConfig { Op = "toDateTime" }, "9-3"));

        // A real full date must still parse normally.
        Assert.AreEqual(new DateTime(1990, 9, 3), Run(new StepConfig { Op = "toDateTime" }, "1990-09-03"));
    }

    [TestMethod]
    public void ToDecimal_AcceptsScientificNotation()
    {
        // A model score like "1.5e-05" must parse to the exact value, not fail, and not
        // get truncated to "6.67572021484375" (which would be 100,000x too large).
        Assert.AreEqual(1.5e-05m, Run(new StepConfig { Op = "toDecimal" }, "1.5e-05"));
        Assert.AreEqual(1.5e10m, Run(new StepConfig { Op = "toDecimal" }, "1.5E+10"));
    }

    [TestMethod]
    public void Map_ResolvesCaseInsensitively_AndFallsBackToDefault()
    {
        var step = new StepConfig
        {
            Op = "map",
            Cases = new Dictionary<string, JsonElement>
            {
                ["B1"] = E("example-us-b1-dev"),
                ["B2"] = E("example-us-b2-dev"),
            },
            Default = E("unknown-brand"),
        };
        Assert.AreEqual("example-us-b1-dev", Run(step, "B1"));
        Assert.AreEqual("example-us-b1-dev", Run(step, "b1")); // case-insensitive
        Assert.AreEqual("unknown-brand", Run(step, "ZZ"));
        Assert.AreEqual("unknown-brand", Run(step, null));
    }

    [TestMethod]
    public void Map_NoDefault_Throws()
    {
        var step = new StepConfig
        {
            Op = "map",
            Cases = new Dictionary<string, JsonElement> { ["B1"] = E("example-us-b1-dev") },
        };
        Assert.ThrowsExactly<EtlRowException>(() => Run(step, "ZZ"));
    }

    [TestMethod]
    public void OnError_Null_YieldsNull()
    {
        var step = new StepConfig { Op = "toInteger", OnError = E("null") };
        Assert.IsNull(Run(step, "not-a-number"));
    }

    [TestMethod]
    public void OnError_Default_Substitutes()
    {
        var step = new StepConfig { Op = "toInteger", OnError = E(new Dictionary<string, object> { ["default"] = 0 }) };
        Assert.AreEqual(0L, Run(step, "not-a-number"));
    }

    [TestMethod]
    public void OnError_Throw_RaisesRowException()
    {
        var step = new StepConfig { Op = "toInteger", OnError = E("throw") };
        Assert.ThrowsExactly<EtlRowException>(() => { Run(step, "not-a-number"); });
    }

    [TestMethod]
    public void OnError_DefaultsToThrow()
    {
        Assert.ThrowsExactly<EtlRowException>(() => { Run(new StepConfig { Op = "toInteger" }, "nope"); });
    }

    [TestMethod]
    public void Arithmetic_UsesRunningValueAsLeft()
    {
        var subtract = new StepConfig { Op = "subtract", Rhs = new OperandConfig { Literal = E(3) } };
        Assert.AreEqual(7m, Run(subtract, 10L));

        var multiplyByField = new StepConfig { Op = "multiply", Rhs = new OperandConfig { Field = "factor" } };
        Assert.AreEqual(20m, Run(multiplyByField, 10L, Row(("factor", 2L))));

        var divide = new StepConfig { Op = "divide", Rhs = new OperandConfig { Literal = E(4) } };
        Assert.AreEqual(2.5m, Run(divide, 10L));
    }

    [TestMethod]
    public void Arithmetic_DivideByZero_IsHandledByOnError()
    {
        var divide = new StepConfig { Op = "divide", Rhs = new OperandConfig { Literal = E(0) }, OnError = E("null") };
        Assert.IsNull(Run(divide, 10L));
    }

    [TestMethod]
    public void Concat_JoinsOperands()
    {
        var step = new StepConfig
        {
            Op = "concat",
            Values = new()
            {
                new OperandConfig { Literal = E("email::") },
                new OperandConfig { Field = "addr" },
            },
        };
        Assert.AreEqual("email::a@x.com", Run(step, null, Row(("addr", "a@x.com"))));
    }

    [TestMethod]
    public void Concat_WithSeparatorAndRunningValue()
    {
        var step = new StepConfig
        {
            Op = "concat",
            Separator = "-",
            Values = new()
            {
                new OperandConfig { Value = true },
                new OperandConfig { Literal = E("b") },
            },
        };
        Assert.AreEqual("a-b", Run(step, "a"));
    }

    [TestMethod]
    public void Lower_And_Upper()
    {
        Assert.AreEqual("accessories", Run(new StepConfig { Op = "lower" }, "ACCESSORIES"));
        Assert.AreEqual("ACCESSORIES", Run(new StepConfig { Op = "upper" }, "accessories"));
    }

    [TestMethod]
    public void DateFormat_FormatsDate()
    {
        var step = new StepConfig { Op = "dateFormat", Format = "yyyy/MM" };
        Assert.AreEqual("2026/06", Run(step, new DateTime(2026, 6, 11)));
    }

    [TestMethod]
    public void NumberFormat_FormatsNumber()
    {
        var step = new StepConfig { Op = "numberFormat", Format = "F2", Culture = "en-US" };
        Assert.AreEqual("1234.50", Run(step, 1234.5d));
    }

    [TestMethod]
    public void UnknownOp_Throws()
    {
        Assert.ThrowsExactly<EtlConfigException>(() => { _registry.CompileStep(new StepConfig { Op = "bogus" }); });
    }
}
