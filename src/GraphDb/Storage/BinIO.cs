using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GraphDb.Storage;

/// <summary>Growable little-endian binary writer over a byte array (span based, no streams).</summary>
internal sealed class BinWriter
{
    private byte[] _buf;
    private int _pos;

    public BinWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _pos;
    public ReadOnlySpan<byte> Written => new(_buf, 0, _pos);
    public byte[] Buffer => _buf;
    public void Reset() => _pos = 0;
    public void Truncate(int length) => _pos = Math.Min(_pos, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> Reserve(int n)
    {
        if (_pos + n > _buf.Length) Grow(n);
        var s = new Span<byte>(_buf, _pos, n);
        _pos += n;
        return s;
    }

    private void Grow(int n)
    {
        int size = Math.Max(_buf.Length * 2, _pos + n);
        Array.Resize(ref _buf, size);
    }

    public void WriteByte(byte b)
    {
        if (_pos >= _buf.Length) Grow(1);
        _buf[_pos++] = b;
    }

    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), v);
    public void WriteUInt32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void WriteInt64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), v);
    public void WriteDouble(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), v);

    public void WriteVarUInt(ulong v)
    {
        if (_pos + 10 > _buf.Length) Grow(10);
        var buf = _buf;
        int p = _pos;
        while (v >= 0x80)
        {
            buf[p++] = (byte)(v | 0x80);
            v >>= 7;
        }
        buf[p++] = (byte)v;
        _pos = p;
    }

    public void WriteVarInt(long v) => WriteVarUInt((ulong)((v << 1) ^ (v >> 63)));

    public void WriteString(string s)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        if (s.Length < 128 && max < 1024)
        {
            // fast path: encode directly after a 1-2 byte length prefix
            Span<byte> tmp = stackalloc byte[max];
            int n = Encoding.UTF8.GetBytes(s, tmp);
            WriteVarUInt((ulong)n);
            tmp[..n].CopyTo(Reserve(n));
            return;
        }
        int len = Encoding.UTF8.GetByteCount(s);
        WriteVarUInt((ulong)len);
        Encoding.UTF8.GetBytes(s, Reserve(len));
    }

    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    public void WriteValue(in Value v)
    {
        WriteByte((byte)v.Kind);
        switch (v.Kind)
        {
            case ValueKind.Null: break;
            case ValueKind.Boolean: WriteByte((byte)v.Num); break;
            case ValueKind.Integer: WriteVarInt(v.Num); break;
            case ValueKind.Float: WriteInt64(v.Num); break;
            case ValueKind.String: WriteString(v.Str); break;
            case ValueKind.List:
                {
                    var items = v.ListItems;
                    WriteVarUInt((ulong)items.Length);
                    foreach (var x in items) WriteValue(x);
                    break;
                }
            case ValueKind.Map:
                {
                    var m = v.MapItems;
                    WriteVarUInt((ulong)m.Count);
                    foreach (var kv in m)
                    {
                        WriteString(kv.Key);
                        WriteValue(kv.Value);
                    }
                    break;
                }
            default:
                throw new GraphDbException($"Cannot persist value of kind {v.Kind}");
        }
    }

    public void WriteProps(PropertyMap? props)
    {
        if (props is null) { WriteVarUInt(0); return; }
        WriteVarUInt((ulong)props.Count);
        var keys = props.Keys;
        var vals = props.Values;
        for (int i = 0; i < keys.Length; i++)
        {
            WriteVarUInt((ulong)keys[i]);
            WriteValue(vals[i]);
        }
    }

    public void WriteIntArray(int[]? arr)
    {
        if (arr is null) { WriteVarUInt(0); return; }
        WriteVarUInt((ulong)arr.Length);
        foreach (var x in arr) WriteVarUInt((ulong)x);
    }
}

/// <summary>Binary reader over a byte array, optionally refilled from a stream.</summary>
internal sealed class BinReader
{
    private readonly Stream? _stream;
    private byte[] _buf;
    private int _pos;
    private int _len;

