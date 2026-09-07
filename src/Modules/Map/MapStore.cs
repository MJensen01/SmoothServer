using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SmoothServer.Map
{
    /// <summary>One shared map pin. Mirrors the fields of Minimap.PinData that matter.</summary>
    internal sealed class SharedPin
    {
        public string Name = "";
        public Vector3 Pos;
        public int Type;
        public bool Checked;
        public long OwnerId;

        public string Key
        {
            get
            {
                return Type + "|" + Mathf.RoundToInt(Pos.x) + "|" + Mathf.RoundToInt(Pos.z) + "|" + (Name ?? "");
            }
        }
    }

    /// <summary>
    /// The authoritative shared-exploration bitset plus the shared pin list.
    ///
    /// Pure data: no MonoBehaviour, no Minimap, no Harmony - so the whole store, the delta
    /// merge and the ServerSideMap import can be exercised headlessly (see MapSelfTestModule).
    ///
    /// Two deliberate departures from Mydayyy's ServerSideMap, whose format this replaces:
    ///
    ///   * NO HARDCODED MAP SIZE. ServerSideMap pins 2048 in a constant with a source comment
    ///     "TODO: Find out where to retrieve this from"; any mod that changes Minimap's texture
    ///     size gives its users an IndexOutOfRangeException in OnClientExplore (its GH #67/#68).
    ///     Ours reads Minimap.m_textureSize at runtime on the client, stamps it into the file
    ///     header, and refuses to merge a peer whose size disagrees.
    ///   * BIT-PACKED, THEN COMPRESSED. ServerSideMap persists one BYTE per pixel, uncompressed:
    ///     the live NEWLAND file is 4,194,316 B for a 2048x2048 map. One bit per pixel is 8x
    ///     smaller before ZPackage.WriteCompressed (the codec vanilla itself uses for map blobs
    ///     in Minimap.GetMapData) even looks at it.
    ///
    /// Bit index is y * mapSize + x, matching Minimap.m_explored and ServerSideMap's array, so
    /// the import is a straight re-pack with no coordinate translation.
    /// </summary>
    internal sealed class MapStore
    {
        internal const int Magic = 0x5353_4D50;   // 'SSMP'
        internal const int Version = 1;

        /// <summary>Bytes of bitset per delta/full-sync chunk (512 pixels).</summary>
        internal const int ChunkBytes = 64;

        /// <summary>Bytes of bitset per full-sync message (32 KB raw -> 16 messages at 2048^2).</summary>
        internal const int FullChunkBytes = 32 * 1024;

        public int MapSize { get; private set; }
        public byte[] Bits { get; private set; }
        public readonly List<SharedPin> Pins = new List<SharedPin>();

        public bool Sized => MapSize > 0 && Bits != null;

        public MapStore() { }

        public MapStore(int mapSize) { Resize(mapSize); }

        public void Resize(int mapSize)
        {
            if (mapSize <= 0) throw new ArgumentException("mapSize must be positive");
            MapSize = mapSize;
            long pixels = (long)mapSize * mapSize;
            Bits = new byte[(pixels + 7) / 8];
        }

        public int PixelCount { get { return MapSize * MapSize; } }

        public bool Get(int index)
        {
            if (Bits == null || index < 0 || index >= PixelCount) return false;
            return (Bits[index >> 3] & (1 << (index & 7))) != 0;
        }

        /// <summary>Sets a bit. Returns true if it was not already set.</summary>
        public bool Set(int index)
        {
            if (Bits == null || index < 0 || index >= PixelCount) return false;
            int b = index >> 3, m = 1 << (index & 7);
            if ((Bits[b] & m) != 0) return false;
            Bits[b] = (byte)(Bits[b] | m);
            return true;
        }

        public int Explored()
        {
            if (Bits == null) return 0;
            int n = 0;
            for (int i = 0; i < Bits.Length; i++)
            {
                int v = Bits[i];
                while (v != 0) { n += v & 1; v >>= 1; }
            }
            return n;
        }

        // ---- delta encoding ---------------------------------------------------------------

        /// <summary>
        /// Encode a set of newly-explored pixel indices as sparse 512-bit chunks. Cheap, and it
        /// collapses the burst of pixels a sprinting player generates in one tick into a handful
        /// of chunks - where ServerSideMap sends one 8-byte RPC PER PIXEL, unbatched, and
        /// re-broadcasts each one individually to every other peer.
        /// </summary>
        public ZPackage EncodeDelta(ICollection<int> indices)
        {
            var chunks = new Dictionary<int, byte[]>();
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= PixelCount) continue;
                int byteIdx = idx >> 3;
                int chunk = byteIdx / ChunkBytes;
                byte[] buf;
                if (!chunks.TryGetValue(chunk, out buf)) { buf = new byte[ChunkBytes]; chunks[chunk] = buf; }
                buf[byteIdx - chunk * ChunkBytes] |= (byte)(1 << (idx & 7));
            }

            var inner = new ZPackage();
            inner.Write(MapSize);
            inner.Write(chunks.Count);
            foreach (var kv in chunks)
            {
                inner.Write(kv.Key);
                inner.Write(kv.Value);
            }

            var outer = new ZPackage();
            outer.WriteCompressed(inner);
            return outer;
        }

        /// <summary>
        /// OR-merge a delta package. Returns the indices that were NOT already set (so the
        /// server can rebroadcast only genuinely new pixels), or null on a size mismatch.
        /// </summary>
        public List<int> MergeDelta(ZPackage wire)
        {
            var inner = wire.ReadCompressedPackage();
            int size = inner.ReadInt();
            if (!Sized) Resize(size);
            if (size != MapSize) return null;

            int count = inner.ReadInt();
            var added = new List<int>();
            for (int c = 0; c < count; c++)
            {
                int chunk = inner.ReadInt();
                var buf = inner.ReadByteArray();
                if (buf == null) continue;
                int baseByte = chunk * ChunkBytes;
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] == 0) continue;
                    int byteIdx = baseByte + i;
                    if (byteIdx >= Bits.Length) break;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((buf[i] & (1 << bit)) == 0) continue;
                        int idx = (byteIdx << 3) | bit;
                        if (idx >= PixelCount) break;
                        if (Set(idx)) added.Add(idx);
                    }
                }
            }
            return added;
        }

        // ---- full sync ---------------------------------------------------------------------

        public int FullChunkCount
        {
            get { return Bits == null ? 0 : (Bits.Length + FullChunkBytes - 1) / FullChunkBytes; }
        }

        /// <summary>
        /// One slice of the bitset, compressed. Chunked because a Steam message is capped at
        /// k_cbMaxSteamNetworkingSocketsMessageSizeSend (512 KB) and a fully-explored 2048^2 map
        /// is 512 KB of bits before the header.
        /// </summary>
        public ZPackage EncodeFullChunk(int chunkIndex)
        {
            int off = chunkIndex * FullChunkBytes;
            int len = Math.Min(FullChunkBytes, Bits.Length - off);
            var slice = new byte[Math.Max(0, len)];
            if (len > 0) Buffer.BlockCopy(Bits, off, slice, 0, len);

            var inner = new ZPackage();
            inner.Write(MapSize);
            inner.Write(chunkIndex);
            inner.Write(FullChunkCount);
            inner.Write(off);
            inner.Write(slice);

            var outer = new ZPackage();
            outer.WriteCompressed(inner);
            return outer;
        }

        /// <summary>Decode a full-sync chunk. Returns false on a map-size mismatch.</summary>
        public static bool DecodeFullChunk(ZPackage wire, out int mapSize, out int chunkIndex,
                                           out int chunkCount, out int byteOffset, out byte[] slice)
        {
            var inner = wire.ReadCompressedPackage();
            mapSize = inner.ReadInt();
            chunkIndex = inner.ReadInt();
            chunkCount = inner.ReadInt();
            byteOffset = inner.ReadInt();
            slice = inner.ReadByteArray();
            return slice != null;
        }

        // ---- persistence ---------------------------------------------------------------------

        public ZPackage Serialize()
        {
            var inner = new ZPackage();
            inner.Write(Bits ?? new byte[0]);
            inner.Write(Pins.Count);
            foreach (var p in Pins)
            {
                inner.Write(p.Name ?? "");
                inner.Write(p.Pos.x); inner.Write(p.Pos.y); inner.Write(p.Pos.z);
                inner.Write(p.Type);
                inner.Write(p.Checked);
                inner.Write(p.OwnerId);
            }

            var outer = new ZPackage();
            outer.Write(Magic);
            outer.Write(Version);
            outer.Write(MapSize);
            outer.WriteCompressed(inner);
            return outer;
        }

        public static MapStore Deserialize(ZPackage pkg)
        {
            int magic = pkg.ReadInt();
            if (magic != Magic) throw new Exception("not a SmoothServer map file (magic 0x" + magic.ToString("x8") + ")");
            int version = pkg.ReadInt();
            if (version != Version) throw new Exception("unsupported map file version " + version);
            int mapSize = pkg.ReadInt();

            var store = new MapStore(mapSize);
            var inner = pkg.ReadCompressedPackage();
            var bits = inner.ReadByteArray();
            if (bits != null && bits.Length == store.Bits.Length) store.Bits = bits;
            else if (bits != null) Buffer.BlockCopy(bits, 0, store.Bits, 0, Math.Min(bits.Length, store.Bits.Length));

            int pins = inner.ReadInt();
            for (int i = 0; i < pins; i++)
            {
                var p = new SharedPin();
                p.Name = inner.ReadString();
                float x = inner.ReadSingle(), y = inner.ReadSingle(), z = inner.ReadSingle();
                p.Pos = new Vector3(x, y, z);
                p.Type = inner.ReadInt();
                p.Checked = inner.ReadBool();
                p.OwnerId = inner.ReadLong();
                store.Pins.Add(p);
            }
            return store;
        }

        public void SaveTo(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var bytes = Serialize().GetArray();
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static MapStore LoadFrom(string path)
        {
            return Deserialize(new ZPackage(File.ReadAllBytes(path)));
        }

        // ---- ServerSideMap import ---------------------------------------------------------

        /// <summary>
        /// One-shot import of Mydayyy's ServerSideMap persistence file
        /// (&lt;world&gt;.mod.serversidemap.explored). Its layout, confirmed byte-exact against the
        /// live 4,194,316-byte NEWLAND file, is a raw uncompressed ZPackage:
        ///   int version (3), int mapSize (2048), mapSize^2 bools written ONE BYTE EACH,
        ///   int pinCount, then per pin: string name, Vector3 pos, int type, bool checked.
        /// (Note the live-sync path in that mod bit-packs; only the file does not.)
        /// Returns the number of explored pixels imported.
        /// </summary>
        public static MapStore ImportServerSideMap(byte[] raw, out int exploredPixels, out int version)
        {
            var pkg = new ZPackage(raw);
            version = pkg.ReadInt();
            int mapSize = pkg.ReadInt();
            if (mapSize <= 0 || (long)mapSize * mapSize > 64L * 1024 * 1024)
                throw new Exception("implausible mapSize " + mapSize + " in ServerSideMap file");

            var store = new MapStore(mapSize);
            int pixels = mapSize * mapSize;
            int count = 0;
            for (int i = 0; i < pixels; i++)
            {
                if (pkg.ReadBool()) { store.Set(i); count++; }
            }

            int pinCount = 0;
            try { pinCount = pkg.ReadInt(); } catch { pinCount = 0; }
            for (int i = 0; i < pinCount; i++)
            {
                try
                {
                    var p = new SharedPin();
                    p.Name = pkg.ReadString();
                    float x = pkg.ReadSingle(), y = pkg.ReadSingle(), z = pkg.ReadSingle();
                    p.Pos = new Vector3(x, y, z);
                    p.Type = pkg.ReadInt();
                    p.Checked = pkg.ReadBool();
                    store.Pins.Add(p);
                }
                catch { break; }
            }

            exploredPixels = count;
            return store;
        }
    }
}
