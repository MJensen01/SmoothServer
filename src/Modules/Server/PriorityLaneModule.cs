using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// Layer A of the hit-registration latency work (docs/HIT-LATENCY-PLAN.md, docs/research/
    /// hit-latency/PRIORITY.md): a **shaper** in front of Steam's single reliable-ordered lane.
    ///
    /// <b>The problem.</b> Per peer there is ONE reliable lane shared by routed RPCs and bulk
    /// ZDOData. `ZSteamSocket.Send` enqueues and immediately calls `SendQueuedPackages`, which
    /// drains to Steam until Steam says no - so `m_sendQueue` is normally EMPTY and the whole
    /// 16-64 KB standing backlog lives inside Steam (`m_cbPendingReliable +
    /// m_cbSentUnackedReliable`), where nothing can be reordered after the fact. A melee hit
    /// therefore waits behind everything already handed to Steam for that peer: 64 KB at Steam's
    /// 153.6 KB/s floor is 427 ms one way.
    ///
    /// <b>The fix.</b> Keep the backlog in managed space instead. A prefix on
    /// `ZSteamSocket.SendQueuedPackages` lifts every newly queued package out of `m_sendQueue`
    /// into two managed queues (prio / bulk) and puts back only about one bandwidth-delay product
    /// (`SteamInflightBytes`, default 6144) at a time, prio first. Vanilla still does the actual
    /// send - we never name a Steam interface (0.3.1's lesson: the dedicated-server
    /// assembly_valheim.dll calls `SteamGameServerNetworkingSockets` throughout, the client build
    /// calls `SteamNetworkingSockets`, and a copied drain loop threw on every send). The residual
    /// head-of-line cost becomes `SteamInflightBytes / bandwidth` - about 20-25 ms at the
    /// ~150-250 KB/s a real peer gets - instead of 80-430 ms.
    ///
    /// <b>Coexistence with SendQueueGuard (decided; logged at apply).</b> PriorityLane does NOT
    /// replace SendQueueGuard's drain, because neither module drains: vanilla always does. The two
    /// stack by Harmony priority on the one seam they share:
    ///   PriorityLane prefix (High) -> Compression prefix (Normal) -> SendQueueGuard prefix (Low)
    ///   -> vanilla drain -> SendQueueGuard finalizer -> PriorityLane postfix.
    /// PriorityLane decides *what* is in the queue, Compression frames exactly that, SendQueueGuard
    /// keeps its back-off/never-throw role over whatever is left. SendQueueGuard skipping a drain
    /// (its back-off window) is harmless here: our packages simply stay queued and our postfix
    /// records them as leftovers.
    ///
    /// <b>Never split a Compression frame.</b> Compression frames one `byte[]` into one `byte[]`,
    /// so a frame is exactly a package. We only ever move whole packages and never modify their
    /// bytes. Two further rules keep the handshake's byte-position invariant intact:
    ///   * We run BEFORE Compression, so everything we hold back is still RAW - a held package can
    ///     never be framed twice, and it is framed only when it is actually installed for sending.
    ///   * `SS_Caps` / `SS_Ready` / `SS_Off` are **barriers**: they are never promoted, and no prio
    ///     package queued after a barrier may overtake it. Without that, a prio package queued
    ///     after `SS_Ready` (and therefore framed) could reach the peer before the Ready that tells
    ///     it to start unframing. This is an addition to the plan, forced by the Compression
    ///     invariant. `CompressionModule.ProtectQueuedPlain` also walks our held packages, so a
    ///     package queued before the flip still goes out plain even if we held it across the flip.
    ///
    /// <b>Ordering guards (mandatory, all enforced in <see cref="LaneQueue"/> / <see cref="LaneWire"/>).</b>
    ///   * Only two things are ever promoted: a routed RPC that passes every gate, and a ZDOData
    ///     package carrying a hot ZDO. Everything else is strict FIFO in the bulk queue, and both
    ///     queues are strict FIFO internally.
    ///   * A deny-listed inner method is never promoted. `DestroyZDO` is a *routed* RPC
    ///     (`ZDOMan.SendDestroyed`), so promoting it could let a destroy overtake the ZDOData that
    ///     creates the object, and `RPC_ZDOData` would happily `CreateNewZDO` a ghost (the
    ///     `m_deadZDOs` guard is `IsServer()`-only - clients have none).
    ///   * `RequireKnownZdo`: a routed RPC with a target ZDO is promoted only when we can prove the
    ///     receiving peer already has that ZDO (it owns it, or it is in that peer's
    ///     `ZDOPeer.m_zdos`). `ZRoutedRpc.HandleRoutedRPC` (1.0.14, lines 189-208) *silently drops*
    ///     an RPC whose target ZDO is unknown - no queue, no retry - so an RPC that overtakes the
    ///     ZDO creating its object is simply lost.
    ///   * Promoting a whole ZDOData package past another ZDOData package is safe, and this is the
    ///     decompile evidence for it: `RPC_ZDOData` (ZDOMan.cs:1125-1187) ignores any package whose
    ///     `DataRevision <= zdo.DataRevision` and applies an owner change only when the incoming
    ///     `OwnerRevision` is strictly greater. Both revisions are monotonic, so ZDOData is
    ///     idempotent and order-insensitive between packages.
    ///   * Ping/pong (hash 0) stays in bulk on purpose: it is the RTT measurement, and promoting it
    ///     would hide exactly the queue delay this module exists to shrink.
    ///   * `MaxPrioBytesPerDrain` stops the prio queue starving bulk.
    ///
    /// <b>Hot ZDOs.</b> A routed RPC in `HotMethods` (`RPC_Damage` / `Hit` / `RPC_Hit`) marks its
    /// target ZDO hot for `HotWindowMs`. On the server the attacker's peer id is remembered with
    /// it; as soon as that ZDO's `DataRevision` moves (the owner has applied the hit and answered)
    /// the server calls `ZDOMan.ForceSendZDO(attackerPeerId, uid)` - `AddForceSendZdos` inserts at
    /// index 0 of that peer's next sync list - and flags that peer's next ZDOData package prio.
    /// On the owner's own client `FastFlushOnDamage` postfixes the seven owner-gated `RPC_Damage` /
    /// `RPC_Hit` bodies and does the same plus a send-timer nudge, which buys the residual 0-50 ms
    /// client cadence wait. Hot ZDOs are never duplicated into a package of their own; we only
    /// reorder what vanilla was going to send anyway.
    ///
    /// <b>Vanilla clients.</b> The server half is entirely server-local: send ORDER changes, the
    /// bytes do not. An unmodded client benefits with no negotiation of any kind.
    ///
    /// <b>Backpressure.</b> Holding bytes outside `m_sendQueue` would blind every consumer of
    /// `ZSteamSocket.GetSendQueueSize()` - above all `ZDOMan.SendZDOs`' own high-water check, which
    /// is what stops the server generating ZDO updates faster than the link takes them. A postfix
    /// adds our held bytes back, so SendBudget, AdaptiveBudget, PeerTelemetry and vanilla all see
    /// the true backlog.
    ///
    /// Headless self-test: <see cref="HitLatencySelfTest"/>, reported in ILSelfTest's PASS/FAIL line.
    /// </summary>
    internal sealed class PriorityLaneModule : FeatureModule
    {
        public override string Name => "PriorityLane";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "PriorityLane";

        protected override string EnabledDescription =>
            "Shape this machine's outgoing Steam traffic: hold the backlog in a managed priority " +
            "queue and hand Steam only about one bandwidth-delay product at a time, so a hit RPC " +
            "no longer waits behind tens of kilobytes of bulk ZDO data inside Steam's own buffer. " +
            "Send ORDER only - every byte stays vanilla-legal, so unmodded clients benefit too.";

        // ---- documented vanilla wire hashes (asserted against the game at patch time) ----------

        internal const int HashPing = 0;
        internal const int HashRoutedRpc = -667652280;
        internal const int HashZdoData = -1975616347;
        internal const int HashPeerInfo = -725574882;
        internal const int HashNetTime = -2045981424;
        internal const int HashRefPos = 1664081997;
        internal const int HashPlayerList = -265949079;

        // ---- config -----------------------------------------------------------------------

        private ConfigEntry<int> _steamInflightBytes;
        private ConfigEntry<int> _hotWindowMs;
        private ConfigEntry<bool> _fastFlushOnDamage;
        private ConfigEntry<bool> _requireKnownZdo;
        private ConfigEntry<string> _denyListedRpcs;
        private ConfigEntry<int> _maxPrioBytesPerDrain;
        private ConfigEntry<string> _hotMethods;

        internal static bool Active;
        internal static int InflightCap = 6144;
        internal static float HotWindowSec = 1.5f;
        internal static bool FastFlush = true;
        internal static bool RequireKnownZdo = true;
        internal static int MaxPrioBytes = 8192;

        /// <summary>Inner routed-method hashes that must never be promoted.</summary>
        internal static readonly HashSet<int> DenyHashes = new HashSet<int>();
        /// <summary>Inner routed-method hashes that mark their target ZDO hot.</summary>
        internal static readonly HashSet<int> HotHashes = new HashSet<int>();
        /// <summary>
        /// Inner routed-method hashes that are ordering barriers: never promoted, and nothing
        /// queued after one may overtake it. Compression's three handshake RPCs, always, whatever
        /// the config says - their byte position in the FIFO IS the framing switch point.
        /// </summary>
        internal static readonly HashSet<int> BarrierHashes = new HashSet<int>();

        // ---- counters (StatusDetail / ss.lag) ----------------------------------------------

        internal static long PktsPromoted, PktsBulk, PktsDemotedUnknownZdo, PktsDemotedDenied, HotHits;
        internal static int PeakHeldBytes;
        /// <summary>Times the byte-offset classifier disagreed with ZRoutedRpc's own fields.</summary>
        internal static int ClassifierMismatches;

        // ---- per-socket state ----------------------------------------------------------------

        private sealed class Lane
        {
            public readonly LaneQueue Queue = new LaneQueue();
            /// <summary>Packages vanilla failed to send last call. They stay put, at the FRONT.</summary>
            public readonly HashSet<byte[]> Leftover = new HashSet<byte[]>();
            /// <summary>Set when a hot ZDO was force-sent to this peer: its next ZDOData is prio.</summary>
            public bool HotZdoPending;
        }

        private static readonly Dictionary<ZSteamSocket, Lane> Lanes = new Dictionary<ZSteamSocket, Lane>();
        private static readonly List<byte[]> DrainBuf = new List<byte[]>(32);
        private static readonly HotSet Hot = new HotSet();

        private static bool _warned;

        // ---- config ------------------------------------------------------------------------

        protected override void Bind()
        {
            _steamInflightBytes = BindSynced("SteamInflightBytes", 6144,
                "How many bytes may be outstanding towards Steam for one peer before this machine " +
                "holds the rest back in its own priority queue. About one bandwidth-delay product: " +
                "the residual head-of-line delay a latency-critical package can suffer is roughly " +
                "this / the peer's bandwidth (6144 B at ~250 KB/s = ~25 ms). Lower = lower latency, " +
                "more send calls; too low costs throughput. 0 = shaping OFF (hot marking and " +
                "FastFlushOnDamage still work)." + Profiles.Note);
            _hotWindowMs = BindSynced("HotWindowMs", 1500,
                "How long an object stays 'hot' after it was damaged. While hot, the ZDO update " +
                "carrying its new health is force-sent to the attacker and its package is sent " +
                "ahead of bulk traffic.");
            _fastFlushOnDamage = BindSynced("FastFlushOnDamage", true,
                "On the machine that OWNS a damaged object, force the changed ZDO to the front of " +
                "the next sync list and nudge the send timer, instead of waiting up to a full " +
                "client send tick (~50 ms). Owner-side only; costs one hash-set add per hit.");
            _requireKnownZdo = BindSynced("RequireKnownZdo", true,
                "Only send a routed RPC ahead of bulk traffic when the receiving peer provably " +
                "already has the RPC's target ZDO. Vanilla silently DROPS a routed RPC whose target " +
                "ZDO it does not know, so an RPC that overtook the ZDO creating its object would be " +
                "lost. Leave this on.");
            _denyListedRpcs = BindSynced("DenyListedRpcs", "DestroyZDO",
                "Comma-separated routed-RPC method names that are never promoted, whatever else " +
                "says. DestroyZDO must stay here: promoted, a destroy could overtake the ZDOData " +
                "that creates the object and the receiver would re-create it as a ghost. " +
                "Compression's own SS_Caps/SS_Ready/SS_Off are always treated as ordering barriers " +
                "and need not be listed.");
            _maxPrioBytesPerDrain = BindSynced("MaxPrioBytesPerDrain", 8192,
                "Starvation guard: at most this many priority bytes are handed to Steam before " +
                "bulk traffic gets its turn within one drain.");
            _hotMethods = BindSynced("HotMethods", "RPC_Damage,Hit,RPC_Hit",
                "Comma-separated routed-RPC method names whose target object becomes 'hot' " +
                "(see HotWindowMs). These are vanilla's three damage entry points.");

            Watch(_steamInflightBytes); Watch(_hotWindowMs); Watch(_fastFlushOnDamage);
            Watch(_requireKnownZdo); Watch(_denyListedRpcs); Watch(_maxPrioBytesPerDrain);
            Watch(_hotMethods);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[PriorityLane] inflight=" + InflightCap + "B hotWindow=" +
                        (HotWindowSec * 1000f).ToString("F0") + "ms requireKnownZdo=" + RequireKnownZdo +
                        " fastFlush=" + FastFlush + " maxPrioBytesPerDrain=" + MaxPrioBytes);
        }

        private void ReadConfig()
        {
            InflightCap = Math.Max(0, _steamInflightBytes.Value);
            HotWindowSec = Mathf.Clamp(_hotWindowMs.Value, 0, 60000) / 1000f;
            FastFlush = _fastFlushOnDamage.Value;
            RequireKnownZdo = _requireKnownZdo.Value;
            MaxPrioBytes = Math.Max(512, _maxPrioBytesPerDrain.Value);

            DenyHashes.Clear();
            foreach (var n in Split(_denyListedRpcs.Value)) DenyHashes.Add(n.GetStableHashCode());
            HotHashes.Clear();
            foreach (var n in Split(_hotMethods.Value)) HotHashes.Add(n.GetStableHashCode());

            // Never configurable: these three define Compression's framing switch point.
            BarrierHashes.Clear();
            BarrierHashes.Add(Net.CompressionModule.RpcCaps.GetStableHashCode());
            BarrierHashes.Add(Net.CompressionModule.RpcReady.GetStableHashCode());
            BarrierHashes.Add(Net.CompressionModule.RpcOff.GetStableHashCode());
        }

        private static string[] Split(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new string[0];
            var parts = csv.Split(',');
            var outp = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) outp.Add(t);
            }
            return outp.ToArray();
        }

        // ---- patches ---------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            AssertWireHashes();

            var send = AccessTools.Method(typeof(ZSteamSocket), "SendQueuedPackages");
            if (send == null || send.GetParameters().Length != 0)
                throw new Exception("PriorityLane: ZSteamSocket.SendQueuedPackages(void) not found - refusing to patch");
            var queueSize = AccessTools.Method(typeof(ZSteamSocket), "GetSendQueueSize");
            if (queueSize == null || queueSize.GetParameters().Length != 0 || queueSize.ReturnType != typeof(int))
                throw new Exception("PriorityLane: ZSteamSocket.GetSendQueueSize():int not found - refusing to patch " +
                                    "(without it our held bytes would be invisible to every send budget)");
            if (AccessTools.Field(typeof(ZSteamSocket), "m_sendQueue") == null)
                throw new Exception("PriorityLane: ZSteamSocket.m_sendQueue not found - refusing to patch");

            var routeRpc = AccessTools.Method(typeof(ZRoutedRpc), "RouteRPC",
                new[] { typeof(ZRoutedRpc.RoutedRPCData) });
            if (routeRpc == null)
                throw new Exception("PriorityLane: ZRoutedRpc.RouteRPC(RoutedRPCData) not found - refusing to patch");

            // ZDOPeer bookkeeping is what RequireKnownZdo's proof reads.
            if (AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_zdos") == null ||
                AccessTools.Field(typeof(ZDOMan.ZDOPeer), "m_peer") == null)
                throw new Exception("PriorityLane: ZDOMan.ZDOPeer.m_zdos/m_peer not found - refusing to patch " +
                                    "(RequireKnownZdo could not be proven and promoting blind loses RPCs)");
            if (AccessTools.Method(typeof(ZDOMan), "GetPeer", new[] { typeof(long) }) == null)
                throw new Exception("PriorityLane: ZDOMan.GetPeer(long) not found - refusing to patch");
            if (AccessTools.Method(typeof(ZDOMan), "ForceSendZDO", new[] { typeof(long), typeof(ZDOID) }) == null)
                throw new Exception("PriorityLane: ZDOMan.ForceSendZDO(long, ZDOID) not found - refusing to patch");

            ReadConfig();

            // Priority.High ON PURPOSE: we must run before Compression's framing prefix, so that
            // everything we hold back is still raw (never framed twice) and Compression frames
            // exactly the packages that are about to go on the wire. SendQueueGuard stays at Low.
            Harmony.Patch(send,
                prefix: new HarmonyMethod(typeof(PriorityLaneModule), nameof(SendPrefix)) { priority = Priority.High },
                postfix: new HarmonyMethod(typeof(PriorityLaneModule), nameof(SendPostfix)));
            Harmony.Patch(queueSize,
                postfix: new HarmonyMethod(typeof(PriorityLaneModule), nameof(QueueSizePostfix)));
            Harmony.Patch(routeRpc,
                prefix: new HarmonyMethod(typeof(PriorityLaneModule), nameof(RouteRpcPrefix)));

            int flushed = 0;
            if (FastFlush) flushed = PatchOwnerDamageBodies();

            Lanes.Clear();
            Hot.Clear();
            Active = true;

            Log.LogInfo("[PriorityLane] shaping ZSteamSocket's drain at Priority.High (vanilla still " +
                        "sends; SendQueueGuard keeps its back-off at Priority.Low and Compression " +
                        "frames between us): inflight=" +
                        (InflightCap == 0 ? "OFF (0)" : InflightCap + "B") +
                        " hotWindow=" + (HotWindowSec * 1000f).ToString("F0") + "ms" +
                        " requireKnownZdo=" + RequireKnownZdo +
                        " deny=" + _denyListedRpcs.Value +
                        " fastFlush=" + (FastFlush ? flushed + " owner RPC bodies" : "off"));
        }

        /// <summary>
        /// The seven owner-gated damage bodies (each starts with an IsOwner() guard, so a postfix
        /// runs only on the owner). Missing types are not fatal - a mod-added prefab family is
        /// allowed to be absent - but a type that exists with no matching method is.
        /// </summary>
        private int PatchOwnerDamageBodies()
        {
            var targets = new[]
            {
                new[] { "Character", "RPC_Damage" },
                new[] { "Destructible", "RPC_Damage" },
                new[] { "WearNTear", "RPC_Damage" },
                new[] { "MineRock5", "RPC_Damage" },
                new[] { "MineRock", "RPC_Hit" },
                new[] { "TreeBase", "RPC_Damage" },
                new[] { "TreeLog", "RPC_Damage" },
            };
            int n = 0;
            foreach (var t in targets)
            {
                var type = AccessTools.TypeByName(t[0]);
                if (type == null)
                    throw new Exception("PriorityLane: type " + t[0] + " not found - refusing to patch");
                var m = AccessTools.Method(type, t[1]);
                if (m == null)
                    throw new Exception("PriorityLane: " + t[0] + "." + t[1] + " not found - refusing to patch");
                Harmony.Patch(m, postfix: new HarmonyMethod(typeof(PriorityLaneModule), nameof(OwnerDamagePostfix)));
                n++;
            }
            return n;
        }

        /// <summary>
        /// The classifier reads a package's method hash straight out of its bytes, so the hash
        /// algorithm itself is an assumption. Prove it against the running game and refuse to
        /// patch if any of the seven documented values has moved.
        /// </summary>
        private static void AssertWireHashes()
        {
            Check("RoutedRPC", HashRoutedRpc);
            Check("ZDOData", HashZdoData);
            Check("PeerInfo", HashPeerInfo);
            Check("NetTime", HashNetTime);
            Check("RefPos", HashRefPos);
            Check("PlayerList", HashPlayerList);
        }

        private static void Check(string method, int expected)
        {
            int actual = method.GetStableHashCode();
            if (actual != expected)
                throw new Exception("PriorityLane: \"" + method + "\".GetStableHashCode() is " + actual +
                                    ", expected " + expected + " - the wire hashes this module classifies " +
                                    "packages by have changed; refusing to patch");
        }

        public override void Disable()
        {
            Active = false;
            Lanes.Clear();
            Hot.Clear();
            base.Disable();
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "prio=" + PktsPromoted + " bulk=" + PktsBulk + " held-peak=" + PeakHeldBytes + "B" +
                   (PktsDemotedUnknownZdo > 0 ? " unknown-zdo=" + PktsDemotedUnknownZdo : "") +
                   (PktsDemotedDenied > 0 ? " denied=" + PktsDemotedDenied : "") +
                   (ClassifierMismatches > 0 ? " CLASSIFIER-MISMATCH=" + ClassifierMismatches : "");
        }

        // ---- the shaper ------------------------------------------------------------------

        private static bool SendPrefix(ZSteamSocket __instance)
        {
            if (!Active || InflightCap <= 0) return true;
            try
            {
                if (__instance == null || !__instance.IsConnected()) return true;
                var q = __instance.m_sendQueue;
                if (q == null) return true;

                var lane = LaneFor(__instance);
                Intake(__instance, lane, q);
                Refill(__instance, lane);
            }
            catch (Exception e)
            {
                // Shaping must never break the send path. Fall back to vanilla for this call.
                WarnOnce("shaper", e);
            }
            return true;
        }

        /// <summary>
        /// Lift every package that is NOT a known leftover out of m_sendQueue into our managed
        /// queues. Leftovers sit at the front (Queue is FIFO and vanilla dequeues from the front),
        /// they may already be framed by Compression, and pulling them out would lose Compression's
        /// reference-identity record of what it framed - so they stay exactly where they are.
        /// </summary>
        private static void Intake(ZSteamSocket sock, Lane lane, Queue<byte[]> q)
        {
            if (q.Count == 0)
            {
                lane.Leftover.Clear();
                return;
            }

            var keep = new Queue<byte[]>(q.Count);
            bool leading = lane.Leftover.Count > 0;
            foreach (var pkt in q)
            {
                if (pkt == null) continue;
                if (leading && lane.Leftover.Contains(pkt)) { keep.Enqueue(pkt); continue; }
                leading = false;
                lane.Queue.Enqueue(Classify(sock, lane, pkt));
            }
            lane.Leftover.Clear();
            sock.m_sendQueue = keep;

            if (lane.Queue.HeldBytes > PeakHeldBytes) PeakHeldBytes = lane.Queue.HeldBytes;
        }

        /// <summary>Put back at most one bandwidth-delay product, priority first.</summary>
        private static void Refill(ZSteamSocket sock, Lane lane)
        {
            if (lane.Queue.Count == 0) return;

            int held = lane.Queue.HeldBytes;
            int inflight;
            // Our own GetSendQueueSize postfix added `held` back; subtract it to get what is really
            // queued for or inside Steam.
            try { inflight = sock.GetSendQueueSize() - held; }
            catch { inflight = 0; }
            if (inflight < 0) inflight = 0;

            int allowance = InflightCap - inflight;
            bool atLeastOne = inflight <= 0;      // never deadlock on a package bigger than the cap
            if (allowance <= 0 && !atLeastOne) return;

            DrainBuf.Clear();
            lane.Queue.Drain(allowance, MaxPrioBytes, atLeastOne, DrainBuf);
            var q = sock.m_sendQueue;
            for (int i = 0; i < DrainBuf.Count; i++) q.Enqueue(DrainBuf[i]);
            DrainBuf.Clear();
        }

        /// <summary>Whatever vanilla could not send is a leftover: remember it by reference.</summary>
        private static void SendPostfix(ZSteamSocket __instance)
        {
            if (!Active || InflightCap <= 0) return;
            try
            {
                Lane lane;
                if (__instance == null || !Lanes.TryGetValue(__instance, out lane)) return;
                lane.Leftover.Clear();
                var q = __instance.m_sendQueue;
                if (q == null) return;
                foreach (var pkt in q) if (pkt != null) lane.Leftover.Add(pkt);
            }
            catch (Exception e) { WarnOnce("postfix", e); }
        }

        /// <summary>
        /// Make our managed backlog visible to every send budget (ZDOMan.SendZDOs' high-water check
        /// above all). Without this the server would keep generating ZDO updates into a queue it
        /// cannot see.
        /// </summary>
        private static void QueueSizePostfix(ZSteamSocket __instance, ref int __result)
        {
            if (!Active) return;
            try
            {
                Lane lane;
                if (__instance != null && Lanes.TryGetValue(__instance, out lane))
                    __result += lane.Queue.HeldBytes;
            }
            catch { /* never break a budget read */ }
        }

        private static Lane LaneFor(ZSteamSocket s)
        {
            Lane lane;
            if (!Lanes.TryGetValue(s, out lane))
            {
                lane = new Lane();
                Lanes[s] = lane;
                if (Lanes.Count > 64) Prune();
            }
            return lane;
        }

        private static void Prune()
        {
            var dead = new List<ZSteamSocket>();
            foreach (var kv in Lanes)
                if (kv.Key == null || !kv.Key.IsConnected()) dead.Add(kv.Key);
            foreach (var d in dead) Lanes.Remove(d);
        }

        /// <summary>
        /// Packages this socket is holding, raw and unframed, oldest first. Compression's
        /// plain-protection walks these as well as m_sendQueue, so a package queued before its
        /// SS_Ready still goes out plain even if we held it across the flip.
        /// </summary>
        internal static void CollectHeld(ZSteamSocket sock, ICollection<byte[]> into)
        {
            if (!Active || sock == null || into == null) return;
            Lane lane;
            if (!Lanes.TryGetValue(sock, out lane)) return;
            lane.Queue.CollectAll(into);
        }

        // ---- classification -----------------------------------------------------------------

        private static LanePacket Classify(ZSteamSocket sock, Lane lane, byte[] pkt)
        {
            bool hot = lane.HotZdoPending;
            LaneWire.Reason why;
            var verdict = LaneWire.Decide(pkt, hot, DenyHashes, BarrierHashes, RequireKnownZdo,
                                          (u, i) => PeerKnowsZdo(sock, u, i), out why);

            if (why == LaneWire.Reason.HotZdoData) { lane.HotZdoPending = false; HotHits++; }
            else if (why == LaneWire.Reason.DenyListed) PktsDemotedDenied++;
            else if (why == LaneWire.Reason.UnknownZdo) PktsDemotedUnknownZdo++;

            var p = new LanePacket
            {
                Data = pkt,
                Prio = verdict == LaneWire.Verdict.Prio,
                Barrier = verdict == LaneWire.Verdict.Barrier,
            };
            if (p.Prio) PktsPromoted++; else PktsBulk++;
            return p;
        }

        /// <summary>
        /// The `m_zdos` proof rule: promote only when the receiving peer provably has the ZDO.
        /// Two proofs, cheapest first: it owns the ZDO, or it is in that peer's sent-ZDO table.
        /// </summary>
        private static bool PeerKnowsZdo(ZSteamSocket sock, long zdoUser, uint zdoId)
        {
            try
            {
                var zm = ZDOMan.instance;
                var net = ZNet.instance;
                if (zm == null || net == null) return false;

                var id = new ZDOID(zdoUser, zdoId);
                var zdo = zm.GetZDO(id);
                if (zdo == null) return false;

                long peerUid = PeerUidFor(sock, net);
                if (peerUid == 0L) return false;
                if (zdo.GetOwner() == peerUid) return true;      // the receiver owns it

                var zp = zm.GetPeer(peerUid);
                return zp != null && zp.m_zdos.ContainsKey(id);
            }
            catch (Exception e)
            {
                WarnOnce("known-zdo", e);
                return false;
            }
        }

        private static long PeerUidFor(ZSteamSocket sock, ZNet net)
        {
            var peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                if (p == null || p.m_socket == null) continue;
                if (ReferenceEquals(SocketResolve.ResolveSteamSocket(p.m_socket), sock)) return p.m_uid;
            }
            return 0L;
        }

        // ---- hot ZDOs -------------------------------------------------------------------------

        /// <summary>
        /// Every routed RPC passes through here on its way to the wire. Two jobs: mark a damaged
        /// object hot, and validate the byte-offset classifier against ZRoutedRpc's own fields.
        /// </summary>
        private static void RouteRpcPrefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (!Active || rpcData == null) return;
            try
            {
                if (rpcData.m_targetZDO.IsNone()) return;
                if (!HotHashes.Contains(rpcData.m_methodHash)) return;
                Hot.Mark(rpcData.m_targetZDO, rpcData.m_senderPeerID, Now(), CurrentDataRevision(rpcData.m_targetZDO));
            }
            catch (Exception e) { WarnOnce("route-rpc", e); }
        }

        private static uint CurrentDataRevision(ZDOID id)
        {
            var zm = ZDOMan.instance;
            if (zm == null) return 0u;
            var zdo = zm.GetZDO(id);
            return zdo == null ? 0u : zdo.DataRevision;
        }

        /// <summary>
        /// Owner-side, after vanilla applied the hit (every one of these bodies starts with an
        /// IsOwner() guard, so if we are not the owner nothing happened and we do nothing).
        /// Front-inserts the changed ZDO in every peer's next sync list and nudges the client send
        /// timer past its 0.05 s gate, which removes the residual 0-50 ms cadence wait.
        /// </summary>
        private static void OwnerDamagePostfix(object __instance)
        {
            if (!Active || !FastFlush) return;
            try
            {
                var nview = NViewOf(__instance);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                var zm = ZDOMan.instance;
                if (zm == null) return;

                var id = nview.GetZDO().m_uid;
                zm.ForceSendZDO(id);
                FlagHotForAllPeers();

                if (ZNet.instance != null && !ZNet.instance.IsServer())
                    zm.m_sendTimer = Mathf.Max(zm.m_sendTimer, 0.0501f);
            }
            catch (Exception e) { WarnOnce("owner-damage", e); }
        }

        private static readonly Dictionary<Type, System.Reflection.FieldInfo> NViewFields =
            new Dictionary<Type, System.Reflection.FieldInfo>();

        private static ZNetView NViewOf(object instance)
        {
            if (instance == null) return null;
            var t = instance.GetType();
            System.Reflection.FieldInfo f;
            if (!NViewFields.TryGetValue(t, out f))
            {
                f = AccessTools.Field(t, "m_nview");
                NViewFields[t] = f;
            }
            return f == null ? null : f.GetValue(instance) as ZNetView;
        }

        private static void FlagHotForAllPeers()
        {
            var net = ZNet.instance;
            if (net == null) return;
            var peers = net.GetPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                var sock = peers[i] == null ? null : SocketResolve.ResolveSteamSocket(peers[i].m_socket);
                if (sock == null) continue;
                Lane lane;
                if (Lanes.TryGetValue(sock, out lane)) lane.HotZdoPending = true;
            }
        }

        /// <summary>
        /// Per-frame: as soon as a hot ZDO's DataRevision moves - the owner has applied the hit and
        /// answered - force-send it to the attacker and flag that peer's next ZDOData package prio.
        /// Server side only; on a client the owner-side postfix above does the same job directly.
        /// </summary>
        internal static void Tick(float dt)
        {
            if (!Active) return;
            try
            {
                float now = Now();
                Hot.Expire(now, HotWindowSec);
                if (Hot.Count == 0) return;

                var zm = ZDOMan.instance;
                var net = ZNet.instance;
                if (zm == null || net == null || !net.IsServer()) return;

                Hot.ForEachReady(zm, (id, attackerUid) =>
                {
                    zm.ForceSendZDO(attackerUid, id);
                    var peer = net.GetPeer(attackerUid);
                    var sock = peer == null ? null : SocketResolve.ResolveSteamSocket(peer.m_socket);
                    Lane lane;
                    if (sock != null && Lanes.TryGetValue(sock, out lane)) lane.HotZdoPending = true;
                });
            }
            catch (Exception e) { WarnOnce("tick", e); }
        }

        private static float Now()
        {
            return Time.realtimeSinceStartup;
        }

        private static void WarnOnce(string what, Exception e)
        {
            if (_warned) return;
            _warned = true;
            Log.LogWarning("[PriorityLane] " + what + " failed once (shaping falls back to vanilla for " +
                           "that call; this is logged only once): " + e);
        }
    }

    // ==== pure, headlessly testable pieces =================================================

    /// <summary>One queued package plus the verdict the classifier reached about it.</summary>
    internal sealed class LanePacket
    {
        public byte[] Data;
        /// <summary>May be sent ahead of bulk traffic.</summary>
        public bool Prio;
        /// <summary>Ordering barrier: never promoted, and nothing queued after it may overtake it.</summary>
        public bool Barrier;
        /// <summary>Enqueue order, assigned by <see cref="LaneQueue"/>.</summary>
        public long Seq;
    }

    /// <summary>
    /// Reads a Valheim package's classification straight out of its bytes. `ZRpc.Invoke` clears
    /// its package, writes `method.GetStableHashCode()` as the first int and then the parameters,
    /// so bytes 0..3 of every byte[] reaching `ZSteamSocket.Send` are the method hash (LE int32).
    ///
    /// A RoutedRPC's payload is a ZPackage parameter, so it is written as a 4-byte length followed
    /// by `RoutedRPCData.Serialize`:
    ///   0 outer hash(4) | 4 length(4) | 8 msgID(8) | 16 sender(8) | 24 targetPeer(8)
    ///   | 32 targetZDO.UserID(8) | 40 targetZDO.ID(4) | 44 inner method hash(4)
    /// (`ZPackage.Write(ZDOID)` writes UserID as a long then ID as a uint - verified in the 1.0.14
    /// decompile - and `ZRpc.m_DEBUG` is false, so no method name is written after the outer hash.)
    /// No allocation, no parsing beyond two BitConverter reads.
    /// </summary>
    internal static class LaneWire
    {
        internal const int RoutedInnerHashOffset = 44;
        internal const int RoutedZdoUserOffset = 32;
        internal const int RoutedZdoIdOffset = 40;
        internal const int RoutedMinLength = 48;

        internal static int OuterHash(byte[] pkt)
        {
            if (pkt == null || pkt.Length < 4) return int.MinValue;   // not classifiable
            return BitConverter.ToInt32(pkt, 0);
        }

        internal static bool TryReadRouted(byte[] pkt, out int innerHash, out long zdoUser, out uint zdoId)
        {
            innerHash = 0; zdoUser = 0L; zdoId = 0u;
            if (pkt == null || pkt.Length < RoutedMinLength) return false;
            zdoUser = BitConverter.ToInt64(pkt, RoutedZdoUserOffset);
            zdoId = BitConverter.ToUInt32(pkt, RoutedZdoIdOffset);
            innerHash = BitConverter.ToInt32(pkt, RoutedInnerHashOffset);
            return true;
        }

        internal enum Verdict { Bulk, Prio, Barrier }

        /// <summary>Why <see cref="Decide"/> reached its verdict - counters and tests read this.</summary>
        internal enum Reason { Plain, HotZdoData, NoTargetZdo, KnownZdo, DenyListed, UnknownZdo, HandshakeBarrier, TooShort }

        /// <summary>
        /// The whole promotion decision, pure: bytes in, verdict out. `peerKnowsZdo` is the
        /// RequireKnownZdo proof (the receiving peer owns the ZDO, or has been sent it); the test
        /// harness substitutes its own.
        /// </summary>
        internal static Verdict Decide(byte[] pkt, bool hotZdoPending, HashSet<int> deny, HashSet<int> barriers,
                                       bool requireKnownZdo, Func<long, uint, bool> peerKnowsZdo, out Reason why)
        {
            why = Reason.Plain;
            int outer = OuterHash(pkt);

            if (outer == PriorityLaneModule.HashZdoData)
            {
                if (!hotZdoPending) return Verdict.Bulk;
                why = Reason.HotZdoData;
                return Verdict.Prio;
            }
            if (outer != PriorityLaneModule.HashRoutedRpc) return Verdict.Bulk;

            int inner; long zdoUser; uint zdoId;
            if (!TryReadRouted(pkt, out inner, out zdoUser, out zdoId))
            {
                why = Reason.TooShort;
                return Verdict.Bulk;
            }
            if (barriers != null && barriers.Contains(inner))
            {
                why = Reason.HandshakeBarrier;
                return Verdict.Barrier;
            }
            if (deny != null && deny.Contains(inner))
            {
                why = Reason.DenyListed;
                return Verdict.Bulk;
            }
            if (zdoUser == 0L && zdoId == 0u)
            {
                why = Reason.NoTargetZdo;           // no target ZDO: there is nothing it can race
                return Verdict.Prio;
            }
            if (!requireKnownZdo || (peerKnowsZdo != null && peerKnowsZdo(zdoUser, zdoId)))
            {
                why = Reason.KnownZdo;
                return Verdict.Prio;
            }
            why = Reason.UnknownZdo;
            return Verdict.Bulk;
        }
    }

    /// <summary>
    /// The shaper itself: two strict-FIFO queues and the rule for choosing between them. Pure -
    /// no Unity, no Steam, no game types - so <see cref="HitLatencySelfTest"/> can drive it headlessly.
    ///
    /// Invariants it must keep, and does:
    ///   * every package that goes in comes out exactly once, as the same byte[] reference
    ///     (frames are whole packages and are never split, copied or edited);
    ///   * order inside each queue is never changed;
    ///   * a prio package may overtake bulk, but never a barrier queued before it;
    ///   * at most `allowance` bytes leave per drain (unless nothing at all is in flight, in which
    ///     case exactly one package may leave, so an oversized package can never deadlock);
    ///   * at most `maxPrioBytes` priority bytes leave per drain before bulk gets its turn.
    /// </summary>
    internal sealed class LaneQueue
    {
        private readonly Queue<LanePacket> _prio = new Queue<LanePacket>();
        private readonly Queue<LanePacket> _bulk = new Queue<LanePacket>();
        private readonly Queue<long> _barriers = new Queue<long>();
        private long _seq;

        internal int HeldBytes { get; private set; }
        internal int Count { get { return _prio.Count + _bulk.Count; } }
        internal int PrioCount { get { return _prio.Count; } }
        internal int BulkCount { get { return _bulk.Count; } }

        internal void Enqueue(LanePacket p)
        {
            if (p == null || p.Data == null) return;
            p.Seq = _seq++;
            if (p.Barrier)
            {
                p.Prio = false;
                _barriers.Enqueue(p.Seq);
            }
            if (p.Prio) _prio.Enqueue(p); else _bulk.Enqueue(p);
            HeldBytes += p.Data.Length;
        }

        private long EarliestBarrierSeq
        {
            get { return _barriers.Count > 0 ? _barriers.Peek() : long.MaxValue; }
        }

        internal void Drain(int allowance, int maxPrioBytes, bool allowAtLeastOne, List<byte[]> into)
        {
            int sent = 0, prioBytes = 0;
            bool first = true;

            while (_prio.Count > 0 || _bulk.Count > 0)
            {
                bool fromPrio = false;
                LanePacket next = null;

                if (_prio.Count > 0)
                {
                    var head = _prio.Peek();
                    if (head.Seq < EarliestBarrierSeq &&
                        (prioBytes + head.Data.Length <= maxPrioBytes || _bulk.Count == 0))
                    {
                        next = head;
                        fromPrio = true;
                    }
                }
                if (next == null)
                {
                    if (_bulk.Count > 0) next = _bulk.Peek();
                    else if (_prio.Count > 0) { next = _prio.Peek(); fromPrio = true; }
                    else break;
                }

                int len = next.Data.Length;
                if (sent + len > allowance && !(first && allowAtLeastOne)) break;

                if (fromPrio) { _prio.Dequeue(); prioBytes += len; }
                else
                {
                    _bulk.Dequeue();
                    if (next.Barrier && _barriers.Count > 0) _barriers.Dequeue();
                }

                into.Add(next.Data);
                HeldBytes -= len;
                sent += len;
                first = false;
            }
        }

        /// <summary>Everything still held, oldest first, for Compression's plain-protection.</summary>
        internal void CollectAll(ICollection<byte[]> into)
        {
            foreach (var p in _bulk) into.Add(p.Data);
            foreach (var p in _prio) into.Add(p.Data);
        }

        internal void Clear()
        {
            _prio.Clear(); _bulk.Clear(); _barriers.Clear();
            HeldBytes = 0;
        }
    }

    /// <summary>
    /// Objects damaged recently, with the attacker that hit them and the DataRevision the object
    /// had at that moment. An entry becomes "ready" when the revision moves - that is the owner's
    /// authoritative answer, and the moment worth rushing back to the attacker.
    /// </summary>
    internal sealed class HotSet
    {
        internal struct Entry
        {
            public long Attacker;
            public float At;
            public uint RevisionAtHit;
            public bool Fired;
        }

        private readonly Dictionary<ZDOID, Entry> _hot = new Dictionary<ZDOID, Entry>();
        private readonly List<ZDOID> _scratch = new List<ZDOID>();

        internal int Count { get { return _hot.Count; } }

        internal void Mark(ZDOID id, long attacker, float now, uint revision)
        {
            if (_hot.Count > 256) return;     // a storm must not become a memory leak
            _hot[id] = new Entry { Attacker = attacker, At = now, RevisionAtHit = revision, Fired = false };
        }

        internal bool IsHot(ZDOID id, float now, float windowSec)
        {
            Entry e;
            if (!_hot.TryGetValue(id, out e)) return false;
            return now - e.At <= windowSec;
        }

        internal void Expire(float now, float windowSec)
        {
            if (_hot.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _hot)
                if (now - kv.Value.At > windowSec) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++) _hot.Remove(_scratch[i]);
            _scratch.Clear();
        }

        internal void ForEachReady(ZDOMan zm, Action<ZDOID, long> act)
        {
            if (_hot.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _hot)
            {
                if (kv.Value.Fired || kv.Value.Attacker == 0L) continue;
                var zdo = zm.GetZDO(kv.Key);
                if (zdo == null || zdo.DataRevision == kv.Value.RevisionAtHit) continue;
                _scratch.Add(kv.Key);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                var id = _scratch[i];
                var e = _hot[id];
                e.Fired = true;
                _hot[id] = e;
                act(id, e.Attacker);
            }
            _scratch.Clear();
        }

        internal void Clear() { _hot.Clear(); _scratch.Clear(); }
    }
}
