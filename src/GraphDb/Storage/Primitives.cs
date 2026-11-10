using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GraphDb.Storage;

/// <summary>Bidirectional name &lt;-&gt; id dictionary for labels, relationship types and property keys.</summary>
internal sealed class TokenTable
{
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();
    /// <summary>Tokens with id below this value have been written to the WAL/snapshot.</summary>
    public int PersistedCount;

    public int Count => _names.Count;

    public int Lookup(string name) => _byName.TryGetValue(name, out var id) ? id : -1;

    public int GetOrCreate(string name)
    {
        if (_byName.TryGetValue(name, out var id)) return id;
        id = _names.Count;
        _names.Add(name);
        _byName[name] = id;
        return id;
    }

    public void DefineAt(int id, string name)
    {
        if (id < _names.Count)
        {
            if (_names[id] != name) throw new GraphDbException($"Corrupt token table: id {id} is '{_names[id]}' not '{name}'");
            return;
        }
        while (_names.Count < id) { var filler = "\0" + _names.Count; _names.Add(filler); }
        _names.Add(name);
        _byName[name] = id;
    }

    public string Name(int id) => _names[id];
    public IReadOnlyList<string> Names => _names;
}

/// <summary>Small property map with linear (SIMD) key search. Most entities have few properties.</summary>
internal sealed class PropertyMap
{
    private int[] _keys;
    private Value[] _vals;
    private int _count;

    public PropertyMap(int capacity = 4)
    {
        _keys = new int[Math.Max(capacity, 1)];
        _vals = new Value[Math.Max(capacity, 1)];
    }

