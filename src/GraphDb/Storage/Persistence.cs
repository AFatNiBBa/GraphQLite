using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GraphDb.Storage;

internal static class CrcStream
{
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        var longs = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var l in longs) state = BitOperations.Crc32C(state, l);
        for (int i = longs.Length * 8; i < data.Length; i++) state = BitOperations.Crc32C(state, data[i]);
        return state;
    }
}

/// <summary>Read-only stream view over the first N bytes of a stream, computing CRC32C on the fly.</summary>
internal sealed class CrcLimitedStream(Stream inner, long limit) : Stream
{
    private long _remaining = limit;
    public uint State = 0xFFFFFFFFu;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        if (buffer.Length > _remaining) buffer = buffer[..(int)_remaining];
        int n = inner.Read(buffer);
        _remaining -= n;
        State = CrcStream.Update(State, buffer[..n]);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Durable storage: a snapshot file plus a write-ahead log (like SQLite WAL mode).
/// <list type="bullet">
/// <item>Each committed transaction appends one checksummed frame to <c>&lt;db&gt;-wal</c>.</item>
/// <item>Checkpoint writes a fresh snapshot atomically (temp file + rename) and truncates the WAL.</item>
/// <item>Recovery loads the snapshot then replays WAL frames newer than the snapshot; a torn tail is discarded.</item>
/// </list>
/// The WAL file is held open exclusively, which also prevents other processes from opening the database.
/// </summary>
internal sealed class DurableStorage : IDisposable
{
    private const ulong SnapshotMagic = 0x31504E5342444700UL; // "\0GDBSNP1"
    private const uint WalMagic = 0x4C415747; // "GWAL"
    private const int FrameHeader = 20;

    private readonly string _dbPath;
    private readonly string _walPath;
    private readonly FileStream _wal;
    private readonly SyncMode _sync;
    private readonly BinWriter _tokenBuf = new(256);
    private readonly byte[] _header = new byte[FrameHeader];

    public long WalSize => _wal.Length;

    private DurableStorage(string dbPath, FileStream wal, SyncMode sync)
    {
        _dbPath = dbPath;
        _walPath = dbPath + "-wal";
        _wal = wal;
        _sync = sync;
    }

    public static DurableStorage Open(string path, GraphStore store, SyncMode sync)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        FileStream wal;
        try
        {
            wal = new FileStream(full + "-wal", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new GraphDbException($"Database '{path}' is locked by another process", ex);
        }
        try
        {
            var ds = new DurableStorage(full, wal, sync);
            ds.LoadSnapshot(store);
            ds.ReplayWal(store);
            store.MarkTokensPersisted();
            return ds;
        }
        catch
        {
            wal.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ snapshot

    private void LoadSnapshot(GraphStore store)
    {
        // a leftover temp file means a checkpoint crashed before the rename: the old snapshot + WAL are valid.
        if (File.Exists(_dbPath + "-tmp")) File.Delete(_dbPath + "-tmp");
        if (!File.Exists(_dbPath)) return;
        using var fs = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        if (fs.Length == 0) return;
        if (fs.Length < 16) throw new GraphDbException("Database file is corrupt (too short)");
        var crcStream = new CrcLimitedStream(fs, fs.Length - 4);
        var r = new BinReader(crcStream);
        if ((ulong)r.ReadInt64() != SnapshotMagic) throw new GraphDbException("Not a GraphDb database file");
        int version = r.ReadInt32();
        if (version != 1) throw new GraphDbException($"Unsupported database format version {version}");
        store.LastTxSeq = r.ReadInt64();
        for (int k = 0; k < 3; k++)
        {
            var t = store.TokensOf((TokenKind)k);
            int n = r.ReadVarInt32();
            for (int i = 0; i < n; i++) t.DefineAt(i, r.ReadString());
        }
        int nodeHigh = r.ReadVarInt32();
        int nodeCount = r.ReadVarInt32();
        if (nodeHigh > store.Nodes.Length) Array.Resize(ref store.Nodes, nodeHigh);
        for (int i = 0; i < nodeCount; i++)
        {
            int id = r.ReadVarInt32();
            var labels = r.ReadIntArray();
            var props = r.ReadProps();
            store.CreateNodeAt(id, labels, props, null);
        }
        store.NodeHigh = nodeHigh;
        int relHigh = r.ReadVarInt32();
        int relCount = r.ReadVarInt32();
        if (relHigh > store.Rels.Length) Array.Resize(ref store.Rels, relHigh);
        for (int i = 0; i < relCount; i++)
        {
            int id = r.ReadVarInt32();
            int src = r.ReadVarInt32();
            int dst = r.ReadVarInt32();
            int type = r.ReadVarInt32();
            var props = r.ReadProps();
            store.CreateRelAt(id, src, dst, type, props, null);
        }
        store.RelHigh = relHigh;
        int ixCount = r.ReadVarInt32();
        for (int i = 0; i < ixCount; i++)
        {
            var name = r.ReadString();
            int label = r.ReadVarInt32();
            int key = r.ReadVarInt32();
            bool unique = r.ReadByte() != 0;
            store.CreateIndex(name, label, key, unique, null);
        }
        if (!r.AtEnd) throw new GraphDbException("Database file is corrupt (trailing data)");
        uint computed = ~crcStream.State;
        Span<byte> tail = stackalloc byte[4];
        fs.ReadExactly(tail);
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != computed)
            throw new GraphDbException("Database file is corrupt (checksum mismatch)");
    }

    /// <summary>Writes a full snapshot atomically and truncates the WAL. Caller must exclude writers.</summary>
    public void Checkpoint(GraphStore store)
    {
        var tmp = _dbPath + "-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            uint crc = 0xFFFFFFFFu;
            var w = new BinWriter(1 << 20);
            void Spill(bool force)
            {
                if (!force && w.Length < (1 << 20)) return;
                crc = CrcStream.Update(crc, w.Written);
                fs.Write(w.Written);
                w.Reset();
            }

            w.WriteInt64((long)SnapshotMagic);
            w.WriteInt32(1);
            w.WriteInt64(store.LastTxSeq);
            for (int k = 0; k < 3; k++)
            {
                var t = store.TokensOf((TokenKind)k);
                w.WriteVarUInt((ulong)t.Count);
                foreach (var name in t.Names) w.WriteString(name);
            }
            w.WriteVarUInt((ulong)store.NodeHigh);
            w.WriteVarUInt((ulong)store.NodeCount);
            var nodes = store.Nodes;
            for (int id = 0; id < store.NodeHigh; id++)
            {
                ref var n = ref nodes[id];
                if (!n.Alive) continue;
                w.WriteVarUInt((ulong)id);
                w.WriteIntArray(n.Labels);
                w.WriteProps(n.Props);
                Spill(false);
            }
            w.WriteVarUInt((ulong)store.RelHigh);
            w.WriteVarUInt((ulong)store.RelCount);
            var rels = store.Rels;
            for (int id = 0; id < store.RelHigh; id++)
            {
                ref var rr = ref rels[id];
                if (!rr.Alive) continue;
                w.WriteVarUInt((ulong)id);
                w.WriteVarUInt((ulong)rr.Src);
                w.WriteVarUInt((ulong)rr.Dst);
                w.WriteVarUInt((ulong)rr.Type);
                w.WriteProps(rr.Props);
                Spill(false);
            }
            var indexes = store.AllIndexes.ToList();
            w.WriteVarUInt((ulong)indexes.Count);
            foreach (var ix in indexes)
            {
                w.WriteString(ix.Name);
                w.WriteVarUInt((ulong)ix.Label);
                w.WriteVarUInt((ulong)ix.Key);
                w.WriteByte(ix.Unique ? (byte)1 : (byte)0);
            }
            Spill(true);
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tail, ~crc);
            fs.Write(tail);
            fs.Flush(true);
        }
        File.Move(tmp, _dbPath, overwrite: true);
        // Frames with seq <= LastTxSeq are now in the snapshot; replay skips them if we crash before truncation.
        _wal.SetLength(0);
        _wal.Flush(true);
        store.MarkTokensPersisted();
    }

