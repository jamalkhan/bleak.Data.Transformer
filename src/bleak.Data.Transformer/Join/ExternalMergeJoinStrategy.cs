using bleak.Data.InfiniteDataLoader;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Engine;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Join;

/// <summary>
/// Disk-backed bounded-memory join: the primary and its direct children are each external-sorted by the
/// join key and merge-joined in a single streaming pass, so neither need fit in RAM. Keys are compared as
/// ordinal strings, so single-column, composite (multi-column), and non-numeric keys are all supported.
/// Levels below the top (grandchildren+) are nested via in-memory indexes (logged). All direct children of
/// a node must join on the same parent key column(s) so the primary can be sorted once.
/// </summary>
public sealed class ExternalMergeJoinStrategy : IJoinStrategy
{
    private readonly int _chunkSize;
    private readonly Action<string>? _log;

    public ExternalMergeJoinStrategy(int chunkSize = 200_000, Action<string>? log = null)
    {
        _chunkSize = chunkSize;
        _log = log;
    }

    public IEnumerable<IReadOnlyList<string[]>> Group(NodePlan root, string groupByColumn)
    {
        var schema = root.Schema;
        var cols = new[] { groupByColumn };
        string Key(string[] r) => JoinKeys.Composite(schema, r, cols);

        var sorted = ExternalSorter.SortByStringKey(root.Reader.ReadRows(), Key, _chunkSize, log: Tag("group"));

        // Group consecutive equal keys (the stream is sorted by Key).
        using var e = sorted.GetEnumerator();
        if (!e.MoveNext())
        {
            yield break;
        }
        var currentKey = Key(e.Current);
        var group = new List<string[]> { e.Current };
        while (e.MoveNext())
        {
            var k = Key(e.Current);
            if (k == currentKey)
            {
                group.Add(e.Current);
            }
            else
            {
                yield return group;
                group = new List<string[]> { e.Current };
                currentKey = k;
            }
        }
        yield return group;
    }

    public IEnumerable<JoinedRow> Join(NodePlan root)
    {
        if (root.Children.Count == 0)
        {
            foreach (var row in root.Reader.ReadRows())
            {
                yield return new JoinedRow { Row = row };
            }
            yield break;
        }

        // All direct children must join on the same parent key column(s) AND the same case-sensitivity —
        // the primary is sorted (and keyed) once, shared by every cursor.
        var signatures = root.Children
            .Select(r => string.Join(",", r.ParentKeyColumns) + "\u0001" + r.CaseInsensitiveJoin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (signatures.Count > 1)
        {
            throw new EtlConfigException(
                $"externalMerge requires all direct children of '{root.SourceAlias}' to join on the same parent " +
                $"key(s) with the same caseInsensitiveJoin setting; found [{string.Join(" | ", signatures)}]. " +
                "Use joinStrategy 'hashInMemory' for this output.");
        }

        var parentCols = root.Children[0].ParentKeyColumns;
        var caseInsensitive = root.Children[0].CaseInsensitiveJoin;
        string PrimaryKey(string[] r) => JoinKeys.Composite(root.Schema, r, parentCols, caseInsensitive);

        var sortedPrimary = ExternalSorter.SortByStringKey(root.Reader.ReadRows(), PrimaryKey, _chunkSize, log: Tag("primary"));

        var cursors = new List<ChildCursor>();
        foreach (var rel in root.Children)
        {
            var childSchema = rel.Child.Schema;
            var childCols = rel.ChildKeyColumns;
            string ChildKey(string[] r) => JoinKeys.Composite(childSchema, r, childCols, rel.CaseInsensitiveJoin);

            var sortedChild = ExternalSorter.SortByStringKey(rel.Child.Reader.ReadRows(), ChildKey, _chunkSize, log: Tag(rel.As));
            var nester = InMemoryNester.ForSubtree(rel.Child); // grandchildren (if any) indexed in RAM
            if (rel.Child.Children.Count > 0)
            {
                _log?.Invoke($"externalMerge: levels below '{rel.As}' are nested using in-memory indexes.");
            }
            cursors.Add(new ChildCursor(rel, sortedChild.GetEnumerator(), ChildKey, nester));
        }

        try
        {
            foreach (var primaryRow in sortedPrimary)
            {
                var pk = PrimaryKey(primaryRow);
                var joined = new JoinedRow { Row = primaryRow };

                foreach (var cursor in cursors)
                {
                    while (cursor.HasCurrent && string.CompareOrdinal(cursor.CurrentKey, pk) < 0)
                    {
                        cursor.MoveNext(); // orphan child: no matching parent
                    }

                    var built = new List<JoinedRow>();
                    while (cursor.HasCurrent && cursor.CurrentKey == pk)
                    {
                        built.Add(cursor.Nester.Build(cursor.Rel.Child, cursor.Current));
                        cursor.MoveNext();
                        if (cursor.Rel.Cardinality == Cardinality.One)
                        {
                            while (cursor.HasCurrent && cursor.CurrentKey == pk)
                            {
                                cursor.MoveNext(); // drop extra matches for a one-relation
                            }
                            break;
                        }
                    }
                    joined.Children[cursor.Rel] = built;
                }

                yield return joined;
            }
        }
        finally
        {
            foreach (var cursor in cursors)
            {
                cursor.Dispose();
            }
        }
    }

    private Action<string>? Tag(string label) => _log is null ? null : m => _log($"{label}: {m}");

    /// <summary>A forward cursor over one external-sorted child stream, plus its in-RAM sub-nester.</summary>
    private sealed class ChildCursor : IDisposable
    {
        private readonly IEnumerator<string[]> _enumerator;
        private readonly Func<string[], string> _key;

        public RelationPlan Rel { get; }
        public InMemoryNester Nester { get; }
        public bool HasCurrent { get; private set; }
        public string[] Current { get; private set; } = Array.Empty<string>();
        public string CurrentKey { get; private set; } = "";

        public ChildCursor(RelationPlan rel, IEnumerator<string[]> enumerator, Func<string[], string> key, InMemoryNester nester)
        {
            Rel = rel;
            _enumerator = enumerator;
            _key = key;
            Nester = nester;
            MoveNext();
        }

        public void MoveNext()
        {
            HasCurrent = _enumerator.MoveNext();
            if (HasCurrent)
            {
                Current = _enumerator.Current;
                CurrentKey = _key(Current);
            }
        }

        public void Dispose() => _enumerator.Dispose();
    }
}