    public int Count => _count;
    public ReadOnlySpan<int> Keys => new(_keys, 0, _count);
    public ReadOnlySpan<Value> Values => new(_vals, 0, _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(int key, out Value value)
    {
        int i = new ReadOnlySpan<int>(_keys, 0, _count).IndexOf(key);
        if (i >= 0) { value = _vals[i]; return true; }
        value = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Get(int key)
    {
        int i = new ReadOnlySpan<int>(_keys, 0, _count).IndexOf(key);
        return i >= 0 ? _vals[i] : Value.Null;
    }

    /// <summary>Sets a value, returns the previous one (Null when absent).</summary>
    public Value Set(int key, Value v)
    {
        int i = new ReadOnlySpan<int>(_keys, 0, _count).IndexOf(key);
        if (i >= 0)
        {
            var old = _vals[i];
            _vals[i] = v;
            return old;
        }
        if (_count == _keys.Length)
        {
            Array.Resize(ref _keys, _count * 2);
            Array.Resize(ref _vals, _count * 2);
        }
        _keys[_count] = key;
        _vals[_count] = v;
        _count++;
        return Value.Null;
    }

    public Value Remove(int key)
    {
        int i = new ReadOnlySpan<int>(_keys, 0, _count).IndexOf(key);
        if (i < 0) return Value.Null;
        var old = _vals[i];
        _count--;
        _keys[i] = _keys[_count];
        _vals[i] = _vals[_count];
        _vals[_count] = default;
        return old;
    }

    public PropertyMap Clone()
    {
        var m = new PropertyMap(_count);
        Array.Copy(_keys, m._keys, _count);
        Array.Copy(_vals, m._vals, _count);
        m._count = _count;
        return m;
    }
}

/// <summary>Growable bitset with fast set-bit iteration (used as label index).</summary>
internal sealed class Bitset
{
    private ulong[] _words = new ulong[16];
    public int Count { get; private set; }

    public ulong[] Words => _words;

    public bool Set(int i)
    {
        int w = i >> 6;
        if (w >= _words.Length) Array.Resize(ref _words, Math.Max(_words.Length * 2, w + 1));
        ulong mask = 1UL << (i & 63);
        if ((_words[w] & mask) != 0) return false;
        _words[w] |= mask;
        Count++;
        return true;
    }

    public bool Clear(int i)
    {
        int w = i >> 6;
        if (w >= _words.Length) return false;
        ulong mask = 1UL << (i & 63);
        if ((_words[w] & mask) == 0) return false;
        _words[w] &= ~mask;
        Count--;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int i)
    {
        int w = i >> 6;
        return w < _words.Length && (_words[w] & (1UL << (i & 63))) != 0;
    }

    /// <summary>Returns the first set bit &gt;= from, or -1.</summary>
    public int NextSetBit(int from)
    {
        int w = from >> 6;
        var words = _words;
        if (w >= words.Length) return -1;
        ulong word = words[w] & (ulong.MaxValue << (from & 63));
        while (true)
        {
            if (word != 0) return (w << 6) + BitOperations.TrailingZeroCount(word);
            if (++w >= words.Length) return -1;
            word = words[w];
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AdjEntry
{
    public int Rel;
    public int Other;
    public int Type;

    public AdjEntry(int rel, int other, int type)
    {
        Rel = rel;
        Other = other;
        Type = type;
    }
}

/// <summary>Adjacency list stored inline in the node record. Entries carry the neighbour and type
/// so that expansion never needs to touch relationship records.</summary>
internal struct AdjList
{
    public AdjEntry[]? Items;
    public int Count;

    public readonly ReadOnlySpan<AdjEntry> Span => Items is null ? default : new(Items, 0, Count);

    public void Add(in AdjEntry e)
    {
        if (Items is null) Items = new AdjEntry[2];
        else if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
        Items[Count++] = e;
    }

    public bool Remove(int relId)
    {
        var items = Items;
        for (int i = Count - 1; i >= 0; i--)
        {
            if (items![i].Rel == relId)
            {
                Count--;
                items[i] = items[Count];
                items[Count] = default;
                return true;
            }
        }
        return false;
    }
}

internal struct NodeRecord
{
    public bool Alive;
    public int[]? Labels; // sorted
    public PropertyMap? Props;
    public AdjList Out;
    public AdjList In;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool HasLabel(int label)
    {
        var l = Labels;
        if (l is null) return false;
        for (int i = 0; i < l.Length; i++) if (l[i] == label) return true;
        return false;
    }
}

internal struct RelRecord
{
    public bool Alive;
    public int Src;
    public int Dst;
    public int Type;
    public PropertyMap? Props;
}

/// <summary>Hash index on (label, property). Unique values stored inline, duplicates spill into a set.</summary>
internal sealed class PropertyIndex
{
    public readonly string Name;
    public readonly int Label;
    public readonly int Key;
    public readonly bool Unique;
    private readonly Dictionary<Value, int> _single = new(ValueEquivalenceComparer.Instance);
    private readonly Dictionary<Value, HashSet<int>> _multi = new(ValueEquivalenceComparer.Instance);

    public PropertyIndex(string name, int label, int key, bool unique)
    {
        Name = name;
        Label = label;
        Key = key;
        Unique = unique;
    }

    public int EntryCount { get; private set; }

    /// <summary>For unique indexes: returns the id of a different node holding the value, or -1.</summary>
    public int Conflict(in Value v, int id)
    {
        if (v.IsNull) return -1;
        if (_single.TryGetValue(v, out var e)) return e != id ? e : -1;
        if (_multi.TryGetValue(v, out var set))
            foreach (var x in set) if (x != id) return x;
        return -1;
    }

    public void Add(in Value v, int id)
    {
        if (v.IsNull) return;
        if (_multi.TryGetValue(v, out var set))
        {
            if (set.Add(id)) EntryCount++;
            return;
        }
        if (_single.TryGetValue(v, out var existing))
        {
            if (existing == id) return;
            _single.Remove(v);
            _multi[v] = [existing, id];
            EntryCount++;
            return;
        }
        _single[v] = id;
        EntryCount++;
    }

    public void Remove(in Value v, int id)
    {
        if (v.IsNull) return;
        if (_single.TryGetValue(v, out var existing))
        {
            if (existing == id) { _single.Remove(v); EntryCount--; }
            return;
        }
        if (_multi.TryGetValue(v, out var set) && set.Remove(id))
        {
            EntryCount--;
            if (set.Count == 1)
            {
                _multi.Remove(v);
                foreach (var x in set) _single[v] = x;
            }
        }
    }

    /// <summary>Copies matching node ids into the list.</summary>
    public void Lookup(in Value v, List<int> into)
    {
        if (v.IsNull) return;
        if (_single.TryGetValue(v, out var id)) { into.Add(id); return; }
        if (_multi.TryGetValue(v, out var set)) into.AddRange(set);
    }

    public int EstimateCount(in Value v)
    {
        if (_single.ContainsKey(v)) return 1;
        return _multi.TryGetValue(v, out var s) ? s.Count : 0;
    }
}
