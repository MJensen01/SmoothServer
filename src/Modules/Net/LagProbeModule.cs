using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// Diagnostics only - measures the lag people actually complain about, while they play, and
    /// never changes a single gameplay value. Everything it records is machine-local except the
    /// ping interval, which the server drives.
    ///
    /// Three independent probes:
    ///
    /// <b>(a) Server -&gt; peer round trip.</b> Every <c>PingIntervalSec</c> the server sends the
    /// routed RPC <c>SS_Ping(seq, serverTimeMs)</c> to each ready peer; the client answers
    /// <c>SS_Pong(seq, serverTimeMs, clientFrameMs)</c> and the server computes RTT against its own
    /// clock (so the two machines need no clock sync at all), plus an EMA, jitter (|Δ| of
    /// consecutive RTTs), loss (pings never answered) and the client's own frame time. These land
    /// in the `[PeerTelemetry]` line as <c>rtt=…ms jit=…ms loss=…% cfps=…</c> and in StatsLog's
    /// per-peer record. They owe nothing to Steam's own statistics, so they keep working even if
    /// PeerTelemetry's Steam read regresses again (it read zeros for every peer through 0.5.0).
    ///
    /// <b>(b) Hit-registration latency (client).</b> "I hit it and nothing happened" is the
    /// complaint; this measures it. A postfix on the three vanilla methods that send a hit to the
    /// object's owner - <c>Character.Damage(HitData)</c>, <c>Destructible.Damage(HitData)</c>,
    /// <c>WearNTear.Damage(HitData)</c>, each of which is just
    /// <c>m_nview.InvokeRPC("RPC_Damage", hit)</c> - records the ZDOID, the owner uid and the
    /// timestamp, but only when this client does NOT own the ZDO (when it does, the damage is
    /// applied locally and there is no round trip to measure). The tick then watches that ZDO's
    /// <c>DataRevision</c>: the owner's authoritative reply (health change, destruction, state
    /// flip) arrives as a ZDO update and bumps the revision, so the first bump after the hit is
    /// the moment the hit registered. A ZDO that disappears counts as registered (the thing died);
    /// nothing after <c>HitTimeoutSec</c> counts as a timeout.
    ///
    /// <b>(c) Ownership churn (client).</b> A postfix on <c>ZDO.SetOwner</c> - vanilla only calls
    /// through when the owner really changes - counts transfers within 30 m of the local player.
    /// Churn is what makes hits land late: every transfer re-points the authority for that object.
    ///
    /// Overhead: no allocation in either patch body (a struct into a pre-grown dictionary), no
    /// per-ZDO work in any loop that vanilla runs per frame, timestamps from
    /// <c>Time.realtimeSinceStartup</c>, every body wrapped in try/catch with one-time warnings.
    /// The client-only patches are not installed at all on a dedicated server.
    ///
    /// <c>ss.lag</c> in the console (client or server) prints the current summary on demand.
    /// </summary>
    internal sealed class LagProbeModule : FeatureModule
    {
        public override string Name => "LagProbe";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "LagProbe";

        protected override string EnabledDescription =>
            "Measure lag while people play: server->player ping/jitter/loss, client hit-registration " +
            "latency, ZDO ownership churn. Diagnostics only - it never changes gameplay. Cheap enough " +
            "to leave on.";

        private const string RpcPing = "SS_Ping";
        private const string RpcPong = "SS_Pong";

        /// <summary>How long a hit may stay unanswered before it counts as a timeout.</summary>
        private const float HitTimeoutSec = 3f;
        /// <summary>Ownership transfers are counted within this radius of the local player.</summary>
        private const float ChurnRadius = 30f;
        private const float ChurnRadiusSqr = ChurnRadius * ChurnRadius;
        /// <summary>Hard caps so a stall can never turn the probe into the problem.</summary>
        private const int MaxPendingHits = 256;
        private const int MaxSamples = 512;

        private ConfigEntry<float> _pingInterval;
        private ConfigEntry<float> _summaryInterval;
        private ConfigEntry<int> _hitThreshold;

        internal static bool Active;
        internal static float PingIntervalSec = 5f;
        internal static float SummaryIntervalSec = 60f;
        internal static int HitLogThresholdMs = 250;

        private static bool _serverHalf;
        private static bool _rpcsRegistered;
        private static bool _cmdRegistered;
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        // ---- server state ----------------------------------------------------------------

        private sealed class PeerLag
        {
            public string Name;
            public int Seq;
            public float RttMs;
            public float EmaMs;
            public float JitterMs;
            public float ClientFrameMs;
            public int Sent;            // pings sent this interval
            public int Answered;        // pongs received this interval
            public int Lost;            // pings that timed out this interval
            public int DeferralsSeen;   // SendQueueGuard deferrals at the last summary
            public bool HaveRtt;
            public readonly Dictionary<int, float> Outstanding = new Dictionary<int, float>();
        }

        private static readonly Dictionary<long, PeerLag> Peers = new Dictionary<long, PeerLag>();
        private static readonly List<long> TempUids = new List<long>();
        private static readonly List<int> TempSeqs = new List<int>();
        private static float _pingAcc;

        // ---- client state ----------------------------------------------------------------

        private struct PendingHit
        {
            public long Owner;
            public string Prefab;
            public float SentAt;
            public uint Rev;
        }

        private struct OwnerStat
        {
            public int Count;
            public float TotalMs;
            public float MaxMs;
        }

        private static readonly Dictionary<ZDOID, PendingHit> Pending = new Dictionary<ZDOID, PendingHit>();
        private static readonly List<ZDOID> TempIds = new List<ZDOID>();
        private static readonly List<float> Samples = new List<float>(MaxSamples);
        private static readonly List<float> Sorted = new List<float>(MaxSamples);
        private static readonly Dictionary<long, OwnerStat> ByOwner = new Dictionary<long, OwnerStat>();
        private static int _hitLate;
        private static int _hitTimeouts;
        private static int _churn;
        private static float _frameMsEma = 16.7f;

        private static float _summaryAcc;

        // ---- config ------------------------------------------------------------------------

        protected override void Bind()
        {
            _pingInterval = BindSynced("PingIntervalSec", 5f,
                "Seconds between the server's SS_Ping round-trip probes to each player. Server-driven, " +
                "so it is synced. One tiny routed RPC each way per player per interval.");
            _summaryInterval = BindLocal("SummaryIntervalSec", 60f,
                "Seconds between LagProbe summary lines (server: per-peer rtt/jitter/loss; client: the " +
                "hit-latency histogram and ownership churn). Machine-local.");
            _hitThreshold = BindLocal("HitLogThresholdMs", 250,
                "Log an individual line for any hit on an object owned by someone else that took longer " +
                "than this to register. Machine-local; the histogram counts every hit regardless.");
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            _serverHalf = SmoothServerPlugin.IsServerSide;

            var rrpcCtor = AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            if (rrpcCtor == null) throw new Exception("ZRoutedRpc(bool) constructor not found");
            Harmony.Patch(rrpcCtor, postfix: new HarmonyMethod(typeof(LagProbeModule), nameof(RoutedRpcCtorPostfix)));

            if (!_serverHalf)
            {
                PatchDamage(typeof(Character), nameof(CharacterDamagePostfix));
                PatchDamage(typeof(Destructible), nameof(DestructibleDamagePostfix));
                PatchDamage(typeof(WearNTear), nameof(WearNTearDamagePostfix));

                var setOwner = AccessTools.Method(typeof(ZDO), "SetOwner", new[] { typeof(long) });
                if (setOwner == null) throw new Exception("ZDO.SetOwner(long) not found");
                Harmony.Patch(setOwner, postfix: new HarmonyMethod(typeof(LagProbeModule), nameof(SetOwnerPostfix)));
            }

            // The console command is a convenience, not the feature: a build without Terminal
            // (or a headless one that never inits it) still logs every summary.
            var initTerminal = AccessTools.Method(typeof(Terminal), "InitTerminal");
            if (initTerminal == null)
                Log.LogWarning("[LagProbe] Terminal.InitTerminal not found - 'ss.lag' will not be available");
            else
                Harmony.Patch(initTerminal, postfix: new HarmonyMethod(typeof(LagProbeModule), nameof(TerminalInitPostfix)));

            Reset();
            Active = true;
            Log.LogInfo("[LagProbe] " + (_serverHalf ? "server" : "client") + " half: ping every " +
                        PingIntervalSec.ToString("F1") + "s, summary every " + SummaryIntervalSec.ToString("F0") +
                        "s" + (_serverHalf ? "" : ", hit log above " + HitLogThresholdMs + "ms") +
                        " ('ss.lag' prints it on demand)");
        }

        private void PatchDamage(Type owner, string postfix)
        {
            var m = AccessTools.Method(owner, "Damage", new[] { typeof(HitData) });
            if (m == null) throw new Exception(owner.Name + ".Damage(HitData) not found");
            Harmony.Patch(m, postfix: new HarmonyMethod(typeof(LagProbeModule), postfix));
        }

        private void ReadConfig()
        {
            PingIntervalSec = Mathf.Clamp(_pingInterval.Value, 1f, 60f);
            SummaryIntervalSec = Mathf.Clamp(_summaryInterval.Value, 5f, 3600f);
            HitLogThresholdMs = Mathf.Clamp(_hitThreshold.Value, 0, 10000);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[LagProbe] pingInterval=" + PingIntervalSec.ToString("F1") + "s summary=" +
                        SummaryIntervalSec.ToString("F0") + "s hitThreshold=" + HitLogThresholdMs + "ms");
        }

        public override void Disable()
        {
            Active = false;
            Reset();
            base.Disable();
        }

        public override string StatusDetail()
        {
            return _serverHalf ? "peersTracked=" + Peers.Count : "pendingHits=" + Pending.Count;
        }

        private static void Reset()
        {
            Peers.Clear();
            Pending.Clear();
            Samples.Clear();
            ByOwner.Clear();
            _hitLate = 0; _hitTimeouts = 0; _churn = 0;
            _pingAcc = 0f; _summaryAcc = 0f;
        }

        // ---- RPC plumbing ------------------------------------------------------------------

        private static void RoutedRpcCtorPostfix()
        {
            // A fresh ZRoutedRpc is a fresh session: every peer and every outstanding ping is gone.
            Peers.Clear();
            _rpcsRegistered = false;
            TryRegisterRpcs();
        }

        private static void TryRegisterRpcs()
        {
            if (_rpcsRegistered) return;
            var rrpc = ZRoutedRpc.instance;
            if (rrpc == null) return;
            try
            {
                rrpc.Register<int, long>(RpcPing, OnPing);
                rrpc.Register<int, long, float>(RpcPong, OnPong);
                _rpcsRegistered = true;
                Log.LogInfo("[LagProbe] routed RPCs '" + RpcPing + "' / '" + RpcPong + "' registered");
            }
            catch (Exception e)
            {
                Log.LogWarning("[LagProbe] could not register routed RPCs: " + e.Message);
                _rpcsRegistered = true; // do not spin
            }
        }

        private static long NowMs()
        {
            return (long)(Time.realtimeSinceStartup * 1000f);
        }

        /// <summary>Client side: answer the server's probe, carrying our own frame time back.</summary>
        private static void OnPing(long sender, int seq, long serverTimeMs)
        {
            if (!Active) return;
            try
            {
                var rrpc = ZRoutedRpc.instance;
                if (rrpc == null) return;
                rrpc.InvokeRoutedRPC(sender, RpcPong, seq, serverTimeMs, _frameMsEma);
            }
            catch (Exception e) { WarnOnce("pong-send", e); }
        }

        /// <summary>Server side: the round trip, measured entirely on the server's own clock.</summary>
        private static void OnPong(long sender, int seq, long serverTimeMs, float clientFrameMs)
        {
            if (!Active) return;
            try
            {
                PeerLag p;
                if (!Peers.TryGetValue(sender, out p)) return;
                if (!p.Outstanding.Remove(seq)) return;   // late, or already written off as lost

                float rtt = NowMs() - serverTimeMs;
                if (rtt < 0f) rtt = 0f;

                if (p.HaveRtt)
                {
                    p.JitterMs = p.JitterMs * 0.8f + Mathf.Abs(rtt - p.RttMs) * 0.2f;
                    p.EmaMs = p.EmaMs * 0.8f + rtt * 0.2f;
                }
                else
                {
                    p.JitterMs = 0f;
                    p.EmaMs = rtt;
                    p.HaveRtt = true;
                }
                p.RttMs = rtt;
                p.ClientFrameMs = clientFrameMs;
                p.Answered++;
            }
            catch (Exception e) { WarnOnce("pong-recv", e); }
        }

        // ---- tick ---------------------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active) return;
            if (ZNet.instance == null) return;

            try
            {
                if (!_rpcsRegistered) TryRegisterRpcs();

                if (_serverHalf)
                {
                    if (!ServerActive()) return;
                    _pingAcc += dt;
                    if (_pingAcc >= PingIntervalSec) { _pingAcc = 0f; SendPings(); }
                }
                else
                {
                    if (!ClientActive()) return;
                    _frameMsEma = _frameMsEma * 0.95f + dt * 1000f * 0.05f;
                    ResolveHits();
                }

                _summaryAcc += dt;
                if (_summaryAcc >= SummaryIntervalSec)
                {
                    _summaryAcc = 0f;
                    foreach (var line in BuildSummary()) SmoothServerPlugin.Log.LogInfo(line);
                    ResetInterval();
                }
            }
            catch (Exception e) { WarnOnce("tick", e); }
        }

        // ---- (a) server -> peer round trip ---------------------------------------------------

        private static void SendPings()
        {
            var net = ZNet.instance;
            var rrpc = ZRoutedRpc.instance;
            if (net == null || rrpc == null) return;

            long now = NowMs();
            float nowSec = Time.realtimeSinceStartup;
            float writeOff = Mathf.Max(PingIntervalSec * 2f, 10f);

            TempUids.Clear();
            var peers = net.GetConnectedPeers();
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null || !peer.IsReady()) continue;

                PeerLag p;
                if (!Peers.TryGetValue(peer.m_uid, out p))
                {
                    p = new PeerLag();
                    Peers[peer.m_uid] = p;
                }
                p.Name = string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
                TempUids.Add(peer.m_uid);

                // Anything still outstanding well past its due date is lost, not slow.
                TempSeqs.Clear();
                foreach (var kv in p.Outstanding)
                    if (nowSec - kv.Value > writeOff) TempSeqs.Add(kv.Key);
                for (int s = 0; s < TempSeqs.Count; s++) { p.Outstanding.Remove(TempSeqs[s]); p.Lost++; }

                p.Seq++;
                p.Outstanding[p.Seq] = nowSec;
                p.Sent++;
                try { rrpc.InvokeRoutedRPC(peer.m_uid, RpcPing, p.Seq, now); }
                catch (Exception e) { p.Outstanding.Remove(p.Seq); WarnOnce("ping-send", e); }
            }

            // forget peers that left
            if (Peers.Count != TempUids.Count)
            {
                var stale = new List<long>();
                foreach (var kv in Peers) if (!TempUids.Contains(kv.Key)) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) Peers.Remove(stale[i]);
            }
        }

        /// <summary>The bit PeerTelemetry appends to its per-peer line. "" when we have nothing.</summary>
        internal static string PeerSuffix(long uid)
        {
            if (!Active || !_serverHalf) return "";
            PeerLag p;
            if (!Peers.TryGetValue(uid, out p) || !p.HaveRtt) return "";
            return string.Format(" rtt={0:F0}ms jit={1:F0}ms loss={2:F0}% cfps={3:F0}",
                p.EmaMs, p.JitterMs, LossPct(p), p.ClientFrameMs > 0.01f ? 1000f / p.ClientFrameMs : 0f);
        }

        /// <summary>StatsLog: this peer's probe numbers. False when there is no round trip yet.</summary>
        internal static bool TryGetPeer(long uid, out float rttMs, out float jitterMs,
                                        out float lossPct, out float clientFrameMs)
        {
            rttMs = 0f; jitterMs = 0f; lossPct = 0f; clientFrameMs = 0f;
            PeerLag p;
            if (!Active || !_serverHalf || !Peers.TryGetValue(uid, out p) || !p.HaveRtt) return false;
            rttMs = p.EmaMs; jitterMs = p.JitterMs; lossPct = LossPct(p); clientFrameMs = p.ClientFrameMs;
            return true;
        }

        private static float LossPct(PeerLag p)
        {
            int accounted = p.Answered + p.Lost;
            return accounted <= 0 ? 0f : 100f * p.Lost / accounted;
        }

        // ---- (b) hit-registration latency (client) --------------------------------------------

        private static void CharacterDamagePostfix(Character __instance)
        {
            NoteHit(__instance != null ? __instance.m_nview : null,
                    __instance != null ? __instance.gameObject : null);
        }

        private static void DestructibleDamagePostfix(Destructible __instance)
        {
            NoteHit(__instance != null ? __instance.m_nview : null,
                    __instance != null ? __instance.gameObject : null);
        }

        private static void WearNTearDamagePostfix(WearNTear __instance)
        {
            NoteHit(__instance != null ? __instance.m_nview : null,
                    __instance != null ? __instance.gameObject : null);
        }

        private static void NoteHit(ZNetView nview, GameObject go)
        {
            if (!Active || _serverHalf) return;
            try
            {
                if (nview == null || !nview.IsValid()) return;
                var zdo = nview.GetZDO();
                if (zdo == null) return;

                long owner = zdo.GetOwner();
                // We own it (or nobody does): vanilla applies the hit locally, nothing to measure.
                if (owner == 0L || owner == ZDOMan.GetSessionID()) return;

                if (Pending.Count >= MaxPendingHits) return;
                var id = zdo.m_uid;
                if (Pending.ContainsKey(id)) return;      // already timing this object

                Pending[id] = new PendingHit
                {
                    Owner = owner,
                    Prefab = go != null ? go.name : "?",
                    SentAt = Time.realtimeSinceStartup,
                    Rev = zdo.DataRevision
                };
            }
            catch (Exception e) { WarnOnce("hit-hook", e); }
        }

        /// <summary>
        /// Watch each pending hit's ZDO for the owner's authoritative answer: any data-revision
        /// bump (health, state, destruction) or the ZDO going away.
        /// </summary>
        private static void ResolveHits()
        {
            if (Pending.Count == 0) return;
            var zm = ZDOMan.instance;
            if (zm == null) { Pending.Clear(); return; }

            float now = Time.realtimeSinceStartup;
            TempIds.Clear();

            foreach (var kv in Pending)
            {
                var id = kv.Key;
                var p = kv.Value;
                float age = now - p.SentAt;

                ZDO zdo = null;
                try { zdo = zm.GetZDO(id); } catch { }

                if (zdo == null || zdo.DataRevision != p.Rev)
                {
                    RecordHit(p, age * 1000f);
                    TempIds.Add(id);
                }
                else if (age > HitTimeoutSec)
                {
                    _hitTimeouts++;
                    TempIds.Add(id);
                }
            }

            for (int i = 0; i < TempIds.Count; i++) Pending.Remove(TempIds[i]);
        }

        private static void RecordHit(PendingHit p, float ms)
        {
            if (Samples.Count < MaxSamples) Samples.Add(ms);

            OwnerStat os;
            ByOwner.TryGetValue(p.Owner, out os);
            os.Count++;
            os.TotalMs += ms;
            if (ms > os.MaxMs) os.MaxMs = ms;
            ByOwner[p.Owner] = os;

            if (ms >= HitLogThresholdMs)
            {
                _hitLate++;
                SmoothServerPlugin.Log.LogInfo(string.Format(
                    "[LagProbe] hit on {0} owned by {1} registered after {2:F0} ms",
                    Clean(p.Prefab), OwnerName(p.Owner), ms));
            }
        }

        // ---- (c) ownership churn (client) ------------------------------------------------------

        private static void SetOwnerPostfix(ZDO __instance)
        {
            if (!Active || _serverHalf) return;
            var player = Player.m_localPlayer;
            if (player == null) return;
            try
            {
                var d = __instance.GetPosition() - player.transform.position;
                if (d.sqrMagnitude <= ChurnRadiusSqr) _churn++;
            }
            catch (Exception e) { WarnOnce("churn-hook", e); }
        }

        // ---- summary ----------------------------------------------------------------------------

        /// <summary>The current summary, one string per line. Used by the tick and by `ss.lag`.</summary>
        internal static List<string> BuildSummary()
        {
            var outLines = new List<string>();
            float window = Mathf.Max(1f, SummaryIntervalSec);

            if (_serverHalf)
            {
                outLines.Add("[LagProbe] server " + window.ToString("F0") + "s: peers=" + Peers.Count);
                foreach (var kv in Peers)
                {
                    var p = kv.Value;
                    int queue = -1;
                    PeerTelemetryModule.PeerStat s;
                    if (PeerTelemetryModule.TryGet(kv.Key, out s)) queue = s.SocketQueueBytes;
                    int deferrals = Mathf.Max(0, DeferralsFor(kv.Key) - p.DeferralsSeen);

                    outLines.Add(string.Format(
                        "[LagProbe]   '{0}' uid={1} rtt={2:F0}ms (ema {3:F0}ms) jit={4:F0}ms loss={5:F0}% " +
                        "({6}/{7} answered) socketQueue={8}B deferrals={9} clientFrame={10:F1}ms",
                        p.Name, kv.Key, p.RttMs, p.EmaMs, p.JitterMs, LossPct(p),
                        p.Answered, p.Sent, queue, deferrals, p.ClientFrameMs));
                }
                if (Peers.Count == 0)
                    outLines.Add("[LagProbe]   no players connected - nothing to probe");
                return outLines;
            }

            // client
            Sorted.Clear();
            Sorted.AddRange(Samples);
            Sorted.Sort();
            int n = Sorted.Count;
            outLines.Add(string.Format(
                "[LagProbe] client {0:F0}s: hits={1} p50={2:F0}ms p95={3:F0}ms max={4:F0}ms late={5}(>{6}ms) " +
                "timeouts={7} ownerChurn={8}/min pending={9}",
                window, n, Pct(Sorted, 50), Pct(Sorted, 95), n == 0 ? 0f : Sorted[n - 1],
                _hitLate, HitLogThresholdMs, _hitTimeouts, _churn * 60f / window, Pending.Count));

            if (ByOwner.Count > 0)
            {
                foreach (var kv in ByOwner)
                {
                    var o = kv.Value;
                    outLines.Add(string.Format("[LagProbe]   owner {0}: n={1} avg={2:F0}ms max={3:F0}ms",
                        OwnerName(kv.Key), o.Count, o.Count > 0 ? o.TotalMs / o.Count : 0f, o.MaxMs));
                }
            }
            return outLines;
        }

        /// <summary>
        /// SendQueueGuard's deferral count for this peer's real socket - which means resolving the
        /// peer's ISocket through any decorator in front of it (the very thing that made
        /// PeerTelemetry read zeros through 0.5.0).
        /// </summary>
        private static int DeferralsFor(long uid)
        {
            try
            {
                var net = ZNet.instance;
                if (net == null) return 0;
                var peer = net.GetPeer(uid);
                if (peer == null) return 0;
                return SendQueueGuardModule.DeferralsFor(PeerTelemetryModule.ResolveSteamSocket(peer.m_socket));
            }
            catch { return 0; }
        }

        private static void ResetInterval()
        {
            if (_serverHalf)
            {
                foreach (var kv in Peers)
                {
                    kv.Value.Sent = 0;
                    kv.Value.Answered = 0;
                    kv.Value.Lost = 0;
                    kv.Value.DeferralsSeen = DeferralsFor(kv.Key);
                }
                return;
            }
            Samples.Clear();
            ByOwner.Clear();
            _hitLate = 0;
            _hitTimeouts = 0;
            _churn = 0;
        }

        private static float Pct(List<float> sorted, int pct)
        {
            if (sorted.Count == 0) return 0f;
            int i = Mathf.Clamp(Mathf.RoundToInt((sorted.Count - 1) * pct / 100f), 0, sorted.Count - 1);
            return sorted[i];
        }

        /// <summary>uid -> player name where ZNet still knows the peer; the uid otherwise.</summary>
        private static string OwnerName(long uid)
        {
            try
            {
                var net = ZNet.instance;
                if (net != null)
                {
                    var peer = net.GetPeer(uid);
                    if (peer != null && !string.IsNullOrEmpty(peer.m_playerName)) return "'" + peer.m_playerName + "'";
                    if (uid == ZDOMan.GetSessionID()) return "'the server'";
                }
            }
            catch { }
            return "uid " + uid;
        }

        private static string Clean(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "?";
            int i = prefab.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? prefab.Substring(0, i) : prefab;
        }

        private static void WarnOnce(string key, Exception e)
        {
            if (!WarnedOnce.Add(key)) return;
            SmoothServerPlugin.Log.LogWarning("[LagProbe] " + key + " failed (reported once): " + e.Message);
        }

        // ---- console command --------------------------------------------------------------------

        private static void TerminalInitPostfix()
        {
            if (_cmdRegistered) return;
            _cmdRegistered = true;
            try
            {
                new Terminal.ConsoleCommand("ss.lag",
                    "SmoothServer: print the current LagProbe summary (rtt/jitter/loss per player on a " +
                    "server; hit-registration latency and ownership churn on a client)",
                    (Terminal.ConsoleEvent)LagCommand);
                SmoothServerPlugin.Log.LogInfo("[LagProbe] console command 'ss.lag' registered");
            }
            catch (Exception e)
            {
                SmoothServerPlugin.Log.LogWarning("[LagProbe] could not register 'ss.lag': " + e.Message);
            }
        }

        private static void LagCommand(Terminal.ConsoleEventArgs args)
        {
            try
            {
                if (!Active)
                {
                    Emit(args, "[LagProbe] module is not active on this side.");
                    return;
                }
                var lines = BuildSummary();
                for (int i = 0; i < lines.Count; i++) Emit(args, lines[i]);
            }
            catch (Exception e)
            {
                Emit(args, "[LagProbe] ss.lag failed: " + e.Message);
            }
        }

        private static void Emit(Terminal.ConsoleEventArgs args, string line)
        {
            if (args != null && args.Context != null) args.Context.AddString(line);
            else SmoothServerPlugin.Log.LogInfo(line);
        }
    }
}
