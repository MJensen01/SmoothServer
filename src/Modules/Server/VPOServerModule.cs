using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// M12 - the server-relevant subset of ValheimPerformanceOptimizations, ported.
    ///
    /// Source: https://github.com/ontrigger/ValheimPerformanceOptimizations - MIT, Copyright (c)
    /// 2021 ontrigger (LICENSE verified in /opt/modlab/src/_vendor/VPO/LICENSE). Adapted here with
    /// credit; see THIRD_PARTY.md. Only three of VPO's ~12 patches do anything on a dedicated
    /// server, and they are ported by hand against the 0.221.12 decompiles rather than vendored,
    /// because VPO's own build drags in Unity.Burst/Unity.Jobs and Unity.Profiling for the
    /// client-side terrain work - a packaging cost with no server return.
    ///
    ///   1. WearNTear support/structural-integrity caching (VPO WearNTearPatches).
    ///      Vanilla UpdateSupport calls collider.GetComponentInParent&lt;WearNTear&gt;() for every
    ///      collider of every bound of every piece, every wear tick, and recomputes each
    ///      neighbour's centre of mass each time. We memoise collider -> owner in a
    ///      ConditionalWeakTable, memoise the piece's own collider set as a HashSet (vanilla does
    ///      a linear Array.Contains), and memoise centre-of-mass for the duration of one call.
    ///      This is "the known worst offender in built-up bases" per SMOOTHSERVER-PHASE2 M12.
    ///      NOTE one deliberate behaviour delta inherited from VPO: a collider that appears in two
    ///      overlapping bounds is processed once, not twice.
    ///
    ///   2. ZDOMan.ReleaseNearbyZDOS scan speedup (VPO ZDOManReleaseNearbyPatch).
    ///      Vanilla calls zdo.GetOwner() (a ZDOExtraData dictionary lookup) up to three times per
    ///      ZDO per scan; the port resolves it once and uses zdo.IsOwner() for the server's own
    ///      pass. Same decisions, fewer lookups. Interacts with OwnershipRelease, which makes this
    ///      scan run 4x more often - port this first, then shorten the timer.
    ///
    ///   3. Physics step cap (VPO MaxPhysicsTimeStepPatch): Time.maximumDeltaTime = n *
    ///      Time.fixedDeltaTime. Unity will run up to 15 physics steps in one frame by default;
    ///      on a headless server that is the single spikiest thing in a long frame. VPO patches
    ///      FejdStartup.Awake, which never runs headless, so we apply it after ZNet.Start instead.
    ///      Default 0 = leave vanilla, because it trades physics accuracy.
    ///
    /// Not ported: VPO's ZNetScene streaming rewrite (needs its BoundsOctree/ObjectCollection and
    /// overlaps CreateBudget), and everything terrain/water/audio/rendering (client-only).
    /// </summary>
    internal sealed class VPOServerModule : FeatureModule
    {
        public override string Name => "VPOServer";

        private ConfigEntry<bool> _wearNTear;
        private ConfigEntry<bool> _releaseScan;
        private ConfigEntry<int> _maxPhysicsSteps;

        internal static bool Active;
        internal static bool WearNTearCache = true;
        internal static bool ReleaseScanSpeedup = true;
        internal static int MaxPhysicsSteps;          // 0 = leave vanilla

        private static bool _physicsPending;
        private static float _physicsVanilla = -1f;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("VPOServer", "Enabled", true,
                "Port of the server-relevant ValheimPerformanceOptimizations patches (MIT, ontrigger).");
            _wearNTear = cfg.Bind("VPOServer", "WearNTearCache", true,
                "Cache collider->WearNTear ownership and centre-of-mass during structural-integrity " +
                "recalculation. The biggest server CPU win in built-up bases.");
            _releaseScan = cfg.Bind("VPOServer", "ReleaseScanSpeedup", true,
                "Resolve each ZDO's owner once per ownership-release scan instead of up to three times.");
            _maxPhysicsSteps = cfg.Bind("VPOServer", "MaxPhysicsStepsPerFrame", 0,
                "0 = leave Unity's default (up to 15 physics steps in one frame). 5-15 caps it, " +
                "trading physics accuracy for a shorter worst frame. VPO's own default is 8.");
            Watch(_wearNTear); Watch(_releaseScan); Watch(_maxPhysicsSteps);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var updateSupport = AccessTools.Method(typeof(WearNTear), "UpdateSupport");
            var clearCached = AccessTools.Method(typeof(WearNTear), "ClearCachedSupport");
            var wntDestroy = AccessTools.Method(typeof(WearNTear), "OnDestroy");
            var releaseNearby = AccessTools.Method(typeof(ZDOMan), "ReleaseNearbyZDOS");

            if (updateSupport == null || clearCached == null || wntDestroy == null)
                throw new Exception("SmoothServer VPOServer: WearNTear.UpdateSupport/ClearCachedSupport/OnDestroy " +
                                    "not all found - refusing to patch");
            if (releaseNearby == null)
                throw new Exception("SmoothServer VPOServer: ZDOMan.ReleaseNearbyZDOS not found");

            // Fields the WearNTear port reaches into. If any is gone the game changed shape and
            // the port would silently compute the wrong support value - fail loudly instead.
            string[] wntFields = { "m_supportColliders", "m_supportPositions", "m_supportValue",
                                   "m_clearCachedSupport", "m_colliders", "m_bounds", "m_support",
                                   "m_supports", "m_comOffset", "m_forceCorrectCOMCalculation" };
            foreach (var f in wntFields)
                if (AccessTools.Field(typeof(WearNTear), f) == null)
                    throw new Exception("SmoothServer VPOServer: WearNTear." + f + " not found - refusing to patch");

            string[] wntStatics = { "s_tempColliders", "s_tempSupportPoints", "s_tempSupportPointValues",
                                    "s_rayMask", "s_terrainLayer" };
            foreach (var f in wntStatics)
                if (AccessTools.Field(typeof(WearNTear), f) == null)
                    throw new Exception("SmoothServer VPOServer: WearNTear." + f + " not found - refusing to patch");

            if (AccessTools.Method(typeof(WearNTear), "GetMaterialProperties") == null ||
                AccessTools.Method(typeof(WearNTear), "FindSupportPoint") == null ||
                AccessTools.Method(typeof(WearNTear), "GetMaxSupport") == null ||
                AccessTools.Method(typeof(WearNTear), "HaveSupport") == null ||
                AccessTools.Method(typeof(WearNTear), "SetupColliders") == null)
                throw new Exception("SmoothServer VPOServer: a WearNTear helper method is missing - refusing to patch");

            if (AccessTools.Method(typeof(ZDOMan), "IsInPeerActiveArea") == null)
                throw new Exception("SmoothServer VPOServer: ZDOMan.IsInPeerActiveArea not found");

            if (WearNTearCache)
            {
                Harmony.Patch(updateSupport,
                    prefix: new HarmonyMethod(typeof(VPOServerModule), nameof(UpdateSupportPrefix)),
                    finalizer: new HarmonyMethod(typeof(VPOServerModule), nameof(UpdateSupportFinalizer)));
                Harmony.Patch(clearCached,
                    postfix: new HarmonyMethod(typeof(VPOServerModule), nameof(ClearCachedSupportPostfix)));
                Harmony.Patch(wntDestroy,
                    postfix: new HarmonyMethod(typeof(VPOServerModule), nameof(WearNTearOnDestroyPostfix)));
            }

            if (ReleaseScanSpeedup)
            {
                Harmony.Patch(releaseNearby,
                    prefix: new HarmonyMethod(typeof(VPOServerModule), nameof(ReleaseNearbyPrefix)) { priority = Priority.High });
            }

            _physicsPending = MaxPhysicsSteps > 0;
            Active = true;
            Log.LogInfo("[VPOServer] wearNTearCache=" + WearNTearCache +
                        " releaseScanSpeedup=" + ReleaseScanSpeedup +
                        " maxPhysicsSteps=" + (MaxPhysicsSteps == 0 ? "vanilla" : MaxPhysicsSteps.ToString()) +
                        " (ported from ValheimPerformanceOptimizations, MIT, ontrigger)");
        }

        public override void Disable()
        {
            Active = false;
            Owners = new ConditionalWeakTable<Collider, CachedOwner>();
            Caches = new ConditionalWeakTable<WearNTear, CachedWearNTear>();
            CachedCentersOfMass.Clear();
            ProcessedSupportColliders.Clear();
            if (_physicsVanilla > 0f) Time.maximumDeltaTime = _physicsVanilla;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            int before = MaxPhysicsSteps;
            ReadConfig();
            if (MaxPhysicsSteps != before)
            {
                _physicsPending = true;
                Log.LogInfo("[VPOServer] MaxPhysicsStepsPerFrame -> " + MaxPhysicsSteps);
            }
            if (entry == _wearNTear || entry == _releaseScan)
                Log.LogInfo("[VPOServer] WearNTearCache/ReleaseScanSpeedup changes need a server restart " +
                            "(patches are installed at load); current: wearNTear=" + WearNTearCache +
                            " releaseScan=" + ReleaseScanSpeedup);
        }

        private void ReadConfig()
        {
            WearNTearCache = _wearNTear.Value;
            ReleaseScanSpeedup = _releaseScan.Value;
            int n = _maxPhysicsSteps.Value;
            MaxPhysicsSteps = n <= 0 ? 0 : Mathf.Clamp(n, 5, 15);
        }

        internal static void Tick(float dt)
        {
            if (!Active || !_physicsPending) return;
            if (!ServerActive()) return;
            _physicsPending = false;

            if (_physicsVanilla < 0f) _physicsVanilla = Time.maximumDeltaTime;
            if (MaxPhysicsSteps <= 0)
            {
                Time.maximumDeltaTime = _physicsVanilla;
                SmoothServerPlugin.Log.LogInfo("[VPOServer] maximumDeltaTime restored to vanilla " +
                    _physicsVanilla.ToString("F4") + "s");
                return;
            }

            float before = Time.maximumDeltaTime;
            Time.maximumDeltaTime = MaxPhysicsSteps * Time.fixedDeltaTime;
            SmoothServerPlugin.Log.LogInfo("[VPOServer] maximumDeltaTime " + before.ToString("F4") +
                "s -> " + Time.maximumDeltaTime.ToString("F4") + "s (" + MaxPhysicsSteps +
                " physics steps x fixedDeltaTime " + Time.fixedDeltaTime.ToString("F4") + "s)");
        }

        // =================================================================================
        // 1. WearNTear support caching - ported from VPO WearNTearPatches (MIT, ontrigger)
        // =================================================================================

        private sealed class CachedOwner { public WearNTear Owner; }

        private sealed class CachedWearNTear
        {
            public readonly HashSet<Collider> OwnColliders = new HashSet<Collider>();
            public readonly List<WearNTear> SupportOwners = new List<WearNTear>();
            public bool HasCenterOfMass;
            public Vector3 CenterOfMass;
        }

        private static ConditionalWeakTable<Collider, CachedOwner> Owners =
            new ConditionalWeakTable<Collider, CachedOwner>();
        private static ConditionalWeakTable<WearNTear, CachedWearNTear> Caches =
            new ConditionalWeakTable<WearNTear, CachedWearNTear>();
        private static readonly HashSet<Collider> ProcessedSupportColliders = new HashSet<Collider>();
        private static readonly List<CachedWearNTear> CachedCentersOfMass = new List<CachedWearNTear>();

        private static WearNTear GetOrCacheOwner(Collider collider)
        {
            if (collider == null) return null;
            CachedOwner cached;
            if (Owners.TryGetValue(collider, out cached)) return cached.Owner;
            var owner = collider.GetComponentInParent<WearNTear>();
            Owners.Add(collider, new CachedOwner { Owner = owner });
            return owner;
        }

        private static CachedWearNTear GetCache(WearNTear instance)
        {
            CachedWearNTear cached;
            if (Caches.TryGetValue(instance, out cached)) return cached;
            cached = new CachedWearNTear();
            Caches.Add(instance, cached);
            return cached;
        }

        private static HashSet<Collider> GetOwnColliders(WearNTear owner)
        {
            var cached = GetCache(owner);
            if (cached.OwnColliders.Count != 0) return cached.OwnColliders;
            var cols = owner.m_colliders;
            if (cols != null)
                for (int i = 0; i < cols.Length; i++) cached.OwnColliders.Add(cols[i]);
            return cached.OwnColliders;
        }

        private static float GetOptimizedSupport(WearNTear instance)
        {
            var nview = instance.m_nview;
            if (nview == null || !nview.IsValid() || !nview.HasOwner()) return instance.GetMaxSupport();
            if (nview.IsOwner()) return instance.m_support;
            return nview.GetZDO().GetFloat(ZDOVars.s_support, instance.GetMaxSupport());
        }

        private static Vector3 GetOptimizedCOM(WearNTear instance)
        {
            var t = instance.transform;
            return t.position + t.rotation * instance.m_comOffset;
        }

        private static Vector3 GetCachedCOM(WearNTear instance)
        {
            var cached = GetCache(instance);
            if (!cached.HasCenterOfMass)
            {
                cached.HasCenterOfMass = true;
                cached.CenterOfMass = GetOptimizedCOM(instance);
                CachedCentersOfMass.Add(cached);
            }
            return cached.CenterOfMass;
        }

        private static bool UpdateSupportPrefix(WearNTear __instance)
        {
            if (!Active || !WearNTearCache) return true;

            int count = __instance.m_supportColliders.Count;
            if (count > 0)
            {
                var cached = GetCache(__instance);
                var supportOwners = cached.SupportOwners;
                if (supportOwners.Count == count)
                {
                    int matched = 0;
                    float best = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        var collider = __instance.m_supportColliders[i];
                        if (collider == null) break;
                        var owner = supportOwners[i];
                        if (owner == null || !owner.m_supports) break;
                        if (collider.transform.position == __instance.m_supportPositions[i])
                        {
                            float support = GetOptimizedSupport(owner);
                            if (support > best) best = support;
                            if (support.Equals(__instance.m_supportValue[i])) matched++;
                        }
                    }
                    if (matched == __instance.m_supportPositions.Count && best > __instance.m_support)
                        return false;
                }
                __instance.ClearCachedSupport();
            }

            if (__instance.m_colliders == null) __instance.SetupColliders();

            HashSet<Collider> ownColliders = GetOwnColliders(__instance);
            List<WearNTear> supportOwnersList = GetCache(__instance).SupportOwners;

            float maxSupport, minSupport, horizontalLoss, verticalLoss;
            __instance.GetMaterialProperties(out maxSupport, out minSupport, out horizontalLoss, out verticalLoss);

            WearNTear.s_tempSupportPoints.Clear();
            WearNTear.s_tempSupportPointValues.Clear();
            ProcessedSupportColliders.Clear();

            Vector3 cOM = GetOptimizedCOM(__instance);
            bool onTerrain = false;
            float bestSupport = 0f;

            foreach (var bound in __instance.m_bounds)
            {
                int hits = Physics.OverlapBoxNonAlloc(bound.m_pos, bound.m_size,
                    WearNTear.s_tempColliders, bound.m_rot, WearNTear.s_rayMask);

                if (__instance.m_clearCachedSupport)
                {
                    for (int i = 0; i < hits; i++)
                    {
                        var collider = WearNTear.s_tempColliders[i];
                        if (collider.attachedRigidbody != null || collider.isTrigger ||
                            ownColliders.Contains(collider)) continue;

                        var owner = GetOrCacheOwner(collider);
                        if (owner == null) continue;

                        if (owner.m_nview.IsOwner()) owner.ClearCachedSupport();
                        else if (owner.m_nview.IsValid())
                            owner.m_nview.InvokeRPC(owner.m_nview.GetZDO().GetOwner(), "RPC_ClearCachedSupport");
                    }
                    __instance.m_clearCachedSupport = false;
                }

                for (int i = 0; i < hits; i++)
                {
                    var collider = WearNTear.s_tempColliders[i];
                    if (collider.attachedRigidbody != null || collider.isTrigger ||
                        ownColliders.Contains(collider)) continue;
                    if (!ProcessedSupportColliders.Add(collider)) continue;

                    if (collider.gameObject.layer == WearNTear.s_terrainLayer)
                    {
                        onTerrain = true;
                        continue;
                    }

                    var owner = GetOrCacheOwner(collider);
                    if (owner == null)
                    {
                        __instance.m_support = maxSupport;
                        __instance.ClearCachedSupport();
                        __instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
                        return false;
                    }
                    if (!owner.m_supports) continue;

                    float dCom = Vector3.Distance(cOM, GetCachedCOM(owner)) + 0.1f;
                    float dPos = Vector3.Distance(cOM, owner.transform.position) + 0.1f;
                    if (dPos < dCom && !__instance.m_forceCorrectCOMCalculation) dCom = dPos;

                    float support = GetOptimizedSupport(owner);
                    bestSupport = Mathf.Max(bestSupport, support - horizontalLoss * dCom * support);

                    Vector3 point = WearNTear.FindSupportPoint(cOM, owner, collider);
                    if (point.y < cOM.y + 0.05f)
                    {
                        Vector3 normalized = (point - cOM).normalized;
                        if (normalized.y < 0f)
                        {
                            float t = Mathf.Acos(1f - Mathf.Abs(normalized.y)) / (Mathf.PI / 2f);
                            float loss = Mathf.Lerp(horizontalLoss, verticalLoss, t);
                            bestSupport = Mathf.Max(bestSupport, support - loss * dCom * support);
                        }
                        WearNTear.s_tempSupportPoints.Add(point);
                        WearNTear.s_tempSupportPointValues.Add(support - verticalLoss * dCom * support);
                        __instance.m_supportColliders.Add(collider);
                        __instance.m_supportPositions.Add(collider.transform.position);
                        __instance.m_supportValue.Add(support);
                        supportOwnersList.Add(owner);
                    }
                }
            }

            if (onTerrain)
            {
                __instance.m_support = maxSupport;
                __instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
                return false;
            }

            if (WearNTear.s_tempSupportPoints.Count > 0)
            {
                int n = WearNTear.s_tempSupportPoints.Count;
                for (int i = 0; i < n - 1; i++)
                {
                    Vector3 from = WearNTear.s_tempSupportPoints[i] - cOM;
                    from.y = 0f;
                    for (int j = i + 1; j < n; j++)
                    {
                        float avg = (WearNTear.s_tempSupportPointValues[i] + WearNTear.s_tempSupportPointValues[j]) * 0.5f;
                        if (avg <= bestSupport) continue;
                        Vector3 to = WearNTear.s_tempSupportPoints[j] - cOM;
                        to.y = 0f;
                        if (Vector3.Angle(from, to) >= 100f) bestSupport = avg;
                    }
                }
            }

            __instance.m_support = Mathf.Min(bestSupport, maxSupport);
            __instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
            if (!__instance.HaveSupport()) __instance.ClearCachedSupport();
            return false;
        }

        private static Exception UpdateSupportFinalizer(Exception __exception)
        {
            ProcessedSupportColliders.Clear();
            for (int i = 0; i < CachedCentersOfMass.Count; i++)
                CachedCentersOfMass[i].HasCenterOfMass = false;
            CachedCentersOfMass.Clear();
            return __exception;
        }

        private static void ClearCachedSupportPostfix(WearNTear __instance)
        {
            CachedWearNTear cached;
            if (Caches.TryGetValue(__instance, out cached)) cached.SupportOwners.Clear();
        }

        private static void WearNTearOnDestroyPostfix(WearNTear __instance)
        {
            CachedWearNTear cached;
            if (!Caches.TryGetValue(__instance, out cached)) return;
            Caches.Remove(__instance);
            CachedCentersOfMass.Remove(cached);
        }

        // =================================================================================
        // 2. ReleaseNearbyZDOS scan - ported from VPO ZDOManReleaseNearbyPatch (MIT, ontrigger)
        // =================================================================================

        private static bool ReleaseNearbyPrefix(ZDOMan __instance, Vector3 refPosition, long uid)
        {
            if (!Active || !ReleaseScanSpeedup || !ServerActive()) return true;

            Vector2i zone = ZoneSystem.GetZone(refPosition);
            List<ZDO> nearby = __instance.m_tempNearObjects;
            nearby.Clear();
            __instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, 0, nearby);

            int activatedArea = ZoneSystem.instance.m_activeArea - 1;
            bool isServerPass = uid == ZDOMan.GetSessionID();

            for (int i = 0; i < nearby.Count; i++)
            {
                var zdo = nearby[i];
                if (zdo == null || !zdo.Persistent) continue;

                Vector2i sector = zdo.GetSector();
                bool hasOwner = zdo.HasOwner();

                long owner;
                bool ownedByPassPeer;
                if (isServerPass)
                {
                    ownedByPassPeer = zdo.IsOwner();
                    owner = (ownedByPassPeer || !hasOwner) ? 0L : zdo.GetOwner();
                }
                else
                {
                    owner = hasOwner ? zdo.GetOwner() : 0L;
                    ownedByPassPeer = owner == uid;
                }

                if (ownedByPassPeer)
                {
                    if (!ZNetScene.InActiveArea(sector, zone, activatedArea)) zdo.SetOwner(0L);
                    continue;
                }

                if ((!hasOwner || !__instance.IsInPeerActiveArea(sector, owner)) &&
                    ZNetScene.InActiveArea(sector, zone, activatedArea))
                    zdo.SetOwner(uid);
            }

            return false;
        }
    }
}
