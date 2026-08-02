namespace ModelSync.Core;

/// <summary>The cardinality of a property, following the streamlined MOF metamodel.</summary>
public enum PropertyCardinality
{
    Single,
    UnorderedSet,
    List,
    Map
}

/// <summary>
/// A node of an ordered property. Nodes have a stable identity independent of
/// their value and position; removal only tombstones a node so that later
/// operations anchored on it stay executable and replicas converge.
/// </summary>
public sealed class ListNode
{
    public ListNode(string itemId, PropertyValue value)
    {
        ItemId = itemId;
        Value = value;
    }

    public string ItemId { get; }
    public PropertyValue Value { get; internal set; }
    public bool IsDeleted { get; internal set; }
}

/// <summary>
/// The state of a single property. A property adopts the cardinality of the
/// first operation that touches it; the underlying stores are kept separate so
/// replays never lose information.
/// </summary>
public sealed class PropertyState
{
    private readonly List<ListNode> _chain = new();
    private readonly Dictionary<string, ListNode> _nodesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyValue> _set = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyValue> _map = new(StringComparer.Ordinal);

    public PropertyState(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public PropertyCardinality Cardinality { get; private set; } = PropertyCardinality.Single;

    public PropertyValue? SingleValue { get; private set; }

    public IReadOnlyCollection<PropertyValue> SetValues => _set.Values;
    public IReadOnlyDictionary<string, PropertyValue> MapValues => _map;

    /// <summary>Alive list items in order.</summary>
    public IReadOnlyList<ListNode> ListItems => _chain.Where(n => !n.IsDeleted).ToList();

    /// <summary>All list nodes, including tombstones, in chain order.</summary>
    public IReadOnlyList<ListNode> ListNodes => _chain;

    public bool ContainsSetValue(PropertyValue value) => _set.ContainsKey(value.MembershipKey);

    public ListNode? FindNode(string itemId) => _nodesById.GetValueOrDefault(itemId);

    public void Set(PropertyValue value)
    {
        Cardinality = PropertyCardinality.Single;
        SingleValue = value;
    }

    public void Unset()
    {
        Cardinality = PropertyCardinality.Single;
        SingleValue = null;
    }

    public void AddSetItem(PropertyValue value)
    {
        Cardinality = PropertyCardinality.UnorderedSet;
        _set[value.MembershipKey] = value;
    }

    public void RemoveSetItem(PropertyValue value)
    {
        Cardinality = PropertyCardinality.UnorderedSet;
        _set.Remove(value.MembershipKey);
    }

    public void PutMapEntry(string key, PropertyValue value)
    {
        Cardinality = PropertyCardinality.Map;
        _map[key] = value;
    }

    public void RemoveMapEntry(string key)
    {
        Cardinality = PropertyCardinality.Map;
        _map.Remove(key);
    }

    /// <summary>
    /// Inserts a list item after the anchor (null anchor = head). If the item
    /// already exists it is relinked instead, which is what makes re-executed
    /// resolution operations deterministic ("the winner moves back in front").
    /// An unknown anchor falls back to appending at the tail.
    /// </summary>
    public void InsertListItem(string itemId, PropertyValue value, string? afterItemId)
    {
        Cardinality = PropertyCardinality.List;

        if (_nodesById.TryGetValue(itemId, out var existing))
        {
            _chain.Remove(existing);
            existing.Value = value;
            Place(existing, afterItemId);
            return;
        }

        var node = new ListNode(itemId, value);
        _nodesById[itemId] = node;
        Place(node, afterItemId);
    }

    public void RemoveListItem(string itemId)
    {
        Cardinality = PropertyCardinality.List;
        if (_nodesById.TryGetValue(itemId, out var node))
        {
            node.IsDeleted = true;
        }
    }

    /// <summary>
    /// The closest non-deleted item preceding <paramref name="itemId"/> in the
    /// chain, or null when the item would re-anchor at the head. Used to
    /// re-anchor inserts whose anchor was deleted concurrently.
    /// </summary>
    public string? FirstAlivePredecessor(string itemId)
    {
        string? candidate = null;
        foreach (var node in _chain)
        {
            if (node.ItemId == itemId)
            {
                return candidate;
            }

            if (!node.IsDeleted)
            {
                candidate = node.ItemId;
            }
        }

        return candidate;
    }

    public string? LastAliveItemId()
    {
        for (var i = _chain.Count - 1; i >= 0; i--)
        {
            if (!_chain[i].IsDeleted)
            {
                return _chain[i].ItemId;
            }
        }

        return null;
    }

    private void Place(ListNode node, string? afterItemId)
    {
        if (afterItemId is null)
        {
            _chain.Insert(0, node);
            return;
        }

        var anchorIndex = _chain.FindIndex(n => n.ItemId == afterItemId);
        if (anchorIndex < 0)
        {
            _chain.Add(node);
            return;
        }

        _chain.Insert(anchorIndex + 1, node);
    }
}
