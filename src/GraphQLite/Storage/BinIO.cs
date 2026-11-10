using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GraphQLite.Storage;

/// <summary>Growable little-endian binary writer over a byte array (span based, no streams).</summary>
internal sealed class BinWriter
{
    private byte[] _buf;
    private int _pos;

    public BinWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _pos;
    public ReadOnlySpan<byte> Written => new(_buf, 0, _pos);
    public Span<byte> WrittenMutable => new(_buf, 0, _pos);
    public void Reset() => _pos = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> Reserve(int n)
    {
        if (_pos + n > _buf.Length) Grow(n);
        var s = new Span<byte>(_buf, _pos, n);
        _pos += n;
        return s;
    }

    private void Grow(int n) => Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));

    public void WriteByte(byte b)
    {
        if (_pos >= _buf.Length) Grow(1);
        _buf[_pos++] = b;
    }

    public void WriteInt32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), v);
    public void WriteInt64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), v);
    public void WriteDouble(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), v);
    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

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
        int len = Encoding.UTF8.GetByteCount(s);
        WriteVarUInt((ulong)len);
        Encoding.UTF8.GetBytes(s, Reserve(len));
    }

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
            case ValueKind.Date:
            case ValueKind.LocalTime:
                WriteVarInt(v.Num);
                break;
            case ValueKind.Time:
                WriteVarInt(v.Num);
                WriteVarInt(v.Zone.FixedOffset);
                break;
            case ValueKind.LocalDateTime:
                WriteInt64(v.Num);
                break;
            case ValueKind.DateTime:
                WriteInt64(v.Num);
                if (v.Zone.Name != null)
                {
                    WriteByte(1);
                    WriteString(v.Zone.Name);
                }
                else
                {
                    WriteByte(0);
                    WriteVarInt(v.Zone.FixedOffset);
                }
                break;
            case ValueKind.Duration:
                {
                    var d = v.AsDuration();
                    WriteVarInt(d.Months);
                    WriteVarInt(d.Days);
                    WriteVarInt(d.Seconds);
                    WriteVarUInt((ulong)d.Nanoseconds);
                    break;
                }
            case ValueKind.Point:
                {
                    var p = v.AsPoint();
                    WriteVarInt(p.Srid);
                    WriteDouble(p.X);
                    WriteDouble(p.Y);
                    WriteByte(p.Z.HasValue ? (byte)1 : (byte)0);
                    if (p.Z.HasValue) WriteDouble(p.Z.Value);
                    break;
                }
            default:
                throw new CypherRuntimeException($"Values of type {v.Kind} cannot be stored as properties");
        }
    }
}

/// <summary>Reader over a span (typically pointing into a memory-mapped page).</summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _s;
    public int Pos;

    public SpanReader(ReadOnlySpan<byte> s, int pos = 0)
    {
        _s = s;
        Pos = pos;
    }

    public byte ReadByte() => _s[Pos++];

    public long ReadInt64()
    {
        long v = BinaryPrimitives.ReadInt64LittleEndian(_s.Slice(Pos, 8));
        Pos += 8;
        return v;
    }

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _s[Pos++];
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
        var s = Encoding.UTF8.GetString(_s.Slice(Pos, len));
        Pos += len;
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
                    if (n == 0) return Value.EmptyList;
                    var arr = new Value[n];
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
            case ValueKind.Date:
            case ValueKind.LocalTime:
                return new Value(kind, ReadVarInt(), null);
            case ValueKind.Time:
                {
                    long t = ReadVarInt();
                    return Value.FromTimeTicks(t, ZoneInfo.FromOffset((int)ReadVarInt()));
                }
            case ValueKind.LocalDateTime: return new Value(kind, ReadInt64(), null);
            case ValueKind.DateTime:
                {
                    long utc = ReadInt64();
                    var zone = ReadByte() == 1 ? ZoneInfo.FromName(ReadString()) : ZoneInfo.FromOffset((int)ReadVarInt());
                    return Value.FromZonedUtc(utc, zone);
                }
            case ValueKind.Duration:
                {
                    long m = ReadVarInt(), d = ReadVarInt(), s = ReadVarInt();
                    return Value.FromDuration(new CypherDuration(m, d, s, (long)ReadVarUInt()));
                }
            case ValueKind.Point:
                {
                    int srid = (int)ReadVarInt();
                    double x = ReadDouble(), y = ReadDouble();
                    double? z = ReadByte() == 1 ? ReadDouble() : null;
                    return Value.FromPoint(new CypherPoint(srid, x, y, z));
                }
            default: throw new InvalidDataException($"Unknown value tag {kind}");
        }
    }
}

internal static class CrcStream
{
    /// <summary>Hardware accelerated CRC32C (state must start at 0xFFFFFFFF and be inverted at the end).</summary>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        var longs = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var l in longs) state = BitOperations.Crc32C(state, l);
        for (int i = longs.Length * 8; i < data.Length; i++) state = BitOperations.Crc32C(state, data[i]);
        return state;
    }
}
