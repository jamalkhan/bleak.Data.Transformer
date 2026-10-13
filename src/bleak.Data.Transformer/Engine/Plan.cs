using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Sources;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Engine;

/// <summary>A compiled scalar/array output field: where it starts, its emitted type, its steps, or a lookup.</summary>
public sealed class FieldPlan
{
    public required string Name { get; init; }
    public string? FromColumn { get; init; }
    public required DataType OutputType { get; init; }
    public required IReadOnlyList<Step> Steps { get; init; }

    /// <summary>When set, the value is pulled from a related source instead of <see cref="FromColumn"/>/<see cref="Steps"/>.</summary>
    public LookupPlan? Lookup { get; init; }

    /// <summary>Extra lookups unioned into this field's array alongside <see cref="Lookup"/> — see <c>FieldConfig.Lookups</c>.</summary>
    public IReadOnlyList<LookupPlan>? ExtraLookups { get; init; }

    /// <summary>When set, the value is the first non-empty candidate from this ordered list — see <see cref="CoalesceCandidatePlan"/>.</summary>
    public IReadOnlyList<CoalesceCandidatePlan>? Coalesce { get; init; }

    /// <summary>Coalesce fields only: true emits the winning candidate's <see cref="CoalesceCandidatePlan.Label"/> instead of its value.</summary>
    public bool CoalesceEmitLabel { get; init; }

    /// <summary>Field-level override of the run's error policy — see <c>FieldConfig.OnError</c>.</summary>
    public FieldErrorMode OnError { get; init; } = FieldErrorMode.Inherit;

    /// <summary>When true and the field's value is null, writes an explicit JSON <c>null</c> instead of
    /// omitting the field — see <c>FieldConfig.EmitNull</c>.</summary>
    public bool EmitNull { get; init; }

    /// <summary>When set, the field's value is the union of these keyed relations' own distinct keys —
    /// see <c>FieldConfig.KeysOf</c>.</summary>
    public IReadOnlyList<string>? KeysOfRelations { get; init; }
}

/// <summary>One candidate in a <see cref="FieldPlan.Coalesce"/> chain: a column on this row, or a lookup.</summary>
public sealed class CoalesceCandidatePlan
{
    /// <summary>A column on this row. Mutually exclusive with <see cref="Lookup"/>.</summary>
    public string? FromColumn { get; init; }

    /// <summary>A lookup on a related source, always rendered as a single scalar. Mutually exclusive with <see cref="FromColumn"/>.</summary>
    public LookupPlan? Lookup { get; init; }

    /// <summary>Emitted for this candidate when the owning field's <see cref="FieldPlan.CoalesceEmitLabel"/> is true.</summary>
    public string? Label { get; init; }
}

/// <summary>A compiled lookup: an in-memory index over a related source, projected on join.</summary>
public sealed class LookupPlan
{
    public required ISourceReader Reader { get; init; }
    public required SourceSchema Schema { get; init; }
    public required string ParentKeyColumn { get; init; }  // column on the current row
    public required string ChildKeyColumn { get; init; }   // column on the lookup source
    public required string Column { get; init; }           // projected column
    public required bool Single { get; init; }
    public required bool Distinct { get; init; }
    public int? Limit { get; init; }

    /// <summary>When true, this lookup emits a bool ("does at least one matching/filtered row exist?").</summary>
    public bool IsExists { get; init; }

    /// <summary>Optional row filter applied to the lookup source before it's indexed.</summary>
    public RowFilter? Filter { get; init; }

    /// <summary>When set, replaces <see cref="Column"/>: computes the indexed value per lookup-source row.</summary>
    public IReadOnlyList<Step>? ExprSteps { get; init; }

    /// <summary>Column on the lookup source's row <see cref="ExprSteps"/> starts from (optional).</summary>
    public string? ExprFromColumn { get; init; }

    /// <summary>
    /// Structural signature of what this lookup indexes (resolved source file + child key column +
    /// projected column/expr + filter + exists-mode) — independent of which output compiled it, and of
    /// per-consumer render options (<see cref="ParentKeyColumn"/>, <see cref="Single"/>, etc). Two
    /// lookups across DIFFERENT outputs in the same run with equal keys index the exact same data, so
    /// the engine builds it once and shares the result — see <c>EtlEngine.BuildLookupIndexes</c>.
    /// </summary>
    public string? CacheKey { get; init; }