    public BinReader(byte[] data, int length)
    {
        _buf = data;
        _len = length;
    }

    public BinReader(Stream stream, int bufferSize = 1 << 20)
    {
        _stream = stream;
        _buf = new byte[bufferSize];
    }

    public bool AtEnd
    {
        get
        {
            if (_pos < _len) return false;
            if (_stream is null) return true;
            return !TryFill(1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int n)
    {
        if (_len - _pos >= n) return;
        if (!TryFill(n)) throw new EndOfStreamException();
    }

    private bool TryFill(int n)
    {
        if (_stream is null) return _len - _pos >= n;
        int remaining = _len - _pos;
        if (n > _buf.Length)
        {
            var nb = new byte[Math.Max(n, _buf.Length * 2)];
            Array.Copy(_buf, _pos, nb, 0, remaining);
            _buf = nb;
        }
        else if (remaining > 0) Array.Copy(_buf, _pos, _buf, 0, remaining);
        _pos = 0;
        _len = remaining;
        while (_len < n)
        {
            int r = _stream.Read(_buf, _len, _buf.Length - _len);
            if (r <= 0) return false;
            _len += r;
        }
        return true;
    }

    public byte ReadByte()
    {
        Ensure(1);
        return _buf[_pos++];
    }

    public int ReadInt32()
    {
        Ensure(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_buf, _pos, 4));
        _pos += 4;
        return v;
    }

    public uint ReadUInt32() => (uint)ReadInt32();

    public long ReadInt64()
    {
        Ensure(8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(_buf, _pos, 8));
        _pos += 8;
        return v;
    }

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Malformed varint");
        }
    }

    public int ReadVarInt32() => checked((int)ReadVarUInt());

    public long ReadVarInt()
    {
        ulong u = ReadVarUInt();
        return (long)(u >> 1) ^ -(long)(u & 1);
    }

    public string ReadString()
    {
        int len = ReadVarInt32();
        Ensure(len);
        var s = Encoding.UTF8.GetString(_buf, _pos, len);
        _pos += len;
        return s;
    }

    public Value ReadValue()
    {
        var kind = (ValueKind)ReadByte();
        switch (kind)
        {
            case ValueKind.Null: return Value.Null;
            case ValueKind.Boolean: return Value.FromBool(ReadByte() != 0);
            case ValueKind.Integer: return Value.FromInt(ReadVarInt());
            case ValueKind.Float: return new Value(ValueKind.Float, ReadInt64(), null);
            case ValueKind.String: return Value.FromString(ReadString());
            case ValueKind.List:
                {
                    int n = ReadVarInt32();
                    var arr = n == 0 ? Array.Empty<Value>() : new Value[n];
                    for (int i = 0; i < n; i++) arr[i] = ReadValue();
                    return Value.FromList(arr);
                }
            case ValueKind.Map:
                {
                    int n = ReadVarInt32();
                    var d = new Dictionary<string, Value>(n);
                    for (int i = 0; i < n; i++) d[ReadString()] = ReadValue();
                    return Value.FromMap(d);
                }
            default: throw new InvalidDataException($"Unknown value tag {kind}");
        }
    }

    public PropertyMap? ReadProps()
    {
        int n = ReadVarInt32();
        if (n == 0) return null;
        var m = new PropertyMap(n);
        for (int i = 0; i < n; i++)
        {
            int k = ReadVarInt32();
            m.Set(k, ReadValue());
        }
        return m;
    }

    public int[]? ReadIntArray()
    {
        int n = ReadVarInt32();
        if (n == 0) return null;
        var arr = new int[n];
        for (int i = 0; i < n; i++) arr[i] = ReadVarInt32();
        return arr;
    }
}

internal static class Crc
{
    /// <summary>Hardware accelerated CRC32C.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        var longs = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var l in longs) crc = BitOperations.Crc32C(crc, l);
        for (int i = longs.Length * 8; i < data.Length; i++) crc = BitOperations.Crc32C(crc, data[i]);
        return ~crc;
    }
}
