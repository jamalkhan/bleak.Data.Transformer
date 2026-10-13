using System.Text.Json;
using bleak.Data.Transformer.Config;

namespace bleak.Data.Transformer.Transforms;

/// <summary>
/// Maps an op name to a factory that builds an <see cref="ITransform"/> from its <see cref="StepConfig"/>.
/// New transforms are added by registering a factory — the engine never hard-codes the op list.
/// </summary>
public sealed class TransformRegistry
{
    private readonly Dictionary<string, Func<StepConfig, ITransform>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A registry preloaded with the built-in op catalog.</summary>
    public static TransformRegistry CreateDefault()
    {
        var r = new TransformRegistry();

        // Type conversions
        r.Register("toString",   s => new ConvertTransform(DataType.String,   s.Format, s.Culture));
        r.Register("toInteger",  s => new ConvertTransform(DataType.Integer,  s.Format, s.Culture));
        r.Register("toInt",      s => new ConvertTransform(DataType.Integer,  s.Format, s.Culture));
        r.Register("toDecimal",  s => new ConvertTransform(DataType.Decimal,  s.Format, s.Culture));
        r.Register("toDouble",   s => new ConvertTransform(DataType.Double,   s.Format, s.Culture));
        r.Register("toBool",     s => new ConvertTransform(DataType.Bool,     s.Format, s.Culture));
        r.Register("toDate",     s => new ConvertTransform(DataType.Date,     s.Format, s.Culture));
        r.Register("toDateTime", s => new ConvertTransform(DataType.DateTime, s.Format, s.Culture));

        // Formatting
        r.Register("dateFormat",   s => new DateFormatTransform(Require(s.Format, "dateFormat", "format"), s.Culture));
        r.Register("numberFormat", s => new NumberFormatTransform(Require(s.Format, "numberFormat", "format"), s.Culture));

        // Arithmetic
        r.Register("add",      s => new ArithmeticTransform(ArithmeticOp.Add,      CompileOperand(RequireRhs(s, "add"))));
        r.Register("subtract", s => new ArithmeticTransform(ArithmeticOp.Subtract, CompileOperand(RequireRhs(s, "subtract"))));
        r.Register("multiply", s => new ArithmeticTransform(ArithmeticOp.Multiply, CompileOperand(RequireRhs(s, "multiply"))));
        r.Register("divide",   s => new ArithmeticTransform(ArithmeticOp.Divide,   CompileOperand(RequireRhs(s, "divide"))));

        // String
        r.Register("lower", _ => new CaseTransform(upper: false));
        r.Register("upper", _ => new CaseTransform(upper: true));
        r.Register("snakeCase", _ => new SnakeCaseTransform());
        r.Register("split", s => new SplitTransform(s.Separator));
        r.Register("concat", s => new ConcatTransform(
            (s.Values ?? throw new EtlConfigException("concat requires 'values'"))
                .Select(CompileOperand).ToArray(),
            s.Separator));

        // Literal (ignores the running value)
        r.Register("literal", s => new LiteralTransform(s.Default is { } d ? ValueConvert.FromLiteral(d) : null));

        // Lookup table
        r.Register("map", s =>
        {
            var cases = s.Cases ?? throw new EtlConfigException("map requires 'cases'.");
            var compiled = cases.ToDictionary(kv => kv.Key, kv => ValueConvert.FromLiteral(kv.Value));
            return new MapTransform(compiled, s.Default is { } d ? ValueConvert.FromLiteral(d) : null, s.Default is not null);
        });

        return r;
    }

    public void Register(string op, Func<StepConfig, ITransform> factory) => _factories[op] = factory;

    /// <summary>Compiles a config step into a runnable <see cref="Step"/> (transform + error handling).</summary>
    public Step CompileStep(StepConfig step)
    {
        if (string.IsNullOrWhiteSpace(step.Op))
        {
            throw new EtlConfigException("a pipeline step is missing 'op'.");
        }
        if (!_factories.TryGetValue(step.Op, out var factory))
        {
            throw new EtlConfigException($"unknown transform op '{step.Op}'.");
        }

        var transform = factory(step);
        var (mode, defaultValue) = ParseOnError(step.OnError);
        return new Step(step.Op, transform, mode, defaultValue);
    }

    public IReadOnlyList<Step> CompilePipeline(IEnumerable<StepConfig> steps)
        => steps.Select(CompileStep).ToList();

    /// <summary>Compiles an operand spec into a resolver over (runningValue, row).</summary>
    public static Operand CompileOperand(OperandConfig operand)
    {
        if (operand.Value == true)
        {
            return static (running, _) => running;
        }
        if (operand.Field is { Length: > 0 } field)
        {
            return (_, row) => row.GetField(field);
        }
        if (operand.Literal is { } literal)
        {
            var constant = ValueConvert.FromLiteral(literal);
            return (_, _) => constant;
        }
        throw new EtlConfigException("an operand must specify one of: field, literal, or value:true.");
    }

    private static (StepErrorMode, object?) ParseOnError(JsonElement? onError)
    {
        if (onError is not { } el)
        {
            return (StepErrorMode.Throw, null);
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                return s?.ToLowerInvariant() switch
                {
                    "throw" => (StepErrorMode.Throw, null),
                    "null" => (StepErrorMode.Null, null),
                    _ => throw new EtlConfigException($"invalid onError value '{s}'."),
                };

            case JsonValueKind.Object:
                if (el.TryGetProperty("default", out var def))
                {
                    return (StepErrorMode.Default, ValueConvert.FromLiteral(def));
                }
                throw new EtlConfigException("onError object must contain 'default'.");

            default:
                throw new EtlConfigException("onError must be \"throw\", \"null\", or { \"default\": <value> }.");
        }
    }

    private static string Require(string? value, string op, string field)
        => string.IsNullOrEmpty(value) ? throw new EtlConfigException($"{op} requires '{field}'.") : value;

    private static OperandConfig RequireRhs(StepConfig s, string op)
        => s.Rhs ?? throw new EtlConfigException($"{op} requires 'rhs'.");
}

/// <summary>Thrown when the configuration is structurally invalid.</summary>
public sealed class EtlConfigException : Exception
{
    public EtlConfigException(string message) : base(message) { }
}
