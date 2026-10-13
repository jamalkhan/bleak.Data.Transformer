using System.Collections;
using System.Globalization;
using System.Text.Json;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Join;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Engine;

/// <summary>Signals that the current record should be dropped (run onError = skip).</summary>
public sealed class RowSkipException : Exception
{
    public RowSkipException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A non-fatal data-quality note about one field on one record: the value parsed fine, but its raw form
/// was unusual enough to be worth reporting back to whoever owns the source data (e.g. a decimal column
/// that came through in scientific notation). Collected per-record and drained by the engine after each
/// successful write — see <see cref="RecordWriter.DrainWarnings"/> and <c>WarningSink</c> in EtlEngine.
/// </summary>
public readonly record struct FieldWarning(string Field, string RawValue, string Reason, string? Key);

/// <summary>
/// Renders compiled records to JSON by walking each node's render tree: scalar/array/lookup fields,
/// object-groups, and relationship placements. Field values run through their pipeline, are coerced to
/// the declared type, and are omitted when null/empty. A failing field is handled per the run's
/// <see cref="RunErrorPolicy"/>: nullify (omit the field), skip (drop the record), or abort (rethrow).
/// </summary>
public sealed class RecordWriter
{
    /// <summary>
    /// Compact ISO 8601 (e.g. "2013-08-02T00:00:00Z"), truncated to whole seconds. All output DateTimes
    /// are labeled UTC ("Z") regardless of the source's actual offset — the source rarely carries a real
    /// timezone, and this matches common ISO-8601 feed conventions.
    /// </summary>
    private const string Iso8601Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly RunErrorPolicy _onError;
    private readonly bool _emitEmpty;
    private readonly List<FieldWarning> _warnings = new();

    public RecordWriter(RunErrorPolicy onError, bool emitEmpty = false)
    {
        _onError = onError;
        _emitEmpty = emitEmpty;
    }

    /// <summary>Takes and clears the warnings raised while rendering the record(s) written since the last drain.</summary>
    public IReadOnlyList<FieldWarning> DrainWarnings()
    {
        if (_warnings.Count == 0)
        {
            return Array.Empty<FieldWarning>();
        }
        var taken = _warnings.ToArray();
        _warnings.Clear();
        return taken;
    }

    // ----- Object mode --------------------------------------------------------------------------
    public void WriteObject(Utf8JsonWriter w, NodePlan node, JoinedRow joined)
    {
        w.WriteStartObject();
        WriteBody(w, node, joined);
        w.WriteEndObject();
    }

    private void WriteBody(Utf8JsonWriter w, NodePlan node, JoinedRow joined)
    {
        foreach (var item in node.RenderItems)
        {
            Render(w, item, node, joined);
        }
    }

    private void Render(Utf8JsonWriter w, RenderItem item, NodePlan node, JoinedRow joined)
    {
        switch (item)
        {
            case FieldItem fi:
                WriteField(w, fi.Field, node, joined.Row, joined: joined);
                break;

            case GroupItem gi:
                w.WriteStartObject(gi.Name);
                foreach (var inner in gi.Items)
                {
                    Render(w, inner, node, joined);
                }
                w.WriteEndObject();
                break;

            case RelationItem ri:
                WriteRelation(w, ri, joined);
                break;
        }
    }

