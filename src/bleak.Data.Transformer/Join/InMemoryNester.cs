using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Join;

/// <summary>
/// Builds nested <see cref="JoinedRow"/> trees by indexing child sources in memory (keyed by their
/// join column) and recursing. Used directly by the hash-join strategy, and by the external-merge
/// strategy for levels below the streamed top level.
/// </summary>
public sealed class InMemoryNester
{
    // One index per relationship in the subtree: childKey -> matching child rows (file order).
    private readonly Dictionary<RelationPlan, Dictionary<string, List<string[]>>> _indexes = new();

    /// <summary>Preloads in-memory indexes for every relationship at or below <paramref name="node"/>.</summary>
    public static InMemoryNester ForSubtree(NodePlan node)
    {
        var nester = new InMemoryNester();
        nester.IndexChildren(node);
        return nester;
    }

    private void IndexChildren(NodePlan node)
    {
        foreach (var rel in node.Children)
        {
            var index = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            foreach (var row in rel.Child.Reader.ReadRows())
            {
                var key = JoinKeys.Composite(rel.Child.Schema, row, rel.ChildKeyColumns, rel.CaseInsensitiveJoin);
                if (!index.TryGetValue(key, out var list))
                {
                    list = new List<string[]>();
                    index[key] = list;
                }
                list.Add(row);
            }
            _indexes[rel] = index;
            IndexChildren(rel.Child); // grandchildren
        }
    }

    /// <summary>Builds a joined row for <paramref name="row"/> of <paramref name="node"/>, attaching children.</summary>
    public JoinedRow Build(NodePlan node, string[] row)
    {
        var joined = new JoinedRow { Row = row };
        foreach (var rel in node.Children)
        {
            var key = JoinKeys.Composite(node.Schema, row, rel.ParentKeyColumns, rel.CaseInsensitiveJoin);
            var matches = _indexes[rel].TryGetValue(key, out var list) ? list : null;

            var built = new List<JoinedRow>();
            if (matches is not null)
            {
                foreach (var childRow in matches)
                {
                    built.Add(Build(rel.Child, childRow));
                    if (rel.Cardinality == Config.Cardinality.One)
                    {
                        break; // one: keep only the first match
                    }
                }
            }
            joined.Children[rel] = built;
        }
        return joined;
    }
}
