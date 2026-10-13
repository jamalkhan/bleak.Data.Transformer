using System.Globalization;
using System.Text;
using bleak.Data.Transformer.Engine;
using bleak.Data.Transformer.Sources;

namespace bleak.Data.Transformer.Join;

/// <summary>A primary row with its nested children attached (recursively). Children are keyed by the
/// <see cref="RelationPlan"/> itself (so two relations sharing an <c>as</c> name don't collide); a
/// one-cardinality relation holds 0..1, a many relation holds 0..N.</summary>
public sealed class JoinedRow
{
    public required string[] Row { get; init; }
    public Dictionary<RelationPlan, List<JoinedRow>> Children { get; } = new();
}

/// <summary>Produces joined/nested rows (object mode) or key-grouped rows (keyedMap mode) for an output.</summary>
public interface IJoinStrategy
{
    /// <summary>Object mode: stream each primary row with its nested children attached.</summary>
    IEnumerable<JoinedRow> Join(NodePlan root);

    /// <summary>KeyedMap mode: stream the primary rows grouped by a key column.</summary>
    IEnumerable<IReadOnlyList<string[]>> Group(NodePlan root, string groupByColumn);
}

/// <summary>Join-key parsing. The streaming sort/merge primitives key on 64-bit integers.</summary>
public static class JoinKeys
{
    /// <summary>Parses a raw key cell as a long; unparseable/empty keys sort last.</summary>
    public static long ToLong(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : long.MaxValue;

    private const char Separator = '\u0001'; // unlikely to appear in real data

    /// <summary>Builds a string match key from one or more columns (for hashInMemory joins, incl. composite).
    /// <paramref name="caseInsensitive"/> lowercases each column's value first — e.g. so an email address
    /// stored with different casing on each side of the join still matches. Both sides of a join must
    /// agree on this, which is why it lives on the relation, not on either individual source.</summary>
    public static string Composite(SourceSchema schema, string[] row, IReadOnlyList<string> columns, bool caseInsensitive = false)
    {
        if (columns.Count == 1)
        {
            var v = schema.RawValue(row, columns[0]) ?? "";
            return caseInsensitive ? v.ToLowerInvariant() : v;
        }
        var sb = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(Separator);
            var v = schema.RawValue(row, columns[i]) ?? "";
            sb.Append(caseInsensitive ? v.ToLowerInvariant() : v);
        }
        return sb.ToString();
    }
}
