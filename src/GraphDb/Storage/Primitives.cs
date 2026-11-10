using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GraphDb.Storage;

internal enum TokenKind : byte { Label = 0, RelType = 1, PropKey = 2 }

/// <summary>Bidirectional name &lt;-&gt; id dictionary. Lock-free reads, appends under a lock.</summary>
internal sealed class TokenTable
{
    private readonly ConcurrentDictionary<string, int> _byName = new(StringComparer.Ordinal);
    private string[] _names = new string[64];
    private int _count;
    private readonly object _lock = new();
    /// <summary>Tokens with id below this value are stored in the database file.</summary>
    public int PersistedCount;

    public int Count => Volatile.Read(ref _count);

    public int Lookup(string name) => _byName.TryGetValue(name, out var id) ? id : -1;

    public int GetOrCreate(string name)
    {
        if (_byName.TryGetValue(name, out var id)) return id;
        lock (_lock)
        {
            if (_byName.TryGetValue(name, out id)) return id;
            id = _count;
            Append(name);
            return id;
        }
    }

    private void Append(string name)
    {
        var names = _names;
        if (_count == names.Length)
        {
            var nn = new string[names.Length * 2];
            Array.Copy(names, nn, names.Length);
            Volatile.Write(ref _names, nn);
            names = nn;
        }
        names[_count] = name;
        _byName[name] = _count;
        Volatile.Write(ref _count, _count + 1);
    }

    public void DefineAt(int id, string name)
    {
        lock (_lock)
        {
            if (id < _count)
            {
                if (_names[id] != name) throw new GraphDbException($"Corrupt token table: id {id} is '{_names[id]}' not '{name}'");
                return;
            }
            while (_count < id) Append("\0" + _count);
            Append(name);
        }
    }

    public string Name(int id) => Volatile.Read(ref _names)[id];

    public IEnumerable<string> Names
    {
        get
        {
            int n = Count;
            var names = Volatile.Read(ref _names);
            for (int i = 0; i < n; i++) yield return names[i];
        }
    }
}

/// <summary>Small managed property map used while building / modifying property blobs.</summary>
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

    public bool TryGet(int key, out Value value)
    {
        int i = new ReadOnlySpan<int>(_keys, 0, _count).IndexOf(key);
        if (i >= 0) { value = _vals[i]; return true; }
        value = default;
        return false;
    }

    public Value Get(int key) => TryGet(key, out var v) ? v : Value.Null;

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
}

/// <summary>Adjacency entry stored in adjacency blocks: relationship, neighbour and type (20 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AdjEntry
{
    public long Rel;
    public long Other;
    public int Type;

    public AdjEntry(long rel, long other, int type)
    {
        Rel = rel;
        Other = other;
        Type = type;
    }
}

internal static class BitUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Log2Ceil(int v) => v <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(v - 1));
}
