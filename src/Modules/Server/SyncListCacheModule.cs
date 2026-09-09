using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M5 - sync-list cache. This module exists to pay back debt SmoothServer 0.2.0 created.
    ///
    /// Vanilla ZDOMan.CreateSyncList (server branch) does, per peer, per send:
    ///     FindSectorObjects(zone, peer.m_peer.m_simulationDistance, sectorObjects, distantObjects)
    ///     -> filter by peer.ShouldSend -> ServerSortSendZDOS (a full List.Sort)
    ///     -> if toSync.Count &lt; 10, also filter the distant list
    ///     -> AddForceSendZdos
    ///
    /// FindSectorObjects walks (2*activeArea+1)^2 sector buckets plus the distant ring. Vanilla
    /// sent to ONE peer per frame after a 50ms gate, so that ran ~4x/s per peer. SendCadence's
    /// flat 20Hz all-peers sweep runs it 20x/s per peer - 120 scans/s at 6 peers.
    ///
    /// The sector *scan* is the part that is stable frame to frame: the buckets only change when
    /// the peer moves to a new zone or a ZDO changes sector. The ShouldSend filter and the sort
    /// are NOT stable - they depend on peer.m_zdos, which SendZDOs mutates on every send - so
    /// caching the finished sorted list would re-send ZDOs the peer already has.
    ///
    /// So: cache the raw sector/distant object lists per peer for CacheMs (and invalidate on a
    /// zone change), and re-run the filter + sort every call exactly as vanilla does. Same bytes
    /// on the wire, one scan per CacheMs instead of one per send.
    /// </summary>
    internal sealed class SyncListCacheModule : FeatureModule
    {
        public override string Name => "SyncListCache";

        private ConfigEntry<float> _cacheMs;
        private ConfigEntry<float> _statsIntervalSec;

        internal static bool Active;
        internal static float CacheSec = 0.1f;
        internal static float StatsIntervalSec = 60f;

        private sealed class Entry
        {
            public Vector2s Zone;
            // 1.0: the scan radius is now per-peer (ZNetPeer.m_simulationDistance), not the global
            // ZoneSystem.m_activeArea, so it has to be part of the cache key.
            public SimulationDistance Dist;
            public float StampedAt;
            public readonly List<ZDO> Sector = new List<ZDO>();
            public readonly List<ZDO> Distant = new List<ZDO>();
        }

        private static readonly Dictionary<long, Entry> Cache = new Dictionary<long, Entry>();
        private static long _hits, _misses;
        private static float _statsAcc;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SyncListCache", "Enabled", true,
                "Cache ZDOMan's per-peer sector scan across the 20Hz send sweep. The ShouldSend " +
                "filter and the priority sort still run every send, so nothing is re-sent.");
            _cacheMs = cfg.Bind("SyncListCache", "CacheMs", 100f,
                "Milliseconds a peer's sector scan stays valid. Also invalidated immediately when " +
                "the peer changes zone. 0 disables caching (still uses our own temp lists).");
            _statsIntervalSec = cfg.Bind("SyncListCache", "StatsIntervalSec", 60f,
                "Seconds between cache hit/miss lines. 0 disables them.");
            Watch(_cacheMs); Watch(_statsIntervalSec);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(ZDOMan), "CreateSyncList");
            if (target == null)
                throw new Exception("SmoothServer SyncListCache: ZDOMan.CreateSyncList not found");

            var pars = target.GetParameters();
            if (pars.Length != 2 || pars[0].Name != "peer" || pars[1].ParameterType != typeof(List<ZDO>))
                throw new Exception("SmoothServer SyncListCache: ZDOMan.CreateSyncList signature changed " +
                                    "(expected (ZDOPeer peer, List<ZDO> toSync)) - refusing to patch");

            if (AccessTools.Method(typeof(ZDOMan), "ServerSortSendZDOS") == null)
                throw new Exception("SmoothServer SyncListCache: ZDOMan.ServerSortSendZDOS not found");
            if (AccessTools.Method(typeof(ZDOMan), "AddForceSendZdos") == null)
                throw new Exception("SmoothServer SyncListCache: ZDOMan.AddForceSendZdos not found");
            var find = AccessTools.Method(typeof(ZDOMan), "FindSectorObjects");
            if (find == null)
                throw new Exception("SmoothServer SyncListCache: ZDOMan.FindSectorObjects not found");
            // 1.0 shape: FindSectorObjects(Vector2s, SimulationDistance, List<ZDO>, List<ZDO> = null)
            var fp = find.GetParameters();
            if (fp.Length != 4 || fp[0].ParameterType != typeof(Vector2s) ||
                fp[1].ParameterType != typeof(SimulationDistance) ||
                fp[2].ParameterType != typeof(List<ZDO>) || fp[3].ParameterType != typeof(List<ZDO>))
                throw new Exception("SmoothServer SyncListCache: ZDOMan.FindSectorObjects signature changed " +
                                    "(expected (Vector2s, SimulationDistance, List<ZDO>, List<ZDO>)) - refusing to patch");
            if (AccessTools.Field(typeof(ZNetPeer), "m_simulationDistance") == null)
                throw new Exception("SmoothServer SyncListCache: ZNetPeer.m_simulationDistance not found - refusing to patch");

            Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SyncListCacheModule), nameof(Prefix)) { priority = Priority.High });

            Cache.Clear();
            _hits = 0; _misses = 0; _statsAcc = 0f;
            Active = true;
            Log.LogInfo("[SyncListCache] caching the per-peer sector scan for " +
                        (CacheSec * 1000f).ToString("F0") + "ms (filter + sort still run every send)");
        }

        public override void Disable()
        {
            Active = false;
            Cache.Clear();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Cache.Clear();
            Log.LogInfo("[SyncListCache] CacheMs -> " + (CacheSec * 1000f).ToString("F0"));
        }

        private void ReadConfig()
        {
            CacheSec = Mathf.Clamp(_cacheMs.Value, 0f, 2000f) / 1000f;
            StatsIntervalSec = Mathf.Max(0f, _statsIntervalSec.Value);
        }

        /// <summary>
        /// Replaces the SERVER branch of CreateSyncList. The client branch is left to vanilla.
        /// </summary>
        private static bool Prefix(ZDOMan __instance, ZDOMan.ZDOPeer peer, List<ZDO> toSync)
        {
            if (!Active || !ServerActive()) return true;
            if (peer == null || peer.m_peer == null) return true;

            Vector3 refPos = peer.m_peer.GetRefPos();
            Vector2s zone = ZoneSystem.GetZone(refPos);
            SimulationDistance dist = peer.m_peer.m_simulationDistance;
            long uid = peer.m_peer.m_uid;
            float now = Time.realtimeSinceStartup;

            Entry e;
            if (!Cache.TryGetValue(uid, out e))
            {
                e = new Entry();
                Cache[uid] = e;
                if (Cache.Count > 64) Prune(__instance);
            }

            bool fresh = CacheSec > 0f && e.StampedAt > 0f &&
                         now - e.StampedAt < CacheSec &&
                         e.Zone.x == zone.x && e.Zone.y == zone.y &&
                         e.Dist.Equals(dist);

            if (fresh)
            {
                _hits++;
            }
            else
            {
                _misses++;
                e.Sector.Clear();
                e.Distant.Clear();
                __instance.FindSectorObjects(zone, dist, e.Sector, e.Distant);
                e.Zone = zone;
                e.Dist = dist;
                e.StampedAt = now;
            }

            for (int i = 0; i < e.Sector.Count; i++)
            {
                var zdo = e.Sector[i];
                if (zdo == null) continue;
                if (peer.ShouldSend(zdo)) toSync.Add(zdo);
            }

            __instance.ServerSortSendZDOS(toSync, refPos, peer);

            if (toSync.Count < 10)
            {
                for (int i = 0; i < e.Distant.Count; i++)
                {
                    var zdo = e.Distant[i];
                    if (zdo == null) continue;
                    if (peer.ShouldSend(zdo)) toSync.Add(zdo);
                }
            }

            __instance.AddForceSendZdos(peer, toSync);
            return false;
        }

        private static void Prune(ZDOMan zm)
        {
            var live = new HashSet<long>();
            for (int i = 0; i < zm.m_peers.Count; i++)
                if (zm.m_peers[i] != null && zm.m_peers[i].m_peer != null)
                    live.Add(zm.m_peers[i].m_peer.m_uid);
            var dead = new List<long>();
            foreach (var kv in Cache) if (!live.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var d in dead) Cache.Remove(d);
        }

        internal static void TickStats(float dt)
        {
            if (!Active || StatsIntervalSec <= 0f) return;
            _statsAcc += dt;
            if (_statsAcc < StatsIntervalSec) return;
            _statsAcc = 0f;
            long total = _hits + _misses;
            if (total == 0) return;
            SmoothServerPlugin.Log.LogInfo(string.Format(
                "[SyncListCache] sector scans avoided: {0}/{1} calls ({2:F0}%)",
                _hits, total, 100.0 * _hits / total));
            _hits = 0; _misses = 0;
        }
    }
}
