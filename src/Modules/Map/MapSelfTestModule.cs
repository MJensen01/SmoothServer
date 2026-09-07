using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace SmoothServer.Map
{
    /// <summary>
    /// Headless unit tests for the SharedMap store. Runs at load on the dedicated server, needs
    /// no client, no Minimap and no Unity rendering - the store is pure data by design.
    ///
    /// Covers: bit set/get, delta round trip (encode -> merge -> exact index set), the
    /// "already set" half of the merge (a re-sent chunk must report nothing new), full-chunk
    /// slicing, persistence round trip (bit-packed + WriteCompressed), and the real
    /// ServerSideMap import file if one is present next to the world.
    /// </summary>
    internal sealed class MapSelfTestModule : FeatureModule
    {
        public override string Name => "MapSelfTest";
        public override ModuleSide Side => ModuleSide.Server;
        public override string Section => "MapSelfTest";
        public override bool DefaultEnabled => true;
        protected override string EnabledDescription =>
            "Run the SharedMap store unit tests at startup and log one PASS/FAIL line.";

        private string _result = "not run";

        protected override void ApplyPatches()
        {
            var fails = new List<string>();
            var notes = new List<string>();

            try
            {
                // --- bitset ---
                var s = new MapStore(64);
                if (s.Bits.Length != 512) fails.Add("bitset length " + s.Bits.Length + " != 512");
                if (!s.Set(1000)) fails.Add("Set(1000) said already-set");
                if (s.Set(1000)) fails.Add("Set(1000) twice reported new");
                if (!s.Get(1000)) fails.Add("Get(1000) false after Set");
                if (s.Get(1001)) fails.Add("Get(1001) true, never set");
                if (s.Explored() != 1) fails.Add("Explored()=" + s.Explored() + " != 1");

                // --- delta round trip ---
                var src = new MapStore(256);
                var idx = new HashSet<int>();
                var rnd = new System.Random(1234);
                for (int i = 0; i < 500; i++) idx.Add(rnd.Next(0, 256 * 256));
                var wire = src.EncodeDelta(idx);
                var dst = new MapStore(256);
                var added = dst.MergeDelta(new ZPackage(wire.GetArray()));
                if (added == null) fails.Add("MergeDelta returned null");
                else
                {
                    if (added.Count != idx.Count) fails.Add("delta round trip: " + added.Count + " != " + idx.Count);
                    foreach (var i in idx) if (!dst.Get(i)) { fails.Add("delta lost index " + i); break; }
                    notes.Add("delta " + idx.Count + "px=" + wire.GetArray().Length + "B");
                }

                // --- idempotent re-merge ---
                var again = dst.MergeDelta(new ZPackage(wire.GetArray()));
                if (again == null || again.Count != 0) fails.Add("re-merge reported " + (again == null ? "null" : again.Count.ToString()) + " new");

                // --- full chunk round trip ---
                var full = new MapStore(2048);
                for (int i = 0; i < 2048 * 2048; i += 997) full.Set(i);
                int chunks = full.FullChunkCount;
                var rebuilt = new MapStore(2048);
                int totalWire = 0;
                for (int c = 0; c < chunks; c++)
                {
                    var pkg = new ZPackage(full.EncodeFullChunk(c).GetArray());
                    totalWire += pkg.GetArray().Length;
                    int mapSize, ci, cc, off; byte[] slice;
                    if (!MapStore.DecodeFullChunk(pkg, out mapSize, out ci, out cc, out off, out slice))
                    { fails.Add("full chunk " + c + " decode failed"); break; }
                    if (mapSize != 2048 || ci != c || cc != chunks) fails.Add("full chunk header wrong at " + c);
                    Buffer.BlockCopy(slice, 0, rebuilt.Bits, off, slice.Length);
                }
                if (rebuilt.Explored() != full.Explored())
                    fails.Add("full sync explored " + rebuilt.Explored() + " != " + full.Explored());
                notes.Add("full 2048^2 " + full.Explored() + "px in " + chunks + " chunks=" + totalWire + "B");

                // --- persistence round trip ---
                full.Pins.Add(new SharedPin { Name = "Silver", Pos = new Vector3(1f, 2f, 3f), Type = 3, Checked = true, OwnerId = 7 });
                var tmpFile = Path.Combine(Path.GetTempPath(), "smoothserver-selftest.map");
                full.SaveTo(tmpFile);
                long onDisk = new FileInfo(tmpFile).Length;
                var back = MapStore.LoadFrom(tmpFile);
                File.Delete(tmpFile);
                if (back.MapSize != 2048) fails.Add("persist mapSize " + back.MapSize);
                if (back.Explored() != full.Explored()) fails.Add("persist explored " + back.Explored() + " != " + full.Explored());
                if (back.Pins.Count != 1 || back.Pins[0].Name != "Silver" || !back.Pins[0].Checked)
                    fails.Add("persist pins wrong");
                notes.Add("persist 2048^2=" + onDisk + "B (ServerSideMap would be 4194316B)");

                // --- ServerSideMap import, against the real file if the world has one ---
                // ZNet does not exist yet at plugin Awake, so find the world dir directly.
                string ssm = null;
                try
                {
                    var dir = World.GetWorldSavePath(FileHelpers.FileSource.Local);
                    if (Directory.Exists(dir))
                    {
                        var hits = Directory.GetFiles(dir, "*.mod.serversidemap.explored");
                        if (hits.Length > 0) ssm = hits[0];
                    }
                }
                catch { ssm = null; }
                if (ssm != null)
                {
                    var bytes = File.ReadAllBytes(ssm);
                    int pixels, version;
                    var imported = MapStore.ImportServerSideMap(bytes, out pixels, out version);
                    if (imported.Explored() != pixels) fails.Add("import count mismatch");
                    long expect = 8L + (long)imported.MapSize * imported.MapSize + 4L;
                    notes.Add("import " + Path.GetFileName(ssm) + " v" + version + " " + imported.MapSize +
                              "^2 " + bytes.Length + "B" + (bytes.Length == expect ? "" : " (expected " + expect + "B!)") +
                              " -> " + pixels + "px");
                }
                else
                {
                    // synthetic fixture in ServerSideMap's exact layout
                    var pkg = new ZPackage();
                    pkg.Write(3); pkg.Write(16);
                    for (int i = 0; i < 256; i++) pkg.Write(i % 5 == 0);
                    pkg.Write(0);
                    int pixels, version;
                    var imported = MapStore.ImportServerSideMap(pkg.GetArray(), out pixels, out version);
                    if (version != 3 || imported.MapSize != 16 || pixels != 52)
                        fails.Add("synthetic import v" + version + " size" + imported.MapSize + " px" + pixels);
                    notes.Add("import(synthetic) 16^2 -> " + pixels + "px");
                }
            }
            catch (Exception e)
            {
                fails.Add("threw: " + e.Message);
            }

            _result = fails.Count == 0 ? "PASS" : "FAIL(" + string.Join("; ", fails.ToArray()) + ")";
            Log.LogInfo("[MapSelfTest] " + _result + " - " + string.Join(", ", notes.ToArray()));
            if (fails.Count > 0) throw new Exception(_result);
        }

        public override string StatusDetail() { return _result; }
    }
}
