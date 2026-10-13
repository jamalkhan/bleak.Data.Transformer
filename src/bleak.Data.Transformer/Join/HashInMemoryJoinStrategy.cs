using bleak.Data.Transformer.Engine;

namespace bleak.Data.Transformer.Join;

/// <summary>
/// Streams the primary source row-by-row and attaches children from in-memory indexes. Handles
/// arbitrary nesting depth. Peak memory is the sum of the child/dimension sources (the primary is
/// never buffered). The default strategy; ideal when children fit comfortably in RAM.
/// </summary>
public sealed class HashInMemoryJoinStrategy : IJoinStrategy
{
    public IEnumerable<JoinedRow> Join(NodePlan root)
    {
        var nester = InMemoryNester.ForSubtree(root);
        foreach (var row in root.Reader.ReadRows())
        {
            yield return nester.Build(root, row);
        }
    }

    public IEnumerable<IReadOnlyList<string[]>> Group(NodePlan root, string groupByColumn)
    {
        // Group preserving first-seen key order.
        var order = new List<string>();
        var groups = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        foreach (var row in root.Reader.ReadRows())
        {
            var key = root.Schema.RawValue(row, groupByColumn) ?? "";
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string[]>();
                groups[key] = list;
                order.Add(key);
            }
            list.Add(row);
        }

        foreach (var key in order)
        {
            yield return groups[key];
        }
    }
}
