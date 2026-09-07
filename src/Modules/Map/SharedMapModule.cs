using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer.Map
{
    /// <summary>
    /// SharedMap - a server-authoritative shared minimap, replacing Mydayyy's ServerSideMap.
    ///
    /// The server owns one explored bitset plus one pin list per world, persisted to
    /// &lt;config&gt;/smoothserver/&lt;world&gt;.map. Clients stream exploration deltas up, the server
    /// merges and fans the genuinely-new pixels back out, and a joining client gets the whole
    /// bitset in compressed chunks.
    ///
    /// What is different from ServerSideMap, and why (all four traced to a real defect in it):
    ///   1. Map size is read from Minimap.m_textureSize at runtime and carried in every message
    ///      and in the file header. ServerSideMap hardcodes 2048 with a "TODO: find out where to
    ///      retrieve this from" comment and hands world-size mods an IndexOutOfRangeException.
    ///   2. Deltas are BATCHED at DeltaHz and bit-packed into sparse 512-pixel chunks.
    ///      ServerSideMap fires one 8-byte RPC per pixel, and the server re-broadcasts each
    ///      single pixel to every other peer, with no throttling anywhere in the path.
    ///   3. The store is bit-packed and ZPackage.WriteCompressed'd. ServerSideMap writes one byte
    ///      per bool, uncompressed: 4,194,316 B on disk for a 2048^2 map.
    ///   4. The full join sync is chunked to stay under Steam's 512 KB message ceiling and is
    ///      compressed; ServerSideMap sends ~524 KB down AND ~524 KB back up, uncompressed.
    ///
    /// What is deliberately kept: server-fed pixels are applied through the *vanilla*
    /// Minimap.Explore(x, y), so they land in the client's own m_explored and get baked into the
    /// .fch profile on the next SaveMapData - a sticky one-directional merge with no separate
    /// overlay layer and no data loss if the module is later turned off.
    ///
    /// A vanilla client is unaffected: it never originates our RPCs and the server never sends it
    /// one. That is why EnforceClientMod defaults to false.
    /// </summary>
    internal sealed class SharedMapModule : FeatureModule
    {
        public override string Name => "SharedMap";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "Map";

        internal const string RpcHello = "SS_MapHello";
        internal const string RpcFull = "SS_MapFull";
        internal const string RpcDelta = "SS_MapDelta";
        internal const string RpcPin = "SS_MapPin";

        private const int PinTypeDeath = 4;   // Minimap.PinType.Death

        // ---- config -----------------------------------------------------------------------

        private ConfigEntry<bool> _shareExploration;
        private ConfigEntry<bool> _sharePins;
        private ConfigEntry<string> _sharedPinTypes;
        private ConfigEntry<bool> _shareDeathPins;
        private ConfigEntry<bool> _excludeTable;
        private ConfigEntry<float> _deltaHz;
        private ConfigEntry<bool> _import;

        internal static bool Active2;
        private static bool _shareExpl = true, _sharePinsV = true, _deathPins, _excludeTableV;
        private static float _hz = 1f;
        private static readonly HashSet<int> AllowedPinTypes = new HashSet<int>();

        // ---- server state -------------------------------------------------------------------

        internal static MapStore Store;
        private static string _storePath;
        private static bool _dirty;
        internal static string ImportSummary;
        private static readonly Dictionary<long, int> PendingFull = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> PendingFullCount = new Dictionary<long, int>();

        // ---- client state -------------------------------------------------------------------

        private static readonly HashSet<int> PendingDelta = new HashSet<int>();
        private static float _deltaTimer, _helloTimer;
        private static bool _helloSent;
        private static bool _applying;          // suppress echo while applying server data
        private static bool _inTableImport;     // inside Minimap.AddSharedMapData
        private static int _clientMapSize;
        private static bool _rpcsRegistered;

        // ---- lifecycle -----------------------------------------------------------------------

        protected override void Bind()
        {
            _shareExploration = BindSynced("ShareExploration", true,
                "Share explored map area between everyone on the server.");
            _sharePins = BindSynced("SharePins", true,
                "Share map pins between everyone on the server.");
            _sharedPinTypes = BindSynced("SharedPinTypes",
                "Icon0,Icon1,Icon2,Icon3,Icon4,Bed,Boss,Hildir1,Hildir2,Hildir3",
                "Comma-separated Minimap.PinType names that propagate. Death is controlled " +
                "separately by ShareDeathPins. Shout/Ping/Player/EventArea/RandomEvent are " +
                "transient and are never shared (vanilla creates them with save=false).");
            _shareDeathPins = BindSynced("ShareDeathPins", false,
                "Also share tombstone (Death) pins. Turn this ON for a group running " +
                "NoVikingLeftBehind's CorpseRunPlus - it makes a dead viking's grave visible to " +
                "the whole party instead of only to the corpse's owner.");
            _excludeTable = BindSynced("ExcludeCartographyTable", false,
                "Do not feed exploration that arrived from a vanilla Cartography Table into the " +
                "shared map. The table uses a completely separate transport (ZDO blobs via " +
                "Minimap.GetSharedMapData/AddSharedMapData, not RPCs), so it coexists with " +
                "SharedMap either way; this only decides whether the table's contents leak into " +
                "the ambient server-wide share.");
            _deltaHz = BindSynced("DeltaHz", 1f,
                "How many times a second a client flushes its newly-explored pixels to the " +
                "server. 1 Hz collapses a sprinting player's pixel burst into one compressed " +
                "message. Higher costs bandwidth for a barely-perceptible latency win.");
            _import = BindSynced("ImportServerSideMapFile", true,
                "One-shot migration: if no <world>.map exists yet but ServerSideMap's " +
                "<world>.mod.serversidemap.explored does, import it. Runs once - after the " +
                "first save our own file exists and this does nothing.");
            ReadConfig();
        }

        private void ReadConfig()
        {
            _shareExpl = _shareExploration.Value;
            _sharePinsV = _sharePins.Value;
            _deathPins = _shareDeathPins.Value;
            _excludeTableV = _excludeTable.Value;
            _hz = Mathf.Clamp(_deltaHz.Value, 0.1f, 20f);
            _importValue = _import.Value;

            AllowedPinTypes.Clear();
            foreach (var raw in (_sharedPinTypes.Value ?? "").Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                try { AllowedPinTypes.Add((int)Enum.Parse(typeof(Minimap.PinType), s, true)); }
                catch { Log.LogWarning("[SharedMap] unknown pin type '" + s + "' in SharedPinTypes"); }
            }
            if (_deathPins) AllowedPinTypes.Add(PinTypeDeath); else AllowedPinTypes.Remove(PinTypeDeath);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == EnabledCfg) Active2 = Applied && Enabled;
            ReadConfig();
        }

        protected override void ApplyPatches()
        {
            var explore = AccessTools.Method(typeof(Minimap), "Explore", new[] { typeof(int), typeof(int) });
            if (explore == null) throw new Exception("Minimap.Explore(int,int) not found");
            var addPin = AccessTools.Method(typeof(Minimap), "AddPin");
            if (addPin == null) throw new Exception("Minimap.AddPin not found");
            var removePin = AccessTools.Method(typeof(Minimap), "RemovePin", new[] { typeof(Minimap.PinData) });
            if (removePin == null) throw new Exception("Minimap.RemovePin(PinData) not found");
            var rrpcCtor = AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            if (rrpcCtor == null) throw new Exception("ZRoutedRpc(bool) constructor not found");

            Harmony.Patch(rrpcCtor, postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(RoutedRpcCtorPostfix)));

            if (SmoothServerPlugin.IsServerSide)
            {
                var load = AccessTools.Method(typeof(ZNet), "LoadWorld");
                if (load == null) throw new Exception("ZNet.LoadWorld not found");
                var save = AccessTools.Method(typeof(ZNet), "SaveWorldThread");
                if (save == null) throw new Exception("ZNet.SaveWorldThread not found");
                Harmony.Patch(load, postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(LoadWorldPostfix)));
                Harmony.Patch(save, postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(SaveWorldPostfix)));
            }
            else
            {
                Harmony.Patch(explore, postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(ExplorePostfix)));
                Harmony.Patch(addPin, postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(AddPinPostfix)));
                Harmony.Patch(removePin, prefix: new HarmonyMethod(typeof(SharedMapModule), nameof(RemovePinPrefix)));

                // Optional: only needed for ExcludeCartographyTable. A missing method is a
                // warning, not a module failure - the rest of SharedMap is unaffected.
                var table = AccessTools.Method(typeof(Minimap), "AddSharedMapData");
                if (table != null)
                    Harmony.Patch(table,
                        prefix: new HarmonyMethod(typeof(SharedMapModule), nameof(TablePrefix)),
                        postfix: new HarmonyMethod(typeof(SharedMapModule), nameof(TablePostfix)));
                else
                    Log.LogWarning("[SharedMap] Minimap.AddSharedMapData not found - " +
                                   "ExcludeCartographyTable will have no effect");
            }

            Active2 = true;
        }

        public override void Disable()
        {
            Active2 = false;
            base.Disable();
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            if (SmoothServerPlugin.IsServerSide)
            {
                if (Store == null) return "store not loaded yet";
                return "mapSize=" + Store.MapSize + " explored=" + Store.Explored() +
                       " pins=" + Store.Pins.Count + " file=" + _storePath +
                       (ImportSummary != null ? "  " + ImportSummary : "");
            }
            return "deltaHz=" + _hz + " pending=" + PendingDelta.Count + " mapSize=" + _clientMapSize;
        }

        // ---- persistence (server) -------------------------------------------------------------

        private static string StoreDir
        {
            get { return Path.Combine(Paths.ConfigPath, "smoothserver"); }
        }

        private static void LoadWorldPostfix()
        {
            if (!Active2) return;
            try
            {
                var world = ZNet.World;
                if (world == null) { Log.LogWarning("[SharedMap] no world at LoadWorld"); return; }

                string worldName = world.m_name;
                _storePath = Path.Combine(StoreDir, worldName + ".map");

                if (File.Exists(_storePath))
                {
                    Store = MapStore.LoadFrom(_storePath);
                    Log.LogInfo("[SharedMap] loaded " + _storePath + ": mapSize=" + Store.MapSize +
                                " explored=" + Store.Explored() + " pins=" + Store.Pins.Count);
                    return;
                }

                if (_importValue)
                {
                    var ssmPath = Path.ChangeExtension(world.GetDBPath(), null) + ".mod.serversidemap.explored";
                    if (File.Exists(ssmPath))
                    {
                        var bytes = File.ReadAllBytes(ssmPath);
                        int pixels, version;
                        Store = MapStore.ImportServerSideMap(bytes, out pixels, out version);
                        ImportSummary = "imported " + pixels + " explored pixels from " +
                                        Path.GetFileName(ssmPath);
                        Log.LogInfo("[SharedMap] " + ImportSummary + " (ServerSideMap v" + version +
                                    ", mapSize=" + Store.MapSize + ", " + bytes.Length + "B on disk, " +
                                    Store.Pins.Count + " pins)");
                        _dirty = true;
                        SaveNow();
                        return;
                    }
                }

                // No file, no import source: stay unsized until the first client tells us its
                // Minimap.m_textureSize. Never guess 2048.
                Store = new MapStore();
                Log.LogInfo("[SharedMap] no store at " + _storePath +
                            " - waiting for the first client to report its map size");
            }
            catch (Exception e)
            {
                Log.LogError("[SharedMap] load failed: " + e);
                Store = new MapStore();
            }
        }

        private static bool _importValue = true;

        private static void SaveWorldPostfix()
        {
            if (!Active2) return;
            SaveNow();
        }

        private static void SaveNow()
        {
            try
            {
                if (Store == null || !Store.Sized || string.IsNullOrEmpty(_storePath)) return;
                if (!_dirty) return;
                Store.SaveTo(_storePath);
                _dirty = false;
                var fi = new FileInfo(_storePath);
                Log.LogInfo("[SharedMap] saved " + _storePath + " (" + fi.Length + "B, explored=" +
                            Store.Explored() + ", pins=" + Store.Pins.Count + ")");
            }
            catch (Exception e) { Log.LogError("[SharedMap] save failed: " + e); }
        }

        // ---- RPC plumbing ---------------------------------------------------------------------

        private static void RoutedRpcCtorPostfix()
        {
            _rpcsRegistered = false;
            _helloSent = false;
            PendingDelta.Clear();
            PendingFull.Clear();
            PendingFullCount.Clear();
            TryRegisterRpcs();
        }

        private static void TryRegisterRpcs()
        {
            if (_rpcsRegistered) return;
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;
            try
            {
                rrpc.Register<ZPackage>(RpcHello, OnHello);
                rrpc.Register<ZPackage>(RpcFull, OnFull);
                rrpc.Register<ZPackage>(RpcDelta, OnDelta);
                rrpc.Register<ZPackage>(RpcPin, OnPin);
                _rpcsRegistered = true;
                Log.LogInfo("[SharedMap] routed RPCs '" + RpcHello + "' / '" + RpcFull + "' / '" +
                            RpcDelta + "' / '" + RpcPin + "' registered");
            }
            catch (Exception e)
            {
                Log.LogWarning("[SharedMap] could not register routed RPCs: " + e.Message);
                _rpcsRegistered = true;
            }
        }

        // ---- server handlers ---------------------------------------------------------------

        private static void OnHello(long sender, ZPackage pkg)
        {
            if (!Active2 || !ServerActive() || Store == null) return;
            int mapSize = pkg.ReadInt();
            if (mapSize <= 0) return;

            if (!Store.Sized)
            {
                Store.Resize(mapSize);
                Log.LogInfo("[SharedMap] adopted mapSize=" + mapSize + " from the first client");
            }
            if (mapSize != Store.MapSize)
            {
                Log.LogWarning("[SharedMap] peer " + sender + " has mapSize=" + mapSize +
                               " but the store is " + Store.MapSize + " - not syncing this peer");
                return;
            }

            if (_shareExpl)
            {
                PendingFull[sender] = 0;
                PendingFullCount[sender] = Store.FullChunkCount;
            }
            if (_sharePinsV) SendAllPins(sender);
        }

        private static void OnDelta(long sender, ZPackage pkg)
        {
            if (!Active2 || !_shareExpl) return;

            if (ServerActive())
            {
                if (Store == null || !Store.Sized) return;
                List<int> added;
                try { added = Store.MergeDelta(pkg); }
                catch (Exception e) { Log.LogWarning("[SharedMap] bad delta from " + sender + ": " + e.Message); return; }
                if (added == null || added.Count == 0) return;
                _dirty = true;

                var wire = Store.EncodeDelta(added);
                var net = ZNet.instance;
                if (net == null) return;
                foreach (var peer in net.GetConnectedPeers())
                {
                    if (peer.m_uid == sender) continue;
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RpcDelta, wire);
                }
                return;
            }

            // client: a delta from the server
            ApplyClientBits(pkg, false);
        }

        private static void OnFull(long sender, ZPackage pkg)
        {
            if (!Active2 || !_shareExpl || ServerActive()) return;
            ApplyClientBits(pkg, true);
        }

        private static void SendAllPins(long target)
        {
            if (Store == null) return;
            foreach (var p in Store.Pins)
                ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcPin, EncodePin(0, p));
        }

        private static void OnPin(long sender, ZPackage pkg)
        {
            if (!Active2 || !_sharePinsV) return;
            int op = pkg.ReadInt();
            var p = new SharedPin();
            p.Name = pkg.ReadString();
            p.Pos = new Vector3(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle());
            p.Type = pkg.ReadInt();
            p.Checked = pkg.ReadBool();
            p.OwnerId = pkg.ReadLong();

            if (!AllowedPinTypes.Contains(p.Type)) return;

            if (ServerActive())
            {
                if (Store == null) return;
                bool changed = false;
                if (op == 0)
                {
                    if (Store.Pins.FindIndex(q => q.Key == p.Key) < 0) { Store.Pins.Add(p); changed = true; }
                }
                else
                {
                    int i = Store.Pins.FindIndex(q => q.Key == p.Key);
                    if (i >= 0) { Store.Pins.RemoveAt(i); changed = true; }
                }
                if (!changed) return;
                _dirty = true;

                var net = ZNet.instance;
                if (net == null) return;
                foreach (var peer in net.GetConnectedPeers())
                {
                    if (peer.m_uid == sender) continue;
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RpcPin, EncodePin(op, p));
                }
                return;
            }

            ApplyClientPin(op, p);
        }

        private static ZPackage EncodePin(int op, SharedPin p)
        {
            var pkg = new ZPackage();
            pkg.Write(op);
            pkg.Write(p.Name ?? "");
            pkg.Write(p.Pos.x); pkg.Write(p.Pos.y); pkg.Write(p.Pos.z);
            pkg.Write(p.Type);
            pkg.Write(p.Checked);
            pkg.Write(p.OwnerId);
            return pkg;
        }

        // ---- client application --------------------------------------------------------------

        private static void ApplyClientBits(ZPackage pkg, bool full)
        {
            var mm = Minimap.instance;
            if (mm == null) return;

            int applied = 0;
            _applying = true;
            try
            {
                if (full)
                {
                    int mapSize, idx, count, off; byte[] slice;
                    if (!MapStore.DecodeFullChunk(pkg, out mapSize, out idx, out count, out off, out slice)) return;
                    if (mapSize != mm.m_textureSize)
                    {
                        Log.LogWarning("[SharedMap] server map is " + mapSize + " but ours is " +
                                       mm.m_textureSize + " - ignoring shared exploration");
                        return;
                    }
                    for (int i = 0; i < slice.Length; i++)
                    {
                        if (slice[i] == 0) continue;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            if ((slice[i] & (1 << bit)) == 0) continue;
                            int index = ((off + i) << 3) | bit;
                            if (ExploreIndex(mm, index)) applied++;
                        }
                    }
                }
                else
                {
                    var tmp = new MapStore(mm.m_textureSize);
                    var added = tmp.MergeDelta(pkg);
                    if (added == null) return;
                    foreach (var index in added) if (ExploreIndex(mm, index)) applied++;
                }
            }
            catch (Exception e) { Log.LogWarning("[SharedMap] applying shared exploration failed: " + e.Message); }
            finally { _applying = false; }

            if (applied > 0 && mm.m_fogTexture != null) mm.m_fogTexture.Apply();
        }

        private static bool ExploreIndex(Minimap mm, int index)
        {
            int size = mm.m_textureSize;
            if (index < 0 || index >= size * size) return false;
            return mm.Explore(index % size, index / size);
        }

        private static void ApplyClientPin(int op, SharedPin p)
        {
            var mm = Minimap.instance;
            if (mm == null) return;
            _applying = true;
            try
            {
                var type = (Minimap.PinType)p.Type;
                if (op == 0)
                {
                    foreach (var existing in mm.m_pins)
                        if (existing.m_type == type && existing.m_name == p.Name &&
                            Utils.DistanceXZ(existing.m_pos, p.Pos) < 1f) return;
                    mm.AddPin(p.Pos, type, p.Name, true, p.Checked, p.OwnerId);
                }
                else
                {
                    Minimap.PinData found = null;
                    foreach (var existing in mm.m_pins)
                        if (existing.m_type == type && existing.m_name == p.Name &&
                            Utils.DistanceXZ(existing.m_pos, p.Pos) < 1f) { found = existing; break; }
                    if (found != null) mm.RemovePin(found);
                }
            }
            catch (Exception e) { Log.LogWarning("[SharedMap] applying shared pin failed: " + e.Message); }
            finally { _applying = false; }
        }

        // ---- client patches -------------------------------------------------------------------

        private static void ExplorePostfix(int x, int y, bool __result)
        {
            if (!__result || !Active2 || !_shareExpl || _applying) return;
            if (_inTableImport && _excludeTableV) return;
            if (!ClientActive()) return;
            var mm = Minimap.instance;
            if (mm == null) return;
            PendingDelta.Add(y * mm.m_textureSize + x);
        }

        private static void AddPinPostfix(Vector3 pos, Minimap.PinType type, string name,
                                          bool save, bool isChecked, long ownerID)
        {
            if (!Active2 || !_sharePinsV || _applying || !save) return;
            if (!ClientActive() || !_rpcsRegistered) return;
            if (!AllowedPinTypes.Contains((int)type)) return;
            var p = new SharedPin { Name = name ?? "", Pos = pos, Type = (int)type, Checked = isChecked, OwnerId = ownerID };
            try { ZRoutedRpc.instance.InvokeRoutedRPC(RpcPin, EncodePin(0, p)); } catch { }
        }

        private static void RemovePinPrefix(Minimap.PinData pin)
        {
            if (!Active2 || !_sharePinsV || _applying || pin == null || !pin.m_save) return;
            if (!ClientActive() || !_rpcsRegistered) return;
            if (!AllowedPinTypes.Contains((int)pin.m_type)) return;
            var p = new SharedPin { Name = pin.m_name ?? "", Pos = pin.m_pos, Type = (int)pin.m_type,
                                    Checked = pin.m_checked, OwnerId = pin.m_ownerID };
            try { ZRoutedRpc.instance.InvokeRoutedRPC(RpcPin, EncodePin(1, p)); } catch { }
        }

        private static void TablePrefix() { _inTableImport = true; }
        private static void TablePostfix() { _inTableImport = false; }

        // ---- tick --------------------------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active2) return;
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return;
            TryRegisterRpcs();
            if (!_rpcsRegistered) return;

            if (ServerActive())
            {
                // Drain one full-sync chunk per peer per tick: 32 KB of raw bits each, so even a
                // fully explored 2048^2 map is 16 messages, comfortably under Steam's 512 KB cap.
                if (PendingFull.Count == 0) return;
                var done = new List<long>();
                var keys = new List<long>(PendingFull.Keys);
                foreach (var uid in keys)
                {
                    int idx = PendingFull[uid];
                    int count = PendingFullCount[uid];
                    if (Store == null || !Store.Sized || idx >= count) { done.Add(uid); continue; }
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(uid, RpcFull, Store.EncodeFullChunk(idx)); }
                    catch (Exception e) { Log.LogWarning("[SharedMap] full sync to " + uid + " failed: " + e.Message); done.Add(uid); continue; }
                    PendingFull[uid] = idx + 1;
                    if (idx + 1 >= count)
                    {
                        done.Add(uid);
                        Log.LogInfo("[SharedMap] full map sent to peer " + uid + " (" + count + " chunks, " +
                                    Store.Explored() + " explored pixels)");
                    }
                }
                foreach (var uid in done) { PendingFull.Remove(uid); PendingFullCount.Remove(uid); }
                return;
            }

            if (!ClientActive()) return;
            var mm = Minimap.instance;
            if (mm == null) return;
            _clientMapSize = mm.m_textureSize;

            if (!_helloSent)
            {
                _helloTimer += dt;
                if (_helloTimer >= 2f)
                {
                    _helloTimer = 0f;
                    var pkg = new ZPackage();
                    pkg.Write(_clientMapSize);
                    try { ZRoutedRpc.instance.InvokeRoutedRPC(RpcHello, pkg); _helloSent = true; }
                    catch (Exception e) { Log.LogWarning("[SharedMap] hello failed: " + e.Message); }
                    if (_helloSent) Log.LogInfo("[SharedMap] requested the shared map (mapSize=" + _clientMapSize + ")");
                }
            }

            if (!_shareExpl || PendingDelta.Count == 0) return;
            _deltaTimer += dt;
            if (_deltaTimer < 1f / _hz) return;
            _deltaTimer = 0f;

            try
            {
                var tmp = new MapStore(_clientMapSize);
                var wire = tmp.EncodeDelta(PendingDelta);
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcDelta, wire);
            }
            catch (Exception e) { Log.LogWarning("[SharedMap] delta send failed: " + e.Message); }
            PendingDelta.Clear();
        }
    }
}
