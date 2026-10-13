using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using bleak.Data.Transformer.Config;

namespace bleak.Data.Transformer.Transforms;

/// <summary>
/// Central type parsing/coercion used both for input-field typing (raw string -> typed value) and for
/// the conversion transforms / final output coercion. Throws on failure; callers decide the policy.
/// </summary>
public static class ValueConvert
{
    /// <summary>Resolves a culture name to a provider; invariant when null/empty.</summary>
    public static CultureInfo Culture(string? name)
        => string.IsNullOrEmpty(name) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);

    /// <summary>Parses a raw cell string into the declared input type. Empty/whitespace becomes null.</summary>
    public static object? ParseInput(string? raw, DataType type, string? format, string? culture)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        return To(raw, type, format, culture);
    }

    /// <summary>
    /// Converts an arbitrary value to the target type. Strings are parsed; numbers/dates are converted.
    /// Throws <see cref="FormatException"/>/<see cref="InvalidCastException"/> on failure.
    /// </summary>
    public static object? To(object? value, DataType type, string? format = null, string? culture = null)
    {
        if (value is null)
        {
            return null;
        }

        var provider = Culture(culture);

        return type switch
        {
            DataType.String => ToStringValue(value, provider),
            DataType.Integer => ToInteger(value, provider),
            DataType.Decimal => value is decimal dm ? dm : Convert.ToDecimal(ParseIfString(value, () => decimal.Parse(AsString(value), NumberStyles.Float | NumberStyles.AllowThousands, provider)), provider),
            DataType.Double => value is double db ? db : Convert.ToDouble(ParseIfString(value, () => double.Parse(AsString(value), NumberStyles.Float | NumberStyles.AllowThousands, provider)), provider),
            DataType.Bool => ToBool(value),
            DataType.Date or DataType.DateTime => ToDateTime(value, format, provider),
            _ => value,
        };
    }

    private static object ParseIfString(object value, Func<object> parse)
        => value is string ? parse() : value;

    private static string AsString(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string ToStringValue(object value, IFormatProvider provider) => value switch
    {
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, provider),
        _ => value.ToString() ?? "",
    };

    private static long ToInteger(object value, IFormatProvider provider) => value switch
    {
        long l => l,
        int i => i,
        string s => long.Parse(s, NumberStyles.Integer, provider),
        decimal d => (long)Math.Round(d, MidpointRounding.AwayFromZero),
        double db => (long)Math.Round(db, MidpointRounding.AwayFromZero),
        _ => Convert.ToInt64(value, provider),
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => s.Trim() switch
        {
            "1" => true,
            "0" => false,
            var other => bool.Parse(other),
        },
        _ => Convert.ToBoolean(value),
    };

    /// <summary>
    /// A bare "M-D" or "MM-DD" with no year (e.g. a birthdate truncated for privacy: "9-3", "02-29").
    /// <see cref="DateTime.Parse(string, IFormatProvider, DateTimeStyles)"/> silently accepts this shape
    /// and fills in the CURRENT year — "09-03" parsed today becomes "2026-09-03", not an error, just a
    /// wrong date. Reject it explicitly so it surfaces as a normal recoverable failure instead of
    /// shipping a fabricated year.
    /// </summary>
    private static readonly Regex NoYearMonthDay = new(@"^\d{1,2}-\d{1,2}$", RegexOptions.Compiled);

    private static DateTime ToDateTime(object value, string? format, IFormatProvider provider)
    {
        if (value is DateTime dt)
        {
            return dt;
        }
        var s = AsString(value);
        if (string.IsNullOrEmpty(format) && NoYearMonthDay.IsMatch(s.Trim()))
        {
            throw new FormatException($"'{s}' has no year (month-day only) — refusing to guess a year.");
        }
        return string.IsNullOrEmpty(format)
            ? DateTime.Parse(s, provider, DateTimeStyles.RoundtripKind)
            : DateTime.ParseExact(s, format, provider, DateTimeStyles.None);
    }

    /// <summary>Materializes a JSON literal token into a CLR scalar (string/long/double/bool/null).</summary>
    public static object? FromLiteral(JsonElement literal) => literal.ValueKind switch
    {
        JsonValueKind.String => literal.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => literal.TryGetInt64(out var l) ? (object)l : literal.GetDouble(),
        _ => literal.GetRawText(),
    };
}