    private void WriteRelation(Utf8JsonWriter w, RelationItem item, JoinedRow joined, string? nameOverride = null)
    {
        var name = nameOverride ?? item.Name;
        if (item.Keyed)
        {
            WriteKeyedRelation(w, item, joined, name);
        }
        else if (item.Inline)
        {
            var rel = item.Relations[0];
            if (joined.Children.TryGetValue(rel, out var list))
            {
                var first = Filter(rel, list).FirstOrDefault();
                if (first is not null)
                {
                    WriteBody(w, rel.Child, first); // splice the child's own fields directly in, no wrapper key
                }
            }
        }
        else if (item.AsArray)
        {
            // Omit the property entirely when nothing matches (same "never emit empty" convention as a
            // plain array field) rather than writing an empty "[]" for every childless record.
            var picked = new List<(RelationPlan Rel, JoinedRow Child)>();
            var sharedSeen = new HashSet<string>(StringComparer.Ordinal);
            var sharedLimit = item.Relations.Where(r => r.ShareDistinct).Select(r => r.Limit).FirstOrDefault(l => l is not null);
            var sharedCount = 0;
            foreach (var rel in item.Relations)
            {
                if (!joined.Children.TryGetValue(rel, out var list))
                {
                    continue;
                }
                if (!rel.ShareDistinct)
                {
                    picked.AddRange(Filter(rel, list).Select(c => (rel, c)));
                    continue;
                }
                // Shared distinct/limit: filter first (a failing row never claims a slot), then dedupe by value
                // across every ShareDistinct relation in this array, capped by the shared limit.
                foreach (var child in list)
                {
                    if (sharedLimit is int max && sharedCount >= max)
                    {
                        break;
                    }
                    if (rel.Filter is { } rowFilter && !rowFilter.Passes(rel.Child.Schema, child.Row))
                    {
                        continue;
                    }
                    if (rel.DistinctBy is { Length: > 0 } col
                        && !sharedSeen.Add(rel.Child.Schema.RawValue(child.Row, col) ?? ""))
                    {
                        continue;
                    }
                    picked.Add((rel, child));
                    sharedCount++;
                }
            }
            if (picked.Count == 0)
            {
                return;
            }
            w.WriteStartArray(name);
            foreach (var (rel, child) in picked)
            {
                WriteObject(w, rel.Child, child);
            }
            w.WriteEndArray();
        }
        else
        {
            var rel = item.Relations[0];
            if (joined.Children.TryGetValue(rel, out var list))
            {
                var first = Filter(rel, list).FirstOrDefault();
                if (first is not null)
                {
                    w.WritePropertyName(name);
                    WriteObject(w, rel.Child, first);
                }
            }
        }
    }

