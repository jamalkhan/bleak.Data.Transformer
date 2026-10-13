using System.Globalization;
using System.Text;
using bleak.Data.Transformer.Config;

namespace bleak.Data.Transformer.Transforms;

/// <summary>Converts the running value to a target type (the to* family). Failure is governed by the step.</summary>
public sealed class ConvertTransform : ITransform
{
    private readonly DataType _target;
    private readonly string? _format;
    private readonly string? _culture;

    public ConvertTransform(DataType target, string? format, string? culture)
    {
        _target = target;
        _format = format;
        _culture = culture;
    }

    public object? Apply(object? input, IRowContext row) => ValueConvert.To(input, _target, _format, _culture);
}

/// <summary>Formats a date/datetime running value into a string using a .NET format string.</summary>
public sealed class DateFormatTransform : ITransform
{
    private readonly string _format;
    private readonly string? _culture;

    public DateFormatTransform(string format, string? culture)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _culture = culture;
    }

    public object? Apply(object? input, IRowContext row)
    {
        if (input is null)
        {
            return null;
        }
        var dt = (DateTime)(ValueConvert.To(input, DataType.DateTime) ?? throw new FormatException("not a date"));
        return dt.ToString(_format, ValueConvert.Culture(_culture));
    }
}

/// <summary>Formats a numeric running value into a string using a .NET numeric format string.</summary>
public sealed class NumberFormatTransform : ITransform
{
    private readonly string _format;
    private readonly string? _culture;

    public NumberFormatTransform(string format, string? culture)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _culture = culture;
    }

    public object? Apply(object? input, IRowContext row)
    {
        if (input is null)
        {
            return null;
        }
        var provider = ValueConvert.Culture(_culture);
        // Render via decimal when exact, else double, so "N2"/"F2"/"P1" etc. all work.
        if (input is double or float)
        {
            return Convert.ToDouble(input, provider).ToString(_format, provider);
        }
        return Convert.ToDecimal(input, provider).ToString(_format, provider);
    }
}

/// <summary>The four supported arithmetic operations.</summary>
public enum ArithmeticOp { Add, Subtract, Multiply, Divide }

/// <summary>Arithmetic between the running value (left) and a configured right-hand operand.</summary>
public sealed class ArithmeticTransform : ITransform
{
    private readonly ArithmeticOp _op;
    private readonly Operand _rhs;

    public ArithmeticTransform(ArithmeticOp op, Operand rhs)
    {
        _op = op;
        _rhs = rhs;
    }

    public object? Apply(object? input, IRowContext row)
    {
        var left = input;
        var right = _rhs(input, row);
        if (left is null || right is null)
        {
            throw new InvalidOperationException("arithmetic operand is null");
        }

        // Use double if either side is floating; otherwise exact decimal.
        if (left is double or float || right is double or float)
        {
            var a = Convert.ToDouble(left, CultureInfo.InvariantCulture);
            var b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
            return _op switch
            {
                ArithmeticOp.Add => a + b,
                ArithmeticOp.Subtract => a - b,
                ArithmeticOp.Multiply => a * b,
                ArithmeticOp.Divide => a / b,
                _ => throw new NotSupportedException(),
            };
        }
        else
        {
            var a = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            var b = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return _op switch
            {
                ArithmeticOp.Add => a + b,
                ArithmeticOp.Subtract => a - b,
                ArithmeticOp.Multiply => a * b,
                ArithmeticOp.Divide => a / b, // DivideByZeroException flows to the step's onError
                _ => throw new NotSupportedException(),
            };
        }
    }
}

/// <summary>Splits the running string value into an array of strings on a separator (empty entries dropped, trimmed).</summary>
public sealed class SplitTransform : ITransform
{
    private readonly string _separator;

    public SplitTransform(string? separator) => _separator = string.IsNullOrEmpty(separator) ? ";" : separator;

    public object? Apply(object? input, IRowContext row)
    {
        if (input is null)
        {
            return null;
        }
        var s = ValueConvert.To(input, DataType.String) as string ?? "";
        var parts = s.Split(_separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Cast<object?>().ToList();
    }
}

/// <summary>Lowercases or uppercases the running string value (invariant culture).</summary>
public sealed class CaseTransform : ITransform
{
    private readonly bool _upper;
    public CaseTransform(bool upper) => _upper = upper;

    public object? Apply(object? input, IRowContext row)
    {
        if (input is null) return null;
        var s = ValueConvert.To(input, DataType.String) as string ?? "";
        return _upper ? s.ToUpperInvariant() : s.ToLowerInvariant();
    }
}

/// <summary>
/// Looks up the running value (coerced to a string, case-insensitive) in a fixed table of cases and
/// returns the matching literal, or a configured default when no case matches (null if no default given).
/// Used for small code translations, e.g. a raw brand code ("B1") -&gt; a site_id ("example-us-b1-dev").
/// </summary>
public sealed class MapTransform : ITransform
{
    private readonly Dictionary<string, object?> _cases;
    private readonly object? _default;
    private readonly bool _hasDefault;

    public MapTransform(Dictionary<string, object?> cases, object? @default, bool hasDefault)
    {
        _cases = new Dictionary<string, object?>(cases, StringComparer.OrdinalIgnoreCase);
        _default = @default;
        _hasDefault = hasDefault;
    }

    public object? Apply(object? input, IRowContext row)
    {
        var key = input is null ? null : (ValueConvert.To(input, DataType.String) as string ?? input.ToString());
        if (key is not null && _cases.TryGetValue(key, out var value))
        {
            return value;
        }
        if (_hasDefault)
        {
            return _default;
        }
        throw new KeyNotFoundException($"map: no case for '{key}' and no default was configured.");
    }
}

/// <summary>Ignores the running value and always returns a fixed literal (e.g. a hard-coded false/null).</summary>
public sealed class LiteralTransform : ITransform
{
    private readonly object? _value;
    public LiteralTransform(object? value) => _value = value;
    public object? Apply(object? input, IRowContext row) => _value;
}

/// <summary>
/// Converts the running string value to snake_case: lowercases it and replaces every run of
/// non-alphanumeric characters (spaces, punctuation) with a single underscore, trimming leading/trailing
/// underscores. E.g. "Model One" -&gt; "model_one".
/// </summary>
public sealed class SnakeCaseTransform : ITransform
{
    public object? Apply(object? input, IRowContext row)
    {
        if (input is null)
        {
            return null;
        }
        var s = ValueConvert.To(input, DataType.String) as string ?? "";
        var sb = new StringBuilder(s.Length);
        var lastWasUnderscore = false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '_')
        {
            sb.Length--;
        }
        return sb.ToString();
    }
}

/// <summary>Concatenates operands (fields, literals, and/or the running value) into a string.</summary>
public sealed class ConcatTransform : ITransform
{
    private readonly Operand[] _values;
    private readonly string _separator;

    public ConcatTransform(Operand[] values, string? separator)
    {
        _values = values;
        _separator = separator ?? "";
    }

    public object? Apply(object? input, IRowContext row)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _values.Length; i++)
        {
            if (i > 0 && _separator.Length > 0)
            {
                sb.Append(_separator);
            }
            var v = _values[i](input, row);
            if (v is not null)
            {
                sb.Append(ValueConvert.To(v, DataType.String));
            }
        }
        return sb.ToString();
    }
}
