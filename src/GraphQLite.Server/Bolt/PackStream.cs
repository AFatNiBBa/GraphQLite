using System.Buffers.Binary;
using System.Text;

namespace GraphQLite.Server.Bolt;

/// <summary>PackStream markers (Bolt serialization format).</summary>
internal static class Marker
{
    public const byte Null = 0xC0, Float = 0xC1, False = 0xC2, True = 0xC3;
    public const byte Int8 = 0xC8, Int16 = 0xC9, Int32 = 0xCA, Int64 = 0xCB;
    public const byte Bytes8 = 0xCC, Bytes16 = 0xCD, Bytes32 = 0xCE;
    public const byte TinyString = 0x80, String8 = 0xD0, String16 = 0xD1, String32 = 0xD2;
    public const byte TinyList = 0x90, List8 = 0xD4, List16 = 0xD5, List32 = 0xD6;
    public const byte TinyMap = 0xA0, Map8 = 0xD8, Map16 = 0xD9, Map32 = 0xDA;
    public const byte TinyStruct = 0xB0;
}

/// <summary>Writes PackStream values into a growable buffer (one Bolt message at a time).</summary>
internal sealed class PackWriter
{
    private byte[] _buf = new byte[4096];
    private int _len;

    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, _len);
    public int Length => _len;
    public void Reset() => _len = 0;

    private Span<byte> Reserve(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void Byte(byte b)
    {
        if (_len == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
        _buf[_len++] = b;
    }

    public void Null() => Byte(Marker.Null);
    public void Bool(bool b) => Byte(b ? Marker.True : Marker.False);

    public void Int(long v)
    {
        if (v >= -16 && v <= 127) Byte((byte)(sbyte)v);
        else if (v >= sbyte.MinValue && v <= sbyte.MaxValue)
        {
            var s = Reserve(2);
            s[0] = Marker.Int8;
            s[1] = (byte)(sbyte)v;
        }
        else if (v >= short.MinValue && v <= short.MaxValue)
        {
            var s = Reserve(3);
            s[0] = Marker.Int16;
            BinaryPrimitives.WriteInt16BigEndian(s[1..], (short)v);
        }
        else if (v >= int.MinValue && v <= int.MaxValue)
        {
            var s = Reserve(5);
            s[0] = Marker.Int32;
            BinaryPrimitives.WriteInt32BigEndian(s[1..], (int)v);
        }
        else
        {
            var s = Reserve(9);
            s[0] = Marker.Int64;
            BinaryPrimitives.WriteInt64BigEndian(s[1..], v);
        }
    }

    public void Float(double d)
    {
        var s = Reserve(9);
        s[0] = Marker.Float;
        BinaryPrimitives.WriteDoubleBigEndian(s[1..], d);
    }

    public void String(string str)
    {
        int n = Encoding.UTF8.GetByteCount(str);
        Header(n, Marker.TinyString, Marker.String8, Marker.String16, Marker.String32);
        Encoding.UTF8.GetBytes(str, Reserve(n));
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= byte.MaxValue)
        {
            Byte(Marker.Bytes8);
            Byte((byte)bytes.Length);
        }
        else if (bytes.Length <= ushort.MaxValue)
        {
            var s = Reserve(3);
            s[0] = Marker.Bytes16;
            BinaryPrimitives.WriteUInt16BigEndian(s[1..], (ushort)bytes.Length);
        }
        else
        {
            var s = Reserve(5);
            s[0] = Marker.Bytes32;
            BinaryPrimitives.WriteInt32BigEndian(s[1..], bytes.Length);
        }
        bytes.CopyTo(Reserve(bytes.Length));
    }

    public void ListHeader(int n) => Header(n, Marker.TinyList, Marker.List8, Marker.List16, Marker.List32);
    public void MapHeader(int n) => Header(n, Marker.TinyMap, Marker.Map8, Marker.Map16, Marker.Map32);

    public void StructHeader(int fields, byte tag)
    {
        Byte((byte)(Marker.TinyStruct | fields));
        Byte(tag);
    }

    private void Header(int n, byte tiny, byte m8, byte m16, byte m32)
    {
        if (n < 16) Byte((byte)(tiny | n));
        else if (n <= byte.MaxValue)
        {
            Byte(m8);
            Byte((byte)n);
        }
        else if (n <= ushort.MaxValue)
        {
            var s = Reserve(3);
            s[0] = m16;
            BinaryPrimitives.WriteUInt16BigEndian(s[1..], (ushort)n);
        }
        else
        {
            var s = Reserve(5);
            s[0] = m32;
            BinaryPrimitives.WriteInt32BigEndian(s[1..], n);
        }
    }

    /// <summary>Writes a metadata map of CLR values (string, long, int, bool, double, string[], nested maps, lists).</summary>
    public void Meta(IReadOnlyCollection<KeyValuePair<string, object?>> map)
    {
        MapHeader(map.Count);
        foreach (var (k, v) in map)
        {
            String(k);
            MetaValue(v);
        }
    }

    private void MetaValue(object? v)
    {
        switch (v)
        {
            case null: Null(); break;
            case string s: String(s); break;
            case bool b: Bool(b); break;
            case int i: Int(i); break;
            case long l: Int(l); break;
            case double d: Float(d); break;
            case IReadOnlyCollection<KeyValuePair<string, object?>> m: Meta(m); break;
            case System.Collections.IList list:
                ListHeader(list.Count);
                foreach (var x in list) MetaValue(x);
                break;
            default: throw new InvalidOperationException($"Unsupported metadata value {v.GetType()}");
        }
    }
}

