using System;
using System.Collections.Generic;
using System.Threading;
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
    /// Five probes, three measured locally and two that travel:
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
    /// <b>Session edges (0.5.2).</b> The first real data was almost all artifact: multi-second
    /// "RTT" only ever at the two moments a client's main thread is not answering anything -
    /// world load on join, and save/quit on leave. Those are not network latency. Every probe
    /// sent within <c>EdgeIgnoreSec</c> of a peer becoming ready, or at any point after the
    /// server knows the peer is going away, is dropped whole: it never touches RTT/EMA/jitter and
    /// an unanswered one is never charged as loss. The per-peer summary line carries the peer's
    /// session age (<c>age=Ns</c>) so a number can be read in context, and a peer that has never
    /// answered a single post-edge ping prints <c>client-mod=none</c> instead of <c>loss=100%</c>
    /// - an old or absent client half is not packet loss.
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
    /// <b>(d) ZDO churn by prefab (server, 0.5.2).</b> "140 kB/s and 800 ZDO updates/s to each
    /// player near the base" is a symptom; this says <i>what</i>. A prefix/finalizer pair on
    /// <c>ZDOMan.SendZDOs(ZDOPeer, bool)</c> - the single funnel every outgoing ZDOData package
    /// goes through, vanilla's round robin and SendCadence's own sweep alike, so there is no way
    /// to double count - opens a window in which a postfix on <c>ZDO.Serialize(ZPackage)</c>
    /// counts one update against the ZDO's prefab hash and adds the bytes it just wrote (plus
    /// vanilla's fixed 42-byte per-ZDO header). Off the send path that postfix is a single bool
    /// read, so the autosave's own serialisation costs nothing; a background thread is ignored
    /// outright. Printed every <c>SummaryIntervalSec</c> as a top-<c>ChurnTopN</c> table, put in
    /// StatsLog's record as <c>zdoChurnTop</c>, and available on demand as <c>ss.lag churn</c>.
    ///
    /// <b>(e) Client hit reports (0.5.2).</b> The hit histogram is measured on the client, where
    /// nobody is reading logs. Every <c>SummaryIntervalSec</c> the client half sends its own
    /// summary to the server as the routed RPC <c>SS_LagReport(ZPackage)</c>; the server logs one
    /// line per client per interval and writes the same fields into StatsLog's per-peer record,
    /// so the whole crew's hit latency lands on the box without anyone sending a log file. A
    /// client with no mod, or an older one, simply never sends - nothing changes for it.
    ///
    /// Overhead: no allocation in any patch body (a struct into a pre-grown dictionary), no
    /// per-ZDO work in any loop that vanilla runs per frame beyond one dictionary update,
    /// timestamps from <c>Time.realtimeSinceStartup</c>, every body wrapped in try/catch with
    /// one-time warnings. The client-only patches are not installed at all on a dedicated server,
    /// and the churn patches are not installed at all on a client.
    ///
    /// <c>ss.lag</c> in the console (client or server) prints the current summary on demand;
    /// <c>ss.lag churn</c> / <c>ss.lag churn reset</c> print and clear the prefab table.
    /// </summary>
    internal sealed class LagProbeModule : FeatureModule
    {
        public override string Name => "LagProbe";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "LagProbe";

        protected override string EnabledDescription =>
            "Measure lag while people play: server->player ping/jitter/loss, client hit-registration " +
            "latency, ZDO ownership churn, and which prefabs generate the outgoing ZDO traffic. " +
            "Diagnostics only - it never changes gameplay. Cheap enough to leave on.";

        private const string RpcPing = "SS_Ping";
        private const string RpcPong = "SS_Pong";
        private const string RpcLagReport = "SS_LagReport";

        /// <summary>Wire version of the SS_LagReport package.</summary>
        private const int ReportVersion = 2;          // 2 (0.5.4): + local-apply count after hits
        private const int OldestReportVersion = 1;    // still parsed: 0.5.1-0.5.3 clients
        /// <summary>A report bigger than this is not one of ours; drop it unread.</summary>
        private const int MaxReportBytes = 4096;
        /// <summary>Owners carried in a report, both sides.</summary>
        private const int MaxReportOwners = 8;

        /// <summary>How long a hit may stay unanswered before it counts as a timeout.</summary>
        private const float HitTimeoutSec = 3f;
        /// <summary>Ownership transfers are counted within this radius of the local player.</summary>
        private const float ChurnRadius = 30f;
        private const float ChurnRadiusSqr = ChurnRadius * ChurnRadius;
        /// <summary>Hard caps so a stall can never turn the probe into the problem.</summary>
        private const int MaxPendingHits = 256;
        private const int MaxSamples = 512;
        private const int MaxFrameSamples = 1024;
        private const float FrameSampleIntervalSec = 0.2f;

        /// <summary>
        /// What vanilla writes per ZDO in the ZDOData package around the serialised body:
        /// ZDOID (12) + OwnerRevision ushort (2) + DataRevision uint (4) + owner long (8) +
        /// position Vector3 (12) + the length prefix of the embedded package (4).
        /// </summary>
        private const int ZdoHeaderBytes = 42;

        private ConfigEntry<float> _pingInterval;
        private ConfigEntry<float> _summaryInterval;
        private ConfigEntry<int> _hitThreshold;
        private ConfigEntry<float> _edgeIgnore;
        private ConfigEntry<bool> _churnEnabled;
        private ConfigEntry<int> _churnTopN;

        internal static bool Active;
        internal static float PingIntervalSec = 5f;
        internal static float SummaryIntervalSec = 60f;
        internal static int HitLogThresholdMs = 250;
        internal static float EdgeIgnoreSec = 30f;
        internal static bool ChurnEnabled = true;
        internal static int ChurnTopN = 10;

        private static bool _serverHalf;
        private static bool _rpcsRegistered;
        private static bool _cmdRegistered;
        private static int _mainThreadId;
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
            public int Sent;            // pings sent this interval (edge ones excluded)
            public int Answered;        // pongs received this interval (edge ones excluded)
            public int Lost;            // pings that timed out this interval (edge ones excluded)
            public int EdgeDropped;     // probes dropped by the edge filter this interval
            public int DeferralsSeen;   // SendQueueGuard deferrals at the last summary
            public bool HaveRtt;
            public float ReadyAt;       // realtime when we first saw this peer ready
            public float DisconnectAt;  // realtime the server learned it is going away, 0 = no
            public int LifetimeSent;    // post-edge pings ever sent to this peer
            public int LifetimeAnswered;// post-edge pongs ever received from it
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
        private static readonly List<float> FrameSamples = new List<float>(MaxFrameSamples);
        private static readonly List<float> FrameSorted = new List<float>(MaxFrameSamples);
        private static readonly Dictionary<long, OwnerStat> ByOwner = new Dictionary<long, OwnerStat>();
        private static int _hitLate;
        /// <summary>Swings on objects this client OWNS: vanilla applies them in the same frame, 0 ms. Counted, never timed,
        /// so that once CombatOwnership claims on hit the histogram does not "improve" by simply losing its samples.</summary>
        private static int _hitsLocal;
        private static int _hitTimeouts;
        private static int _churn;
        private static float _frameMsEma = 16.7f;
        private static float _frameSampleAcc;

        private static float _summaryAcc;

        // ---- (d) ZDO churn by prefab (server) -----------------------------------------------

        private struct ChurnStat
        {
            public long Count;
            public long Bytes;
        }

        /// <summary>One row of the churn table, as handed to StatsLog and the console.</summary>
        internal struct ChurnEntry
        {
            public string Prefab;
            public long Count;
            public long Bytes;
        }

        private static readonly Dictionary<int, ChurnStat> Churn = new Dictionary<int, ChurnStat>();
        private static readonly Dictionary<int, string> PrefabNames = new Dictionary<int, string>();
        private static readonly HashSet<long> ChurnPeers = new HashSet<long>();
        private static readonly List<KeyValuePair<int, ChurnStat>> ChurnSort =
            new List<KeyValuePair<int, ChurnStat>>();
        private static readonly Comparison<KeyValuePair<int, ChurnStat>> ByCountDesc =
            (a, b) => b.Value.Count.CompareTo(a.Value.Count);
        private static long _churnTotal;
        private static long _churnBytes;
        /// <summary>Set for exactly the duration of one ZDOMan.SendZDOs call on the main thread.</summary>
        private static bool _inSendZDOs;

        // ---- (e) client hit reports (server side store) -------------------------------------

        internal struct OwnerReport
        {
            public long Uid;
            public int N;
            public float AvgMs;
            public float MaxMs;
        }

        /// <summary>The last SS_LagReport a client sent us. Read by StatsLog and `ss.lag`.</summary>
        internal sealed class ClientReport
        {
            public long Uid;
            public float WindowSec;
            public int Hits;
            public float P50Ms;
            public float P95Ms;
            public float MaxMs;
            public int Late;
            public int Timeouts;
            public int Local;
            public float ChurnPerMin;
            public float FrameMs;
            public float ReceivedAt;
            public readonly List<OwnerReport> Owners = new List<OwnerReport>(MaxReportOwners);
        }

        private static readonly Dictionary<long, ClientReport> Reports = new Dictionary<long, ClientReport>();

        // ---- config ------------------------------------------------------------------------

        protected override void Bind()
        {
            _pingInterval = BindSynced("PingIntervalSec", 5f,
                "Seconds between the server's SS_Ping round-trip probes to each player. Server-driven, " +
                "so it is synced. One tiny routed RPC each way per player per interval.");
            _summaryInterval = BindLocal("SummaryIntervalSec", 60f,
                "Seconds between LagProbe summary lines (server: per-peer rtt/jitter/loss plus the ZDO " +
                "churn table; client: the hit-latency histogram and ownership churn, which the client " +
                "also sends to the server as SS_LagReport). Machine-local.");
            _hitThreshold = BindLocal("HitLogThresholdMs", 250,
                "Log an individual line for any hit on an object owned by someone else that took longer " +
                "than this to register. Machine-local; the histogram counts every hit regardless.");
            _edgeIgnore = BindLocal("EdgeIgnoreSec", 30f,
                "Ignore ping results for this many seconds after a player becomes ready, and from the " +
                "moment the server sees the player leaving. A client loading the world or saving on quit " +
                "does not answer anything for seconds at a time; that is not latency and not packet loss, " +
                "so probes in those windows are dropped whole (no rtt, no jitter, no loss). Machine-local.");
            _churnEnabled = BindLocal("ChurnEnabled", true,
                "Count outgoing ZDO updates per prefab on the server and print a top-N table every " +
                "SummaryIntervalSec ('what generates the traffic?'). One dictionary update per ZDO sent. " +
                "Machine-local; server side only.");
            _churnTopN = BindLocal("ChurnTopN", 10,
                "How many prefabs the ZDO churn summary line and StatsLog's zdoChurnTop array carry. " +
                "'ss.lag churn' always prints 25. Machine-local.");
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            _serverHalf = SmoothServerPlugin.IsServerSide;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var rrpcCtor = AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            if (rrpcCtor == null) throw new Exception("ZRoutedRpc(bool) constructor not found");
            Harmony.Patch(rrpcCtor, postfix: new HarmonyMethod(typeof(LagProbeModule), nameof(RoutedRpcCtorPostfix)));

            if (_serverHalf)
            {
                PatchChurn();

                // The server's only warning that a session is ending: vanilla removes the peer
                // inside this call, so the prefix is where it is still identifiable.
                var disconnect = AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) });
                if (disconnect == null)
                    Log.LogWarning("[LagProbe] ZNet.Disconnect(ZNetPeer) not found - the leave-edge filter is off");
                else
                    Harmony.Patch(disconnect, prefix: new HarmonyMethod(typeof(LagProbeModule), nameof(DisconnectPrefix)));
            }
            else
            {
                PatchDamage(typeof(Character), nameof(CharacterDamagePostfix));
                PatchDamage(typeof(Destructible), nameof(DestructibleDamagePostfix));
                PatchDamage(typeof(WearNTear), nameof(WearNTearDamagePostfix));
                PatchDamage(typeof(MineRock5), nameof(MineRock5DamagePostfix));
                PatchDamage(typeof(MineRock), nameof(MineRockDamagePostfix));
                PatchDamage(typeof(TreeBase), nameof(TreeBaseDamagePostfix));
                PatchDamage(typeof(TreeLog), nameof(TreeLogDamagePostfix));

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
                        "s, edge filter " + EdgeIgnoreSec.ToString("F0") + "s" +
                        (_serverHalf
                            ? ", zdo churn " + (ChurnEnabled ? "on (top " + ChurnTopN + ")" : "off")
                            : ", hit log above " + HitLogThresholdMs + "ms, reporting to the server") +
                        " ('ss.lag' prints it on demand)");
        }

        /// <summary>
        /// The churn window. <c>ZDOMan.SendZDOs</c> is the one place a ZDO is written into an
        /// outgoing ZDOData package, and it is a real method: vanilla's round robin calls it,
        /// SendAllZDOs loops it, and SendCadence's own prefix calls it directly - all three go
        /// through this patch exactly once per call, so there is nothing to double count.
        /// </summary>
        private void PatchChurn()
        {
            var sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs",
                new[] { typeof(ZDOMan.ZDOPeer), typeof(bool) });
            if (sendZdos == null)
            {
                Log.LogWarning("[LagProbe] ZDOMan.SendZDOs(ZDOPeer, bool) not found - zdo churn is off");
                return;
            }
            var serialize = AccessTools.Method(typeof(ZDO), "Serialize", new[] { typeof(ZPackage) });
            if (serialize == null)
            {
                Log.LogWarning("[LagProbe] ZDO.Serialize(ZPackage) not found - zdo churn is off");
                return;
            }

            Harmony.Patch(sendZdos,
                prefix: new HarmonyMethod(typeof(LagProbeModule), nameof(SendZdosPrefix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(LagProbeModule), nameof(SendZdosFinalizer)));
            Harmony.Patch(serialize,
                postfix: new HarmonyMethod(typeof(LagProbeModule), nameof(SerializePostfix)));
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
            EdgeIgnoreSec = Mathf.Clamp(_edgeIgnore.Value, 0f, 600f);
            ChurnEnabled = _churnEnabled.Value;
            ChurnTopN = Mathf.Clamp(_churnTopN.Value, 1, 50);
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            Log.LogInfo("[LagProbe] pingInterval=" + PingIntervalSec.ToString("F1") + "s summary=" +
                        SummaryIntervalSec.ToString("F0") + "s hitThreshold=" + HitLogThresholdMs +
                        "ms edgeIgnore=" + EdgeIgnoreSec.ToString("F0") + "s churn=" +
                        (ChurnEnabled ? "on" : "off") + " top" + ChurnTopN);
        }

        public override void Disable()
        {
            Active = false;
            Reset();
            base.Disable();
        }

        public override string StatusDetail()
        {
            return _serverHalf
                ? "peersTracked=" + Peers.Count + " churnPrefabs=" + Churn.Count
                : "pendingHits=" + Pending.Count;
        }

        private static void Reset()
        {
            Peers.Clear();
            Reports.Clear();
            Pending.Clear();
            Samples.Clear();
            FrameSamples.Clear();
            ByOwner.Clear();
            ResetChurn();
            _hitLate = 0; _hitTimeouts = 0; _hitsLocal = 0; _churn = 0;
            _pingAcc = 0f; _summaryAcc = 0f; _frameSampleAcc = 0f;
        }

        private static void ResetChurn()
        {
            Churn.Clear();
            ChurnPeers.Clear();
            _churnTotal = 0L;
            _churnBytes = 0L;
        }

        // ---- RPC plumbing ------------------------------------------------------------------

        private static void RoutedRpcCtorPostfix()
        {
            // A fresh ZRoutedRpc is a fresh session: every peer and every outstanding ping is gone.
            Peers.Clear();
            Reports.Clear();
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
                rrpc.Register<ZPackage>(RpcLagReport, OnLagReport);
                _rpcsRegistered = true;
                Log.LogInfo("[LagProbe] routed RPCs '" + RpcPing + "' / '" + RpcPong + "' / '" +
                            RpcLagReport + "' registered");
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
                float sentAt;
                if (!p.Outstanding.TryGetValue(seq, out sentAt)) return; // late, or written off
                p.Outstanding.Remove(seq);

                // Join/leave edge: the client's main thread was not answering anything. Not latency.
                if (IsEdgeProbe(p, sentAt)) { p.EdgeDropped++; return; }

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
                p.LifetimeAnswered++;
            }
            catch (Exception e) { WarnOnce("pong-recv", e); }
        }

        /// <summary>
        /// True for a probe sent while this peer was joining, or at any time once the server knows
        /// the peer is leaving. Such a probe is dropped whole - no RTT, no jitter, and if it is
        /// never answered it is not loss either.
        /// </summary>
        private static bool IsEdgeProbe(PeerLag p, float sentAtSec)
        {
            if (p.DisconnectAt > 0f) return true;
            return sentAtSec - p.ReadyAt < EdgeIgnoreSec;
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
                    _frameSampleAcc += dt;
                    if (_frameSampleAcc >= FrameSampleIntervalSec)
                    {
                        _frameSampleAcc = 0f;
                        if (FrameSamples.Count < MaxFrameSamples) FrameSamples.Add(_frameMsEma);
                    }
                    ResolveHits();
                }

                _summaryAcc += dt;
                if (_summaryAcc >= SummaryIntervalSec)
                {
                    _summaryAcc = 0f;
                    foreach (var line in BuildSummary()) SmoothServerPlugin.Log.LogInfo(line);
                    if (!_serverHalf) SendLagReport();
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
                    p = new PeerLag { ReadyAt = nowSec };
                    Peers[peer.m_uid] = p;
                }
                p.Name = string.IsNullOrEmpty(peer.m_playerName) ? peer.m_uid.ToString() : peer.m_playerName;
                TempUids.Add(peer.m_uid);

                // A socket already on its way down is a leave edge even if Disconnect has not run.
                if (p.DisconnectAt <= 0f)
                {
                    bool up = true;
                    try { up = peer.m_socket != null && peer.m_socket.IsConnected(); } catch { }
                    if (!up) p.DisconnectAt = nowSec;
                }

                // Anything still outstanding well past its due date is lost, not slow - unless it
                // was an edge probe, which is dropped without being charged to anyone.
                TempSeqs.Clear();
                foreach (var kv in p.Outstanding)
                    if (nowSec - kv.Value > writeOff) TempSeqs.Add(kv.Key);
                for (int s = 0; s < TempSeqs.Count; s++)
                {
                    float sentAt;
                    p.Outstanding.TryGetValue(TempSeqs[s], out sentAt);
                    p.Outstanding.Remove(TempSeqs[s]);
                    if (IsEdgeProbe(p, sentAt)) p.EdgeDropped++;
                    else p.Lost++;
                }

                if (p.DisconnectAt > 0f) continue;   // nothing useful to learn from a dying session

                p.Seq++;
                p.Outstanding[p.Seq] = nowSec;
                bool edge = IsEdgeProbe(p, nowSec);
                if (!edge) { p.Sent++; p.LifetimeSent++; }
                try { rrpc.InvokeRoutedRPC(peer.m_uid, RpcPing, p.Seq, now); }
                catch (Exception e)
                {
                    p.Outstanding.Remove(p.Seq);
                    if (!edge) { p.Sent--; p.LifetimeSent--; }
                    WarnOnce("ping-send", e);
                }
            }

            // forget peers that left
            if (Peers.Count != TempUids.Count)
            {
                var stale = new List<long>();
                foreach (var kv in Peers) if (!TempUids.Contains(kv.Key)) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) { Peers.Remove(stale[i]); Reports.Remove(stale[i]); }
            }
        }

        /// <summary>Server: the session is ending - stop believing anything this peer reports.</summary>
        private static void DisconnectPrefix(ZNetPeer peer)
        {
            if (!Active || !_serverHalf || peer == null) return;
            try
            {
                PeerLag p;
                if (Peers.TryGetValue(peer.m_uid, out p) && p.DisconnectAt <= 0f)
                    p.DisconnectAt = Time.realtimeSinceStartup;
            }
            catch (Exception e) { WarnOnce("disconnect-hook", e); }
        }

        /// <summary>The bit PeerTelemetry appends to its per-peer line. "" when we have nothing.</summary>
        internal static string PeerSuffix(long uid)
        {
            if (!Active || !_serverHalf) return "";
            PeerLag p;
            if (!Peers.TryGetValue(uid, out p)) return "";
            if (!p.HaveRtt)
                return NoClientMod(p) ? " client-mod=none" : "";
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

        /// <summary>
        /// Past the join edge, sent real probes, never got one back: this player is not running a
        /// client half that answers SS_Ping. That is not packet loss and must not read as 100%.
        /// </summary>
        private static bool NoClientMod(PeerLag p)
        {
            return p.LifetimeSent > 0 && p.LifetimeAnswered == 0 && !p.HaveRtt;
        }

        private static float SessionAge(PeerLag p)
        {
            return Mathf.Max(0f, Time.realtimeSinceStartup - p.ReadyAt);
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

        // 0.5.4: ore, rocks and trees - the cave complaint was vines AND ore, and mining was never timed.
        // These four keep m_nview private, so the view comes off the GameObject.
        private static void MineRock5DamagePostfix(MineRock5 __instance) { NoteHitOn(__instance); }
        private static void MineRockDamagePostfix(MineRock __instance) { NoteHitOn(__instance); }
        private static void TreeBaseDamagePostfix(TreeBase __instance) { NoteHitOn(__instance); }
        private static void TreeLogDamagePostfix(TreeLog __instance) { NoteHitOn(__instance); }

        private static void NoteHitOn(MonoBehaviour mb)
        {
            if (mb == null) return;
            ZNetView nv = null;
            try { nv = mb.GetComponent<ZNetView>(); } catch { }
            NoteHit(nv, mb.gameObject);
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
                // We own it: vanilla applied the hit in this very call, 0 ms. Count it so the share of
                // local vs remote swings is visible; nothing to time.
                if (owner == ZDOMan.GetSessionID()) { _hitsLocal++; return; }
                // Nobody owns it yet: the RPC goes nowhere useful and vanilla sorts ownership out
                // first. Not a measurement of anything.
                if (owner == 0L) return;

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

        // ---- (d) ZDO churn by prefab (server) ---------------------------------------------------

        private static void SendZdosPrefix(ZDOMan.ZDOPeer peer)
        {
            if (!Active || !ChurnEnabled || !_serverHalf) return;
            _inSendZDOs = true;
            try
            {
                if (peer != null && peer.m_peer != null) ChurnPeers.Add(peer.m_peer.m_uid);
            }
            catch (Exception e) { WarnOnce("churn-peer", e); }
        }

        private static void SendZdosFinalizer()
        {
            _inSendZDOs = false;
        }

        /// <summary>
        /// One outgoing ZDO update. Off the send path this is a single bool read, so the autosave's
        /// own serialisation costs nothing; a worker thread is ignored so an async save can never
        /// race the counters.
        /// </summary>
        private static void SerializePostfix(ZDO __instance, ZPackage pkg)
        {
            if (!_inSendZDOs) return;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;
            try
            {
                int hash = __instance.GetPrefab();
                long bytes = (pkg != null ? pkg.Size() : 0) + ZdoHeaderBytes;

                ChurnStat c;
                if (Churn.TryGetValue(hash, out c))
                {
                    c.Count++; c.Bytes += bytes;
                    Churn[hash] = c;
                }
                else
                {
                    c.Count = 1; c.Bytes = bytes;
                    Churn[hash] = c;
                }
                _churnTotal++;
                _churnBytes += bytes;
            }
            catch (Exception e) { WarnOnce("churn-count", e); }
        }

        /// <summary>The churn table, biggest first. False when there is nothing to report.</summary>
        internal static bool TryGetChurn(int topN, List<ChurnEntry> into,
                                         out long total, out long bytes, out int peers)
        {
            total = _churnTotal; bytes = _churnBytes; peers = ChurnPeers.Count;
            if (into != null) into.Clear();
            if (!Active || !_serverHalf || !ChurnEnabled) return false;
            if (into == null || Churn.Count == 0) return true;

            ChurnSort.Clear();
            foreach (var kv in Churn) ChurnSort.Add(kv);
            ChurnSort.Sort(ByCountDesc);

            int n = Mathf.Min(topN, ChurnSort.Count);
            for (int i = 0; i < n; i++)
            {
                var kv = ChurnSort[i];
                into.Add(new ChurnEntry
                {
                    Prefab = PrefabName(kv.Key),
                    Count = kv.Value.Count,
                    Bytes = kv.Value.Bytes
                });
            }
            return true;
        }

        private static string PrefabName(int hash)
        {
            string name;
            if (PrefabNames.TryGetValue(hash, out name)) return name;
            try
            {
                var scene = ZNetScene.instance;
                if (scene != null)
                {
                    var go = scene.GetPrefab(hash);
                    if (go != null && !string.IsNullOrEmpty(go.name))
                    {
                        PrefabNames[hash] = go.name;
                        return go.name;
                    }
                }
            }
            catch { }
            return "#" + hash;   // not cached: the scene may simply not be up yet
        }

        private static string ChurnLine(int topN)
        {
            var rows = new List<ChurnEntry>(topN);
            long total, bytes; int peers;
            TryGetChurn(topN, rows, out total, out bytes, out peers);

            var sb = new System.Text.StringBuilder(160);
            sb.Append("[LagProbe] zdo churn ").Append(Mathf.Max(1f, SummaryIntervalSec).ToString("F0"))
              .Append("s: total=").Append(total).Append(" updates (")
              .Append((bytes / 1024f).ToString("F0")).Append(" kB) to ").Append(peers)
              .Append(" peers; top: ");
            if (rows.Count == 0) { sb.Append("none"); return sb.ToString(); }

            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                float pct = total > 0 ? 100f * rows[i].Count / total : 0f;
                sb.Append(rows[i].Prefab).Append('=').Append(rows[i].Count)
                  .Append(" (").Append(pct.ToString("F0")).Append("%)");
            }
            return sb.ToString();
        }

        // ---- (e) client hit reports -------------------------------------------------------------

        /// <summary>Client: hand this interval's histogram to the server. One small routed RPC.</summary>
        private static void SendLagReport()
        {
            if (!Active || _serverHalf) return;
            try
            {
                var rrpc = ZRoutedRpc.instance;
                var net = ZNet.instance;
                if (rrpc == null || net == null) return;
                if (net.GetServerPeer() == null) return;   // not connected to anyone but ourselves

                float window = Mathf.Max(1f, SummaryIntervalSec);

                Sorted.Clear();
                Sorted.AddRange(Samples);
                Sorted.Sort();
                int n = Sorted.Count;

                FrameSorted.Clear();
                FrameSorted.AddRange(FrameSamples);
                FrameSorted.Sort();

                var pkg = new ZPackage();
                pkg.Write(ReportVersion);
                pkg.Write(window);
                pkg.Write(n);
                pkg.Write(_hitsLocal);            // v2
                pkg.Write(Pct(Sorted, 50));
                pkg.Write(Pct(Sorted, 95));
                pkg.Write(n == 0 ? 0f : Sorted[n - 1]);
                pkg.Write(_hitLate);
                pkg.Write(_hitTimeouts);
                pkg.Write(_churn * 60f / window);
                pkg.Write(Pct(FrameSorted, 50));

                int owners = Mathf.Min(ByOwner.Count, MaxReportOwners);
                pkg.Write(owners);
                int written = 0;
                foreach (var kv in ByOwner)
                {
                    if (written >= owners) break;
                    var o = kv.Value;
                    pkg.Write(kv.Key);
                    pkg.Write(o.Count);
                    pkg.Write(o.Count > 0 ? o.TotalMs / o.Count : 0f);
                    pkg.Write(o.MaxMs);
                    written++;
                }

                rrpc.InvokeRoutedRPC(RpcLagReport, pkg);
            }
            catch (Exception e) { WarnOnce("report-send", e); }
        }

        /// <summary>Server: a client's histogram arrived. One line, and into StatsLog's record.</summary>
        private static void OnLagReport(long sender, ZPackage pkg)
        {
            if (!Active || !_serverHalf) return;
            try
            {
                if (pkg == null) return;
                if (pkg.Size() > MaxReportBytes)
                {
                    WarnOnce("report-size", new Exception("oversized SS_LagReport (" + pkg.Size() + "B) ignored"));
                    return;
                }

                var net = ZNet.instance;
                if (net == null) return;
                var peer = net.GetPeer(sender);
                if (peer == null || !peer.IsReady()) return;   // not a ready peer: ignore

                pkg.SetPos(0);
                int ver = pkg.ReadInt();
                if (ver < OldestReportVersion || ver > ReportVersion) return;

                ClientReport r;
                if (!Reports.TryGetValue(sender, out r)) { r = new ClientReport(); Reports[sender] = r; }
                r.Uid = sender;
                r.WindowSec = pkg.ReadSingle();
                r.Hits = pkg.ReadInt();
                r.Local = ver >= 2 ? pkg.ReadInt() : -1;   // -1 = an older client, unknown
                r.P50Ms = pkg.ReadSingle();
                r.P95Ms = pkg.ReadSingle();
                r.MaxMs = pkg.ReadSingle();
                r.Late = pkg.ReadInt();
                r.Timeouts = pkg.ReadInt();
                r.ChurnPerMin = pkg.ReadSingle();
                r.FrameMs = pkg.ReadSingle();
                r.ReceivedAt = Time.realtimeSinceStartup;

                int owners = Mathf.Clamp(pkg.ReadInt(), 0, MaxReportOwners);
                r.Owners.Clear();
                for (int i = 0; i < owners; i++)
                {
                    r.Owners.Add(new OwnerReport
                    {
                        Uid = pkg.ReadLong(),
                        N = pkg.ReadInt(),
                        AvgMs = pkg.ReadSingle(),
                        MaxMs = pkg.ReadSingle()
                    });
                }

                SmoothServerPlugin.Log.LogInfo("[LagProbe] " + ReportBody(peer.m_playerName, r));
            }
            catch (Exception e) { WarnOnce("report-recv", e); }
        }

        /// <summary>The report line without the tag, so the tag/indent is the caller's choice.</summary>
        private static string ReportBody(string playerName, ClientReport r)
        {
            var sb = new System.Text.StringBuilder(220);
            sb.Append("client '")
              .Append(string.IsNullOrEmpty(playerName) ? r.Uid.ToString() : playerName)
              .Append("' ").Append(r.WindowSec.ToString("F0")).Append("s: ")
              .Append("hits=").Append(r.Hits)
              .Append(" local=").Append(r.Local < 0 ? "?" : r.Local.ToString())
              .Append(" p50=").Append(r.P50Ms.ToString("F0")).Append("ms")
              .Append(" p95=").Append(r.P95Ms.ToString("F0")).Append("ms")
              .Append(" max=").Append(r.MaxMs.ToString("F0")).Append("ms")
              .Append(" late=").Append(r.Late)
              .Append(" timeouts=").Append(r.Timeouts)
              .Append(" churn=").Append(r.ChurnPerMin.ToString("F0")).Append("/min")
              .Append(" cfps=").Append((r.FrameMs > 0.01f ? 1000f / r.FrameMs : 0f).ToString("F0"));

            if (r.Owners.Count > 0)
            {
                sb.Append(" | owners: ");
                for (int i = 0; i < r.Owners.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var o = r.Owners[i];
                    sb.Append(OwnerName(o.Uid)).Append(" n=").Append(o.N)
                      .Append(" avg=").Append(o.AvgMs.ToString("F0")).Append("ms")
                      .Append(" max=").Append(o.MaxMs.ToString("F0")).Append("ms");
                }
            }
            return sb.ToString();
        }

        /// <summary>StatsLog: the last hit report this peer sent, or null.</summary>
        internal static ClientReport GetClientReport(long uid)
        {
            ClientReport r;
            if (!Active || !_serverHalf || !Reports.TryGetValue(uid, out r)) return null;
            return r;
        }

        /// <summary>StatsLog / logs: a player name for an owner uid.</summary>
        internal static string OwnerLabel(long uid)
        {
            try
            {
                var net = ZNet.instance;
                if (net != null)
                {
                    var peer = net.GetPeer(uid);
                    if (peer != null && !string.IsNullOrEmpty(peer.m_playerName)) return peer.m_playerName;
                    if (uid == ZDOMan.GetSessionID()) return "the server";
                }
            }
            catch { }
            return "uid " + uid;
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
                    float age = SessionAge(p);

                    string quality = NoClientMod(p)
                        ? string.Format("client-mod=none ({0} sent, 0 answered)", p.LifetimeSent)
                        : string.Format("rtt={0:F0}ms (ema {1:F0}ms) jit={2:F0}ms loss={3:F0}% ({4}/{5} answered)",
                            p.RttMs, p.EmaMs, p.JitterMs, LossPct(p), p.Answered, p.Sent);

                    string edge = p.DisconnectAt > 0f
                        ? " leaving"
                        : (age < EdgeIgnoreSec
                            ? string.Format(" joining ({0:F0}s of edge window left)", EdgeIgnoreSec - age)
                            : "");

                    outLines.Add(string.Format(
                        "[LagProbe]   '{0}' uid={1} age={2:F0}s {3} socketQueue={4}B deferrals={5} " +
                        "clientFrame={6:F1}ms edgeDropped={7}{8}",
                        p.Name, kv.Key, age, quality, queue, deferrals, p.ClientFrameMs, p.EdgeDropped, edge));

                    var r = GetClientReport(kv.Key);
                    if (r != null)
                        outLines.Add("[LagProbe]   " + ReportBody(p.Name, r) +
                                     " (reported " + (Time.realtimeSinceStartup - r.ReceivedAt).ToString("F0") + "s ago)");
                }
                if (Peers.Count == 0)
                    outLines.Add("[LagProbe]   no players connected - nothing to probe");

                if (ChurnEnabled) outLines.Add(ChurnLine(ChurnTopN));
                return outLines;
            }

            // client
            Sorted.Clear();
            Sorted.AddRange(Samples);
            Sorted.Sort();
            int n = Sorted.Count;
            outLines.Add(string.Format(
                "[LagProbe] client {0:F0}s: hits={1} local={10} p50={2:F0}ms p95={3:F0}ms max={4:F0}ms late={5}(>{6}ms) " +
                "timeouts={7} ownerChurn={8}/min pending={9}",
                window, n, Pct(Sorted, 50), Pct(Sorted, 95), n == 0 ? 0f : Sorted[n - 1],
                _hitLate, HitLogThresholdMs, _hitTimeouts, _churn * 60f / window, Pending.Count, _hitsLocal));

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
                return SendQueueGuardModule.DeferralsFor(SocketResolve.ResolveSteamSocket(peer.m_socket));
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
                    kv.Value.EdgeDropped = 0;
                    kv.Value.DeferralsSeen = DeferralsFor(kv.Key);
                }
                ResetChurn();
                return;
            }
            Samples.Clear();
            FrameSamples.Clear();
            ByOwner.Clear();
            _hitLate = 0;
            _hitTimeouts = 0;
            _hitsLocal = 0;
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
                    "server; hit-registration latency and ownership churn on a client). " +
                    "'ss.lag churn' prints the top 25 prefabs by outgoing ZDO updates; " +
                    "'ss.lag churn reset' clears that table.",
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

                string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
                if (sub == "churn")
                {
                    if (!_serverHalf)
                    {
                        Emit(args, "[LagProbe] zdo churn is measured on the server half only.");
                        return;
                    }
                    if (!ChurnEnabled)
                    {
                        Emit(args, "[LagProbe] zdo churn is off ([LagProbe] ChurnEnabled=false).");
                        return;
                    }
                    if (args.Length > 2 && args[2].ToLowerInvariant() == "reset")
                    {
                        ResetChurn();
                        Emit(args, "[LagProbe] zdo churn counters cleared.");
                        return;
                    }
                    Emit(args, ChurnLine(25));
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