    /// <summary>
    /// Renders one or more relations sharing a keyed 'as' into a single object: each child's derived key
    /// (via its relation's KeyBySteps, run against the CHILD row) becomes a property, holding either an
    /// array of matching children (default) or a single object (<see cref="RelationItem.KeyedSingle"/>).
    /// Children with no/empty derived key are dropped (and would otherwise collide silently).
    /// </summary>
    private void WriteKeyedRelation(Utf8JsonWriter w, RelationItem item, JoinedRow joined, string? nameOverride = null)
    {
        var (order, groups) = GroupKeyedChildren(item, joined);

        w.WriteStartObject(nameOverride ?? item.Name);
        foreach (var key in order)
        {
            var bucket = groups[key];
            if (item.KeyedSingle)
            {
                var (rel, row) = PickKeyedSingle(bucket);
                w.WritePropertyName(key);
                WriteObject(w, rel.Child, row);
            }
            else
            {
                w.WriteStartArray(key);
                // Entries whose relation sets MergeBy are grouped (by that column's value) and merged
                // into one object per distinct value; everything else renders individually as before.
                var mergeOrder = new List<string>();
                var mergeGroups = new Dictionary<string, List<(RelationPlan Rel, JoinedRow Row)>>(StringComparer.Ordinal);
                foreach (var (rel, row) in bucket)
                {
                    if (rel.MergeBy is { Length: > 0 } mergeCol)
                    {
                        var mergeKey = rel.Child.Schema.RawValue(row.Row, mergeCol) ?? "";
                        if (!mergeGroups.TryGetValue(mergeKey, out var mergeBucket))
                        {
                            mergeBucket = new List<(RelationPlan, JoinedRow)>();
                            mergeGroups[mergeKey] = mergeBucket;
                            mergeOrder.Add(mergeKey);
                        }
                        mergeBucket.Add((rel, row));
                    }
                    else
                    {
                        WriteObject(w, rel.Child, row);
                    }
                }
                foreach (var mergeKey in mergeOrder)
                {
                    WriteMergedObject(w, mergeGroups[mergeKey]);
                }
                w.WriteEndArray();
            }
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// Computes a keyed relation's distinct derived keys, in first-seen order, and which (relation, row)
    /// pairs contributed to each — the shared core of <see cref="WriteKeyedRelation"/> and of a
    /// <c>keysOf</c> field's value (see <see cref="ComputeKeysOfValue"/>), so both always agree on
    /// exactly which keys a keyed relation produces for a given record.
    /// </summary>
    private (List<string> Order, Dictionary<string, List<(RelationPlan Rel, JoinedRow Row)>> Groups) GroupKeyedChildren(RelationItem item, JoinedRow joined)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<(RelationPlan Rel, JoinedRow Row)>>(StringComparer.Ordinal);

        foreach (var rel in item.Relations)
        {
            if (!joined.Children.TryGetValue(rel, out var list))
            {
                continue;
            }
            foreach (var child in Filter(rel, list))
            {
                var key = ComputeKey(rel, child);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                if (!groups.TryGetValue(key, out var bucket))
                {
                    bucket = new List<(RelationPlan, JoinedRow)>();
                    groups[key] = bucket;
                    order.Add(key);
                }
                bucket.Add((rel, child));
            }
        }

        return (order, groups);
    }

    /// <summary>
    /// The value for a <c>keysOf</c> field: the union (first-seen order, deduped) of every distinct key
    /// that each named KEYED relation on this same node actually produces for this record — e.g.
    /// "site_ids" computed from "site_contacts" ∪ "site_properties" so it can never drift from
    /// what those relations actually contain (the bug this field exists to make structurally impossible).
    /// </summary>
    private List<string> ComputeKeysOfValue(NodePlan node, JoinedRow joined, IReadOnlyList<string> relationNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var relationName in relationNames)
        {
            var target = FindKeyedRelationItem(node.RenderItems, relationName);
            if (target is null)
            {
                continue; // already validated at compile time; defensive only
            }
            var (order, _) = GroupKeyedChildren(target, joined);
            foreach (var key in order)
            {
                if (seen.Add(key))
                {
                    result.Add(key);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// True when at least one of the named relations on <paramref name="node"/> has content for this
    /// record after its filters — the same rows the writer would render (for a keyed relation, rows with
    /// a non-empty derived key). Backs an output's <c>requireRelations</c>.
    /// </summary>
    public bool HasAnyRelationContent(NodePlan node, JoinedRow joined, IReadOnlyList<string> relationNames)
    {
        foreach (var relationName in relationNames)
        {
            var item = FindRelationItem(node.RenderItems, relationName);
            if (item is null)
            {
                continue; // already validated at compile time; defensive only
            }
            var hasContent = item.Keyed
                ? GroupKeyedChildren(item, joined).Order.Count > 0
                : item.Relations.Any(rel => joined.Children.TryGetValue(rel, out var list) && Filter(rel, list).Any());
            if (hasContent)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Finds a relation (keyed or not) by its <c>as</c> name, searching into object-groups.</summary>
    public static RelationItem? FindRelationItem(IReadOnlyList<RenderItem> items, string name)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case RelationItem ri when ri.Name == name:
                    return ri;
                case GroupItem gi:
                    var found = FindRelationItem(gi.Items, name);
                    if (found is not null)
                    {
                        return found;
                    }
                    break;
            }
        }
        return null;
    }

    private static RelationItem? FindKeyedRelationItem(IReadOnlyList<RenderItem> items, string name)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case RelationItem { Keyed: true } ri when ri.Name == name:
                    return ri;
                case GroupItem gi:
                    var found = FindKeyedRelationItem(gi.Items, name);
                    if (found is not null)
                    {
                        return found;
                    }
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// Picks which child wins a keyed-single slot: the first row, unless the relation sets
    /// <see cref="RelationPlan.KeyedSingleBestOf"/>, in which case the row with the smallest numeric value
    /// of that column wins (a non-numeric/missing value sorts last). Ties keep the first-seen row.
    /// </summary>
    private static (RelationPlan Rel, JoinedRow Row) PickKeyedSingle(List<(RelationPlan Rel, JoinedRow Row)> bucket)
    {
        var bestOfColumn = bucket[0].Rel.KeyedSingleBestOf;
        if (string.IsNullOrEmpty(bestOfColumn))
        {
            return bucket[0];
        }

        var best = bucket[0];
        var bestValue = double.PositiveInfinity;
        foreach (var entry in bucket)
        {
            var raw = entry.Rel.Child.Schema.RawValue(entry.Row.Row, bestOfColumn);
            var value = double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : double.PositiveInfinity;
            if (value < bestValue)
            {
                best = entry;
                bestValue = value;
            }
        }
        return best;
    }

    /// <summary>
    /// Writes one merged object for a MergeBy group: scalar/lookup fields come from the first row (they're
    /// assumed identical across the group, e.g. contact_type/contact_value — the very thing grouped by);
    /// a nested object-group field (e.g. contact_properties) has every row's fields merged into it, each
    /// row's own keys prefixed per its relation's MergePrefix. A row whose prefix can't be computed is
    /// dropped from the merge (warned, not fatal) — see <see cref="TryComputeMergePrefix"/>.
    /// </summary>
    /// <summary>
    /// Writes one merged object for a MergeBy group. Rows in a group can come from DIFFERENT relations
    /// with DIFFERENT shapes (e.g. merging "email" contacts with "profile" contacts that share a
    /// contact_value: one has email_key/email_hash fields, the other has seg_* fields, and only one of
    /// them may carry a nested relation like household_member) — so this walks the UNION of every
    /// row's own top-level items, not just the first row's, and for a shared item (e.g. contact_properties)
    /// merges every row's OWN version of it, not a version borrowed from whichever row happened to be first.
    /// </summary>
    private void WriteMergedObject(Utf8JsonWriter w, List<(RelationPlan Rel, JoinedRow Row)> group)
    {
        w.WriteStartObject();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, row) in group)
        {
            foreach (var item in rel.Child.RenderItems)
            {
                var name = ItemName(item);
                if (name is null || !seenNames.Add(name))
                {
                    continue; // already written (by an earlier row's item of the same name), or unnamed
                }
                switch (item)
                {
                    case FieldItem fi:
                        WriteField(w, fi.Field, rel.Child, row.Row);
                        break;
                    case GroupItem gi:
                        w.WriteStartObject(gi.Name);
                        // A duplicate row from the SAME relation+prefix (e.g. two historical snapshot rows
                        // for the same customer+email in a source with no distinctBy of its own) would
                        // otherwise write every one of its keys twice; keep only the first such row. Two
                        // DIFFERENT relations (or the same relation with a different per-row prefix, e.g.
                        // one email row per brand) are never deduped against each other here.
                        var seenSourcePrefix = new HashSet<(RelationPlan, string?)>();
                        var contributors = new List<(RelationPlan Rel, JoinedRow Row, GroupItem Group, string? Prefix)>();
                        foreach (var (r2, row2) in group)
                        {
                            var ownGroup = FindGroupItem(r2.Child.RenderItems, gi.Name);
                            if (ownGroup is null)
                            {
                                continue; // this row's own source doesn't have a group by this name at all
                            }
                            if (!TryComputeMergePrefix(r2, row2, out var prefix))
                            {
                                continue; // e.g. an unmapped brand code with no "map" default — already warned
                            }
                            if (!seenSourcePrefix.Add((r2, prefix)))
                            {
                                continue; // duplicate row from a source+prefix already written — keep the first
                            }
                            contributors.Add((r2, row2, ownGroup, prefix));
                        }

                        var writtenArrays = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var (r2, row2, ownGroup, prefix) in contributors)
                        {
                            foreach (var inner in ownGroup.Items)
                            {
                                switch (inner)
                                {
                                    case FieldItem innerFi:
                                    {
                                        var pname = string.IsNullOrEmpty(prefix) ? innerFi.Field.Name : $"{prefix}_{innerFi.Field.Name}";
                                        WriteField(w, innerFi.Field, r2.Child, row2.Row, pname);
                                        break;
                                    }
                                    case RelationItem { AsArray: true, Keyed: false } innerRi:
                                    {
                                        // e.g. a "payment_cards" array nested inside contact_properties: never
                                        // prefixed. Written ONCE, where first seen, as the union of every contributor's
                                        // matches (each joined against its OWN row) — so a parent-level contact merged
                                        // from several brands gets one array of all its distinct cards.
                                        if (writtenArrays.Add(innerRi.Name))
                                        {
                                            var sources = contributors
                                                .SelectMany(c => c.Group.Items.OfType<RelationItem>()
                                                    .Where(ri2 => ri2.AsArray && !ri2.Keyed && ri2.Name == innerRi.Name)
                                                    .Select(ri2 => (Item: ri2, c.Row)))
                                                .ToList();
                                            WriteUnionedArray(w, innerRi.Name, sources);
                                        }
                                        break;
                                    }
                                    case RelationItem innerRi:
                                    {
                                        // A non-array (single/keyed/inline) nested relation can't be unioned, so it
                                        // stays per-row and is prefixed the same as a field.
                                        var pname = string.IsNullOrEmpty(prefix) ? innerRi.Name : $"{prefix}_{innerRi.Name}";
                                        WriteRelation(w, innerRi, row2, pname);
                                        break;
                                    }
                                }
                            }
                        }
                        w.WriteEndObject();
                        break;
                    case RelationItem ri:
                        // Relations join on a column of THIS row (e.g. contact_value = email_address),
                        // which every row in the merge group shares by construction — so it doesn't matter
                        // WHICH row supplies it, just that some row in the group does.
                        WriteRelation(w, ri, row);
                        break;
                }
            }
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// Writes one array holding the union of several (array relation, row) contributions — used for an array
    /// nested inside a merged object-group. Each relation's own filter applies to its own children; a child
    /// row reached through more than one contributor is written once; then each relation's distinctBy
    /// dedupes across the whole union (by column value), and the first relation's limit caps it. Omitted
    /// entirely when nothing survives.
    /// </summary>
    private void WriteUnionedArray(Utf8JsonWriter w, string name, List<(RelationItem Item, JoinedRow Row)> sources)
    {
        var seenRows = new HashSet<string[]>(ReferenceEqualityComparer.Instance);
        var seenDistinct = new HashSet<(string Column, string Value)>();
        var limit = sources.SelectMany(s => s.Item.Relations).Select(r => r.Limit).FirstOrDefault(l => l is not null);
        var picked = new List<(RelationPlan Rel, JoinedRow Child)>();
        foreach (var (item, row) in sources)
        {
            foreach (var rel in item.Relations)
            {
                if (!row.Children.TryGetValue(rel, out var list))
                {
                    continue;
                }
                foreach (var child in list)
                {
                    if (limit is int max && picked.Count >= max)
                    {
                        break;
                    }
                    if (rel.Filter is { } rowFilter && !rowFilter.Passes(rel.Child.Schema, child.Row))
                    {
                        continue;
                    }
                    if (!seenRows.Add(child.Row))
                    {
                        continue;
                    }
                    if (rel.DistinctBy is { Length: > 0 } col
                        && !seenDistinct.Add((col, rel.Child.Schema.RawValue(child.Row, col) ?? "")))
                    {
                        continue;
                    }
                    picked.Add((rel, child));
                }
            }
        }
        if (picked.Count == 0)
        {
            return;
        }
        w.WriteStartArray(name);
        foreach (var (rel, child) in picked)
        {
            WriteObject(w, rel.Child, child);
        }
        w.WriteEndArray();
    }

    private static string? ItemName(RenderItem item) => item switch
    {
        FieldItem fi => fi.Field.Name,
        GroupItem gi => gi.Name,
        RelationItem ri => ri.Name,
        _ => null,
    };

    private static GroupItem? FindGroupItem(IReadOnlyList<RenderItem> items, string name) =>
        items.OfType<GroupItem>().FirstOrDefault(g => g.Name == name);

    /// <summary>Computes a MergeBy row's property-name prefix. Returns false (not fatal) when the
    /// relation's MergePrefix pipeline throws — e.g. a "map" step with no default for an unmapped code —
    /// so the caller drops just that row's contribution instead of failing the whole record.</summary>
    private bool TryComputeMergePrefix(RelationPlan rel, JoinedRow row, out string? prefix)
    {
        if (rel.MergePrefixSteps is not { Count: > 0 } steps)
        {
            prefix = null;
            return true; // no prefix configured -> merge fields unprefixed
        }
        try
        {
            var schema = rel.Child.Schema;
            var start = rel.MergePrefixFromColumn is { Length: > 0 } col ? schema.TypedValue(row.Row, col) : null;
            var ctx = schema.Context(row.Row);
            var value = Pipeline.Execute(steps, start, ctx);
            prefix = ValueConvert.To(value, DataType.String) as string;
            return true;
        }
        catch (Exception ex)
        {
            var key = rel.Child.RecordKey(row.Row);
            _warnings.Add(new FieldWarning(rel.As, "", ex.Message, key));
            prefix = null;
            return false;
        }
    }

    private string? ComputeKey(RelationPlan rel, JoinedRow child)
    {
        try
        {
            var schema = rel.Child.Schema;
            var start = rel.KeyByFromColumn is { Length: > 0 } col ? schema.TypedValue(child.Row, col) : null;
            var ctx = schema.Context(child.Row);
            var value = Pipeline.Execute(rel.KeyBySteps!, start, ctx);
            return ValueConvert.To(value, DataType.String) as string;
        }
        catch (Exception ex)
        {
            // e.g. a "map" step with no default hitting an unrecognized code — drop just this child
            // from the keyed output (same as an empty/null key) rather than failing the whole record.
            var key = rel.Child.RecordKey(child.Row);
            _warnings.Add(new FieldWarning(rel.As, "", ex.Message, key));
            return null;
        }
    }

    /// <summary>
    /// A cheap, structural "how fully populated is this record" score, for <see cref="LoadConfig.RichSampling"/>.
    /// Walks the render tree (same rules as actual rendering: relation filters/distinctBy/limit apply) and
    /// counts every relation placement, at any depth, that has at least one matching child — plus, for each
    /// one, recurses into that child's own subtree, so a record with e.g. a populated payment_cards
    /// AND a populated household_member nested inside a populated site_properties site scores higher
    /// than one with just the site itself. Does not serialize to JSON, so it's cheap enough to run per row.
    /// </summary>
    public static int ComputeRichnessScore(NodePlan node, JoinedRow joined) => ScoreItems(node.RenderItems, joined);

    private static int ScoreItems(IReadOnlyList<RenderItem> items, JoinedRow joined)
    {
        var score = 0;
        foreach (var item in items)
        {
            switch (item)
            {
                case GroupItem gi:
                    score += ScoreItems(gi.Items, joined);
                    break;
                case RelationItem ri:
                    foreach (var rel in ri.Relations)
                    {
                        if (!joined.Children.TryGetValue(rel, out var list))
                        {
                            continue;
                        }
                        var matched = Filter(rel, list).ToList();
                        if (matched.Count == 0)
                        {
                            continue;
                        }
                        score++;
                        foreach (var child in matched)
                        {
                            score += ScoreItems(rel.Child.RenderItems, child);
                        }
                    }
                    break;
            }
        }
        return score;
    }

    /// <summary>Applies a relation's optional distinct-by-column and limit to its joined children.</summary>
    private static IEnumerable<JoinedRow> Filter(RelationPlan rel, List<JoinedRow> children)
    {
        IEnumerable<JoinedRow> seq = children;
        if (rel.Filter is { } rowFilter)
        {
            seq = seq.Where(jr => rowFilter.Passes(rel.Child.Schema, jr.Row));
        }
        if (rel.DistinctBy is { Length: > 0 } col)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            seq = seq.Where(jr => seen.Add(rel.Child.Schema.RawValue(jr.Row, col) ?? ""));
        }
        if (rel.Limit is int limit)
        {
            seq = seq.Take(limit);
        }
        return seq;
    }

    // ----- KeyedMap mode ------------------------------------------------------------------------
    public void WriteKeyedMap(Utf8JsonWriter w, OutputPlan output, IReadOnlyList<string[]> group)
    {
        var node = output.Root;
        var rows = Dedup(node, group, output.Dedup);

        w.WriteStartObject();
        foreach (var row in rows)
        {
            var ctx = node.Schema.Context(row);
            var keyValue = Pipeline.Execute(output.KeyExprSteps!, null, ctx);
            var key = ValueConvert.To(keyValue, DataType.String) as string;
            if (string.IsNullOrEmpty(key))
            {
                continue; // cannot key this row
            }

            w.WritePropertyName(key);
            w.WriteStartObject();
            WriteBody(w, node, new JoinedRow { Row = row });
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static IReadOnlyList<string[]> Dedup(NodePlan node, IReadOnlyList<string[]> group, DedupConfig? dedup)
    {
        if (dedup is null)
        {
            return group;
        }

        var order = new List<string>();
        var best = new Dictionary<string, (string[] Row, string Max)>(StringComparer.Ordinal);
        foreach (var row in group)
        {
            var within = node.Schema.RawValue(row, dedup.Within) ?? "";
            var max = node.Schema.RawValue(row, dedup.KeepMaxOf) ?? "";
            if (!best.TryGetValue(within, out var current))
            {
                best[within] = (row, max);
                order.Add(within);
            }
            else if (string.CompareOrdinal(max, current.Max) > 0)
            {
                best[within] = (row, max);
            }
        }
        return order.Select(k => best[k].Row).ToList();
    }

    // ----- Field emission -----------------------------------------------------------------------
    private void WriteField(Utf8JsonWriter w, FieldPlan field, NodePlan node, string[] row, string? nameOverride = null, JoinedRow? joined = null)
    {
        object? value;
        try
        {
            if (field.KeysOfRelations is { Count: > 0 } relationNames)
            {
                // joined is always supplied for a top-level field render — see Render()'s FieldItem case.
                value = joined is null ? null : ComputeKeysOfValue(node, joined, relationNames).Cast<object?>().ToList();
            }
            else if (field.Coalesce is { Count: > 0 } candidates)
            {
                var (raw, label) = ComputeCoalesceRaw(candidates, node, row);
                if (field.CoalesceEmitLabel)
                {
                    value = label;
                }
                else if (raw is null)
                {
                    value = null;
                }
                else
                {
                    var ctx = node.Schema.Context(row);
                    value = field.Steps.Count > 0
                        ? ValueConvert.To(Pipeline.Execute(field.Steps, raw, ctx), field.OutputType)
                        : ValueConvert.To(raw, field.OutputType);
                }
            }
            else if (field.Lookup is not null)
            {
                value = ComputeLookup(field, node, row);
                if (field.ExtraLookups is { Count: > 0 } extraLookups)
                {
                    value = UnionExtraLookups(value, extraLookups, node, row);
                }
                if (field.Steps.Count > 0 && value is not null)
                {
                    // Post-process a lookup's raw result (e.g. "map" a raw brand code -> site_id).
                    var ctx = node.Schema.Context(row);
                    value = value is IEnumerable en and not string
                        ? en.Cast<object?>().Select(v => ValueConvert.To(Pipeline.Execute(field.Steps, v, ctx), DataType.String)).ToList()
                        : ValueConvert.To(Pipeline.Execute(field.Steps, value, ctx), field.OutputType);
                }
            }
            else
            {
                var ctx = node.Schema.Context(row);
                var start = field.FromColumn is { Length: > 0 } col ? node.Schema.TypedValue(row, col) : null;
                var result = Pipeline.Execute(field.Steps, start, ctx);
                value = ValueConvert.To(result, field.OutputType);

                if (value is not null && field.FromColumn is { Length: > 0 } rawCol
                    && (field.OutputType == DataType.Decimal || field.OutputType == DataType.Double))
                {
                    RecordNumericFormatWarning(node, row, field.Name, rawCol);
                }

                // With emitEmpty, a present-but-empty string cell is kept as "" (TypedValue collapses
                // empty cells to null, so recover the empty string for plain string passthrough fields).
                if (value is null && _emitEmpty && field.OutputType == DataType.String
                    && field.Steps.Count == 0 && field.FromColumn is { Length: > 0 } ec
                    && node.Schema.ColumnIndex(ec) >= 0
                    && node.Schema.RawValue(row, ec) is { Length: 0 })
                {
                    value = "";
                }
            }
        }
        catch (Exception ex)
        {
            if (field.OnError == FieldErrorMode.WarnAndNull)
            {
                var raw = field.FromColumn is { Length: > 0 } wc ? node.Schema.RawValue(row, wc) : null;
                var key = node.RecordKey(row);
                _warnings.Add(new FieldWarning(field.Name, raw ?? "", ex.Message, key));
                return; // omit this field, keep the record
            }
            switch (_onError)
            {
                case RunErrorPolicy.Nullify:
                    return; // omit this field, keep the record
                case RunErrorPolicy.Abort:
                    throw;
                default: // Skip
                    throw new RowSkipException($"field '{field.Name}': {ex.Message}", ex);
            }
        }

        Emit(w, field, value, nameOverride);
    }

    /// <summary>
    /// Flags a successfully-parsed decimal/double whose raw source text was in scientific notation
    /// (e.g. "1.5e-05") — legal and parsed correctly, but unusual enough for a numeric feed
    /// that it's worth reporting back to whoever owns the source data rather than passing through silently.
    /// </summary>
    private void RecordNumericFormatWarning(NodePlan node, string[] row, string fieldName, string rawColumn)
    {
        var raw = node.Schema.RawValue(row, rawColumn);
        if (raw is null || raw.IndexOf('e', StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }
        var key = node.RecordKey(row);
        _warnings.Add(new FieldWarning(fieldName, raw, "scientific notation in a numeric column", key));
    }

    /// <summary>Walks a coalesce field's candidates in order, returning the first non-empty raw value (and its label).</summary>
    private static (string? Raw, string? Label) ComputeCoalesceRaw(IReadOnlyList<CoalesceCandidatePlan> candidates, NodePlan node, string[] row)
    {
        foreach (var c in candidates)
        {
            string? raw;
            if (c.FromColumn is { Length: > 0 } col)
            {
                raw = node.Schema.RawValue(row, col);
            }
            else
            {
                var lp = c.Lookup!;
                var key = node.Schema.RawValue(row, lp.ParentKeyColumn) ?? "";
                raw = lp.Index.TryGetValue(key, out var vals) && vals.Count > 0 ? vals[0] : null;
            }
            if (!string.IsNullOrEmpty(raw))
            {
                return (raw, c.Label);
            }
        }
        return (null, null);
    }

    private static object? ComputeLookup(FieldPlan field, NodePlan node, string[] row) =>
        ComputeLookupValue(field.Lookup!, field.OutputType, node, row);

    private static object? ComputeLookupValue(LookupPlan lp, DataType outputType, NodePlan node, string[] row)
    {
        var key = node.Schema.RawValue(row, lp.ParentKeyColumn) ?? "";
        if (lp.IsExists)
        {
            return lp.Index.ContainsKey(key);
        }
        if (!lp.Index.TryGetValue(key, out var raw) || raw.Count == 0)
        {
            return null;
        }

        IEnumerable<string> values = raw;
        if (lp.Distinct)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            values = raw.Where(seen.Add);
        }
        if (lp.Limit is int limit)
        {
            values = values.Take(limit);
        }

        if (lp.Single)
        {
            var first = values.FirstOrDefault();
            return first is null ? null : ValueConvert.To(first, outputType);
        }
        return values.Cast<object?>().ToList();
    }

    /// <summary>Unions extra lookups' results into a primary lookup's array value — see <c>FieldConfig.Lookups</c>.</summary>
    private static object? UnionExtraLookups(object? primary, IReadOnlyList<LookupPlan> extraLookups, NodePlan node, string[] row)
    {
        var combined = primary switch
        {
            null => new List<object?>(),
            IEnumerable en and not string => en.Cast<object?>().ToList(),
            _ => new List<object?> { primary },
        };
        var seen = new HashSet<string>(combined.Select(v => v?.ToString() ?? ""), StringComparer.Ordinal);

        foreach (var extraLp in extraLookups)
        {
            var extraValue = ComputeLookupValue(extraLp, DataType.String, node, row);
            var extraValues = extraValue switch
            {
                null => Enumerable.Empty<object?>(),
                IEnumerable en and not string => en.Cast<object?>(),
                _ => new[] { extraValue },
            };
            foreach (var v in extraValues)
            {
                if (seen.Add(v?.ToString() ?? ""))
                {
                    combined.Add(v);
                }
            }
        }

        return combined;
    }

    private void Emit(Utf8JsonWriter w, FieldPlan field, object? value, string? nameOverride = null)
    {
        var name = nameOverride ?? field.Name;

        if (value is null)
        {
            if (field.EmitNull)
            {
                w.WriteNull(name); // explicit JSON null, e.g. a deliberately blanked-out "properties"
            }
            return; // omit otherwise (the long-standing default)
        }

        // Array value (split / array lookup): omit when empty, else write a JSON array.
        if (value is IEnumerable enumerable && value is not string)
        {
            var elements = enumerable.Cast<object?>().ToList();
            if (elements.Count == 0)
            {
                return;
            }
            w.WriteStartArray(name);
            foreach (var el in elements)
            {
                WriteScalar(w, el);
            }
            w.WriteEndArray();
            return;
        }

        switch (field.OutputType)
        {
            case DataType.Integer:
                w.WriteNumber(name, (long)value);
                break;
            case DataType.Decimal:
                w.WriteNumber(name, (decimal)value);
                break;
            case DataType.Double:
                w.WriteNumber(name, (double)value);
                break;
            case DataType.Bool:
                w.WriteBoolean(name, (bool)value);
                break;
            case DataType.Date:
                w.WriteString(name, ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case DataType.DateTime:
                w.WriteString(name, ((DateTime)value).ToString(Iso8601Format, CultureInfo.InvariantCulture));
                break;
            default: // String (and anything coerced to text)
                var s = value as string ?? value.ToString() ?? "";
                if (s.Length == 0 && !_emitEmpty) return; // omit empty unless emitEmpty
                w.WriteString(name, s);
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter w, object? el)
    {
        switch (el)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case long l: w.WriteNumberValue(l); break;
            case int i: w.WriteNumberValue(i); break;
            case decimal d: w.WriteNumberValue(d); break;
            case double db: w.WriteNumberValue(db); break;
            case DateTime dt: w.WriteStringValue(dt.ToString(Iso8601Format, CultureInfo.InvariantCulture)); break;
            default: w.WriteStringValue(el.ToString() ?? ""); break;
        }
    }
}
