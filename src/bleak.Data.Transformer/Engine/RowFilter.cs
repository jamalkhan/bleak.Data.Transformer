using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Sources;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Engine;

/// <summary>
/// A compiled primary-record filter: a set of conditions that must all pass (AND). Date ops compare a
/// column against an ISO date or a relative token (e.g. "now-2years") resolved once at run start.
/// </summary>
public sealed class RowFilter
{
    private readonly IReadOnlyList<Func<SourceSchema, string[], bool>> _conditions;

    private RowFilter(IReadOnlyList<Func<SourceSchema, string[], bool>> conditions) => _conditions = conditions;

    public bool Passes(SourceSchema schema, string[] row)
    {
        foreach (var c in _conditions)
        {
            if (!c(schema, row))
            {
                return false;
            }
        }
        return true;
    }

    public static RowFilter Compile(IReadOnlyList<FilterConditionConfig> conditions, SourceSchema schema)
        => new(conditions.Select(c => Build(c, schema)).ToList());

    private static Func<SourceSchema, string[], bool> Build(FilterConditionConfig c, SourceSchema schema)
    {
        if (string.IsNullOrWhiteSpace(c.Column))
        {
            throw new EtlConfigException("a filter condition is missing 'column'.");
        }
        if (schema.ColumnIndex(c.Column) < 0)
        {
            throw new EtlConfigException($"filter column '{c.Column}' not found in the primary source.");
        }

        var col = c.Column;
        var op = (c.Op ?? "").Trim().ToLowerInvariant();
        switch (op)
        {
            case "onorafter":
            case "after":
            case "onorbefore":
            case "before":
            {
                var cutoff = ResolveDate(RequireString(c)).ToUniversalTime();
                return (s, row) =>
                {
                    var raw = s.RawValue(row, col);
                    if (string.IsNullOrEmpty(raw) || !TryDate(raw, out var dt))
                    {
                        return false; // missing/garbage date can't be confirmed recent -> exclude
                    }
                    var u = dt.ToUniversalTime();
                    return op switch
                    {
                        "onorafter" => u >= cutoff,
                        "after" => u > cutoff,
                        "onorbefore" => u <= cutoff,
                        _ => u < cutoff,
                    };
                };
            }
            case "notempty": return (s, row) => !string.IsNullOrEmpty(s.RawValue(row, col));
            case "empty": return (s, row) => string.IsNullOrEmpty(s.RawValue(row, col));
            case "in":
            {
                var set = ValueList(c).ToHashSet(StringComparer.Ordinal);
                return (s, row) => set.Contains(s.RawValue(row, col) ?? "");
            }
            case "eq":
            case "ne":
            case "gt":
            case "ge":
            case "lt":
            case "le":
            {
                var val = RequireString(c);
                return (s, row) =>
                {
                    var raw = s.RawValue(row, col) ?? "";
                    int cmp = decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
                              && decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var b)
                        ? a.CompareTo(b)
                        : string.CompareOrdinal(raw, val);
                    return op switch
                    {
                        "eq" => cmp == 0,
                        "ne" => cmp != 0,
                        "gt" => cmp > 0,
                        "ge" => cmp >= 0,
                        "lt" => cmp < 0,
                        _ => cmp <= 0,
                    };
                };
            }
            default:
                throw new EtlConfigException($"unknown filter op '{c.Op}'.");
        }
    }

    private static bool TryDate(string s, out DateTime dt)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt);

    private static readonly Regex RelativeDate =
        new(@"^now(?:\s*([+-]\d+)\s*(year|month|week|day|hour|minute)s?)?$", RegexOptions.IgnoreCase);

    /// <summary>Resolves an ISO date or a relative token ("now", "now-2years", "now-90days", …) to a DateTime.</summary>
    public static DateTime ResolveDate(string token)
    {
        var t = token.Trim();
        var m = RelativeDate.Match(t);
        if (m.Success)
        {
            var now = DateTime.UtcNow;
            if (!m.Groups[1].Success)
            {
                return now;
            }
            var amount = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value.ToLowerInvariant() switch
            {
                "year" => now.AddYears(amount),
                "month" => now.AddMonths(amount),
                "week" => now.AddDays(7 * amount),
                "day" => now.AddDays(amount),
                "hour" => now.AddHours(amount),
                _ => now.AddMinutes(amount),
            };
        }
        if (TryDate(t, out var dt))
        {
            return dt;
        }
        throw new EtlConfigException($"filter value '{token}' is not a valid date or relative expression (e.g. \"now-2years\").");
    }

    private static string RequireString(FilterConditionConfig c)
    {
        if (c.Value is not { } v)
        {
            throw new EtlConfigException($"filter on '{c.Column}' requires a 'value'.");
        }
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    }

    private static List<string> ValueList(FilterConditionConfig c)
    {
        if (c.Value is { ValueKind: JsonValueKind.Array } arr)
        {
            return arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                .ToList();
        }
        throw new EtlConfigException($"filter 'in' on '{c.Column}' requires an array value.");
    }
}