    /// <summary>key -> projected values; built once per distinct <see cref="CacheKey"/> (or once per
    /// lookup when there's no cache key), and shared across every lookup with that key in the run.</summary>
    public Dictionary<string, List<string>> Index { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A compiled child relationship: how it links to its parent and the child node it nests.</summary>
public sealed class RelationPlan
{
    public required string As { get; init; }
    public required Cardinality Cardinality { get; init; }

    /// <summary>Parent-side join columns (one for a simple join, more for a composite join).</summary>
    public required IReadOnlyList<string> ParentKeyColumns { get; init; }

    /// <summary>Child-side join columns, positionally matched to <see cref="ParentKeyColumns"/>.</summary>
    public required IReadOnlyList<string> ChildKeyColumns { get; init; }

    /// <summary>When true, this relation's join ignores case on both sides — see <c>ChildConfig.CaseInsensitiveJoin</c>.</summary>
    public bool CaseInsensitiveJoin { get; init; }

    public required NodePlan Child { get; init; }

    /// <summary>Render-time: keep only the first child per distinct value of this column (optional).</summary>
    public string? DistinctBy { get; init; }

    /// <summary>Render-time: merge rows sharing this column's value into one entry instead of many — see <c>ChildConfig.MergeBy</c>.</summary>
    public string? MergeBy { get; init; }

    /// <summary>Pipeline computing each row's merge prefix (optional; only meaningful with <see cref="MergeBy"/>).</summary>
    public IReadOnlyList<Step>? MergePrefixSteps { get; init; }

    /// <summary>Column on the CHILD row <see cref="MergePrefixSteps"/> starts from (optional).</summary>
    public string? MergePrefixFromColumn { get; init; }

    /// <summary>Render-time: cap the number of children emitted (optional).</summary>
    public int? Limit { get; init; }

    /// <summary>Render-time: distinct set and limit are shared with the other ShareDistinct relations in the same array — see <c>FieldConfig.ShareDistinct</c>.</summary>
    public bool ShareDistinct { get; init; }

    /// <summary>Render-time: only children passing this filter are included (optional).</summary>
    public RowFilter? Filter { get; init; }

    /// <summary>When set, children render grouped by this derived key instead of a flat array.</summary>
    public IReadOnlyList<Step>? KeyBySteps { get; init; }

    /// <summary>Column on the CHILD row the <see cref="KeyBySteps"/> pipeline starts from (optional).</summary>
    public string? KeyByFromColumn { get; init; }

    /// <summary>Keyed relations only: true = each key holds one object; false = an array.</summary>
    public bool KeyedSingle { get; init; }

    /// <summary>Keyed-single relations only: when set, keep the child with the smallest numeric value of
    /// this column instead of the first-seen one — see <c>ChildConfig.KeyedSingleBestOf</c>.</summary>
    public string? KeyedSingleBestOf { get; init; }

    /// <summary>When true, this relation's fields render as siblings of the parent's own fields (no wrapper key).</summary>
    public bool Inline { get; init; }
}

// ---- Render tree ------------------------------------------------------------------------------
// A node renders as an ordered list of items: scalar/array/lookup fields, object-groups, and
// relationship placements. Relationships are populated by the join strategy (keyed by RelationPlan)
// regardless of where they render.

public abstract class RenderItem { }

/// <summary>Render a scalar/array/lookup field.</summary>
public sealed class FieldItem : RenderItem
{
    public required FieldPlan Field { get; init; }
}

/// <summary>Render a nested object built from inner items (same row).</summary>
public sealed class GroupItem : RenderItem
{
    public required string Name { get; init; }
    public required IReadOnlyList<RenderItem> Items { get; init; }
}

/// <summary>Render one or more relationships under a single property (multiple relations are concatenated).</summary>
public sealed class RelationItem : RenderItem
{
    public required string Name { get; init; }
    public required bool AsArray { get; init; }
    public required IReadOnlyList<RelationPlan> Relations { get; init; }

    /// <summary>When true, this relation renders as an object keyed by each relation's derived key.</summary>
    public bool Keyed { get; init; }

    /// <summary>Keyed relations only: true = each key holds one object; false = an array.</summary>
    public bool KeyedSingle { get; init; }

    /// <summary>When true, this relation's fields render as siblings of the parent's own fields (no wrapper key).</summary>
    public bool Inline { get; init; }
}

/// <summary>A compiled node: an opened source, its schema, every relationship (for joining), and a render tree.</summary>
public sealed class NodePlan
{
    public required string SourceAlias { get; init; }
    public required ISourceReader Reader { get; init; }
    public required SourceSchema Schema { get; init; }

    /// <summary>Every relationship in this node (including those nested inside object-groups), for the join strategy.</summary>
    public required IReadOnlyList<RelationPlan> Children { get; init; }

    /// <summary>How to render this node, in order.</summary>
    public required IReadOnlyList<RenderItem> RenderItems { get; init; }

    /// <summary>
    /// Columns of this node's source that identify the record a row belongs to: the join key towards the
    /// parent (child nodes) or towards the children / the keyedMap <c>groupBy</c> (the primary node).
    /// Used for the <c>record_key</c> column of the warnings file. Empty when no key is known.
    /// </summary>
    public IReadOnlyList<string> RecordKeyColumns { get; set; } = Array.Empty<string>();

    /// <summary>The raw record-key value for a row (composite keys are joined with '|'), or null if unknown.</summary>
    public string? RecordKey(string[] row)
    {
        if (RecordKeyColumns.Count == 0)
        {
            return null;
        }
        var parts = new List<string>(RecordKeyColumns.Count);
        foreach (var column in RecordKeyColumns)
        {
            if (Schema.ColumnIndex(column) < 0)
            {
                return null;
            }
            parts.Add(Schema.RawValue(row, column) ?? "");
        }
        var joined = string.Join("|", parts);
        return joined.Length == 0 || parts.All(string.IsNullOrEmpty) ? null : joined;
    }
}

/// <summary>A compiled output feed.</summary>
public sealed class OutputPlan
{
    public required string Name { get; init; }
    public required OutputMode Mode { get; init; }
    public required NodePlan Root { get; init; }
    public required LoadConfig Load { get; init; }

    /// <summary>All lookups across the output's nodes (for index building + reader disposal).</summary>
    public required IReadOnlyList<LookupPlan> Lookups { get; init; }

    /// <summary>Optional primary-record filter; records that fail are excluded.</summary>
    public RowFilter? Filter { get; init; }

    /// <summary>When true, filtered-out primary rows are written to a side file.</summary>
    public bool FilterExcludedFile { get; init; }

    /// <summary>Object mode only: keep only the first primary row per distinct value of this column (optional).</summary>
    public string? DistinctByColumn { get; init; }

    /// <summary>Object mode only: emit a record only if at least one of these root relations has content (optional).</summary>
    public IReadOnlyList<string>? RequireRelations { get; init; }

    // keyedMap mode only:
    public string? GroupByColumn { get; init; }
    public IReadOnlyList<Step>? KeyExprSteps { get; init; }
    public DedupConfig? Dedup { get; init; }
}