    // ------------------------------------------------------------------ WAL

    private void ReplayWal(GraphStore store)
    {
        _wal.Position = 0;
        long goodEnd = 0;
        long length = _wal.Length;
        var header = new byte[FrameHeader];
        byte[] payload = new byte[4096];
        while (goodEnd + FrameHeader <= length)
        {
            _wal.Position = goodEnd;
            if (_wal.Read(header, 0, FrameHeader) != FrameHeader) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != WalMagic) break;
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            long seq = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            if (len <= 0 || goodEnd + FrameHeader + len > length) break;
            if (payload.Length < len) payload = new byte[Math.Max(len, payload.Length * 2)];
            _wal.ReadExactly(payload, 0, len);
            uint state = CrcStream.Update(0xFFFFFFFFu, header.AsSpan(8, 8));
            state = CrcStream.Update(state, payload.AsSpan(0, len));
            if (~state != crc) break;
            if (seq > store.LastTxSeq)
            {
                store.Apply(new BinReader(payload, len));
                store.LastTxSeq = seq;
            }
            goodEnd += FrameHeader + len;
        }
        if (goodEnd != length)
        {
            _wal.SetLength(goodEnd); // discard torn / partial tail
            _wal.Flush(true);
        }
        _wal.Position = goodEnd;
    }

    /// <summary>Appends a transaction frame. Caller holds the write lock.</summary>
    public void Append(GraphStore store, TxLog log, long seq)
    {
        _tokenBuf.Reset();
        store.WriteNewTokens(_tokenBuf);
        var redo = log.Redo!;
        int len = _tokenBuf.Length + redo.Length + 1;
        Span<byte> commit = [(byte)WalOp.Commit];

        BinaryPrimitives.WriteUInt32LittleEndian(_header, WalMagic);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(4), len);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(8), seq);
        uint state = CrcStream.Update(0xFFFFFFFFu, _header.AsSpan(8, 8));
        state = CrcStream.Update(state, _tokenBuf.Written);
        state = CrcStream.Update(state, redo.Written);
        state = CrcStream.Update(state, commit);
        BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(16), ~state);

        long start = _wal.Position;
        try
        {
            _wal.Write(_header);
            _wal.Write(_tokenBuf.Written);
            _wal.Write(redo.Written);
            _wal.Write(commit);
            if (_sync == SyncMode.Off) { }
            else _wal.Flush(_sync == SyncMode.Full);
        }
        catch
        {
            try { _wal.SetLength(start); _wal.Position = start; } catch { /* best effort */ }
            throw;
        }
        store.MarkTokensPersisted();
    }

    public void Flush() => _wal.Flush(true);

    public void Dispose()
    {
        try { _wal.Flush(true); } catch { }
        _wal.Dispose();
    }
}