/// <summary>A PackStream structure decoded generically (tag and fields).</summary>
internal sealed class PackStruct(byte tag, object?[] fields)
{
    public readonly byte Tag = tag;
    public readonly object?[] Fields = fields;
}

/// <summary>
/// Reads PackStream values from a message buffer. Values decode to CLR objects: null, bool, long, double,
/// string, byte[], List&lt;object?&gt;, Dictionary&lt;string, object?&gt; and <see cref="PackStruct"/>.
/// </summary>
internal ref struct PackReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public bool AtEnd => _pos >= _data.Length;

    private byte Next()
    {
        if (_pos >= _data.Length) throw new BoltProtocolException("Truncated PackStream value");
        return _data[_pos++];
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new BoltProtocolException("Truncated PackStream value");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    /// <summary>Reads the header of a structure: (number of fields, tag).</summary>
    public (int Fields, byte Tag) StructHeader()
    {
        byte m = Next();
        if ((m & 0xF0) != Marker.TinyStruct) throw new BoltProtocolException($"Expected a structure, found marker 0x{m:X2}");
        return (m & 0x0F, Next());
    }

    public object? Read()
    {
        byte m = Next();
        if (m < 0x80) return (long)m;
        if (m >= 0xF0) return (long)(sbyte)m;
        switch (m & 0xF0)
        {
            case Marker.TinyString: return Encoding.UTF8.GetString(Take(m & 0x0F));
            case Marker.TinyList: return ReadList(m & 0x0F);
            case Marker.TinyMap: return ReadMap(m & 0x0F);
            case Marker.TinyStruct: return ReadStruct(m & 0x0F);
        }
        switch (m)
        {
            case Marker.Null: return null;
            case Marker.True: return true;
            case Marker.False: return false;
            case Marker.Float: return BinaryPrimitives.ReadDoubleBigEndian(Take(8));
            case Marker.Int8: return (long)(sbyte)Next();
            case Marker.Int16: return (long)BinaryPrimitives.ReadInt16BigEndian(Take(2));
            case Marker.Int32: return (long)BinaryPrimitives.ReadInt32BigEndian(Take(4));
            case Marker.Int64: return BinaryPrimitives.ReadInt64BigEndian(Take(8));
            case Marker.Bytes8: return Take(Next()).ToArray();
            case Marker.Bytes16: return Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))).ToArray();
            case Marker.Bytes32: return Take(BinaryPrimitives.ReadInt32BigEndian(Take(4))).ToArray();
            case Marker.String8: return Encoding.UTF8.GetString(Take(Next()));
            case Marker.String16: return Encoding.UTF8.GetString(Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))));
            case Marker.String32: return Encoding.UTF8.GetString(Take(BinaryPrimitives.ReadInt32BigEndian(Take(4))));
            case Marker.List8: return ReadList(Next());
            case Marker.List16: return ReadList(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
            case Marker.List32: return ReadList(BinaryPrimitives.ReadInt32BigEndian(Take(4)));
            case Marker.Map8: return ReadMap(Next());
            case Marker.Map16: return ReadMap(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
            case Marker.Map32: return ReadMap(BinaryPrimitives.ReadInt32BigEndian(Take(4)));
        }
        throw new BoltProtocolException($"Unknown PackStream marker 0x{m:X2}");
    }

    private List<object?> ReadList(int n)
    {
        if (n < 0 || n > _data.Length - _pos) throw new BoltProtocolException("Invalid list size");
        var list = new List<object?>(n);
        for (int i = 0; i < n; i++) list.Add(Read());
        return list;
    }

    private Dictionary<string, object?> ReadMap(int n)
    {
        if (n < 0 || n > _data.Length - _pos) throw new BoltProtocolException("Invalid map size");
        var map = new Dictionary<string, object?>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (Read() is not string key) throw new BoltProtocolException("Map keys must be strings");
            map[key] = Read();
        }
        return map;
    }

    private PackStruct ReadStruct(int n)
    {
        byte tag = Next();
        var fields = new object?[n];
        for (int i = 0; i < n; i++) fields[i] = Read();
        return new PackStruct(tag, fields);
    }
}

/// <summary>Malformed Bolt traffic: the connection is closed.</summary>
internal sealed class BoltProtocolException(string message) : Exception(message);
