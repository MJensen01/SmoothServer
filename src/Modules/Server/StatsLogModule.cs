using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using SmoothServer.Net;
using UnityEngine;

namespace SmoothServer
{
    /// <summary>
    /// Persistent JSONL stats + events logging for multi-day analysis. Server-side, no Harmony
    /// patches - it is a poller (like PeerTelemetry/Telemetry) plus a set of tiny additive
    /// counters other modules feed as they already do their own work.
    ///
    /// Two files per UTC day, one JSON object per line, flushed on every write:
    ///   stats-YYYY-MM-DD.jsonl  - one snapshot every IntervalSec: fps/frame time, ZDO/scene
    ///                             counts, per-peer network+budget+compression state, and
    ///                             deltas (compression bytes, saves, GC) since the last record.
    ///   events-YYYY-MM-DD.jsonl - discrete events as they happen: player join/leave, world
    ///                             save (with stall ms), GC collect, AdaptiveBudget backoff
    ///                             start/end, SendQueueGuard drops, config reloads.
    ///
    /// Data sources (all pre-existing, read-only from here except the four tiny additive
    /// counters called out at each site - see NOTES.md §19-StatsLog):
    ///   PeerTelemetryModule.Snapshot()      - per-peer ping/pending/in-flight/queue bytes
    ///   AdaptiveBudgetModule.TryGetBudget() - per-peer target bytes + congested (NEW accessor)
    ///   Net.CompressionModule               - RawOut/WireOut/RawIn/WireIn totals (existing
    ///                                          internal statics) + IsFramedFor() (NEW accessor)
    ///   AsyncSaveModule.ConsumeSaveStats()  - save count + max stall since last read (NEW)
    ///   GcThrottleModule.ConsumeGcEvents()  - sweep count since last read (NEW)
    ///   SendQueueGuardModule.ConsumeDrops() - drop episodes since last read (NEW)
    ///   ConfigWatcher.ConsumeReloadSummaries() - reload change summaries since last read (NEW)
    ///   ZDOMan.instance / ZNetScene.instance - zdos, zdos/sec, scene object count (as Telemetry)
    ///
    /// Player join/leave and AdaptiveBudget backoff transitions are detected here by diffing
    /// PeerTelemetryModule.Snapshot() against the previous tick - no new hook needed anywhere.
    ///
    /// No config sync: every entry is BindLocal. Stats are a machine-local operational concern,
    /// not gameplay, and there is nothing useful to enforce on a client (this module is
    /// Side=Server and never runs there).
    /// </summary>
    internal sealed class StatsLogModule : FeatureModule
    {
        public override string Name => "StatsLog";
        public override string Section => "StatsLog";

        private ConfigEntry<float> _intervalSec;
        private ConfigEntry<int> _retentionDays;
        private ConfigEntry<string> _dir;

        internal static bool Active;
        private static float IntervalSec = 10f;
        private static int RetentionDays = 30;
        private static string Dir;

        // ---- frame-time accumulation, own window (independent of TelemetryModule's) --------
        private static float _frameAcc;
        private static int _frameCount;
        private static float _frameWorst;
        private static float _writeAcc;

        // ---- fast poll (join/leave + backoff transition detection), 1s ---------------------
        private static float _pollAcc;
        private const float PollIntervalSec = 1f;
        private static readonly Dictionary<long, string> KnownPeers = new Dictionary<long, string>();
        private static readonly Dictionary<long, bool> KnownCongested = new Dictionary<long, bool>();

        // ---- compression delta tracking -----------------------------------------------------
        private static long _lastRawOut, _lastWireOut, _lastRawIn, _lastWireIn;

        // ---- file handles ---------------------------------------------------------------------
        private static StreamWriter _statsWriter;
        private static StreamWriter _eventsWriter;
        private static string _statsDate = "";
        private static string _eventsDate = "";

        protected override void Bind()
        {
            // Legacy-style plain Bind here (not BindSynced/BindLocal helper) is unnecessary -
            // use BindLocal directly: nothing here should ever be pushed to a client.
            _intervalSec = BindLocal("IntervalSec", 10f,
                "Seconds between stats-YYYY-MM-DD.jsonl snapshot records. Hot-reloadable.");
            _retentionDays = BindLocal("RetentionDays", 30,
                "Delete stats/events files older than this many days, checked on each daily " +
                "rotation. Hot-reloadable.");
            _dir = BindLocal("Dir", "",
                "Directory for stats-*.jsonl / events-*.jsonl. Empty = " +
                "<BepInEx config dir>/smoothserver/stats/. Not hot-reloadable (needs a restart).");
            Watch(_intervalSec);
            Watch(_retentionDays);
        }

        protected override void ApplyPatches()
        {
            IntervalSec = Mathf.Max(1f, _intervalSec.Value);
            RetentionDays = Math.Max(1, _retentionDays.Value);
            Dir = string.IsNullOrEmpty(_dir.Value)
                ? Path.Combine(Paths.ConfigPath, "smoothserver", "stats")
                : _dir.Value;

            Directory.CreateDirectory(Dir);

            _frameAcc = 0f; _frameCount = 0; _frameWorst = 0f; _writeAcc = 0f; _pollAcc = 0f;
            KnownPeers.Clear();
            KnownCongested.Clear();
            _lastRawOut = _lastWireOut = _lastRawIn = _lastWireIn = 0;

            Active = true;
            Log.LogInfo("[StatsLog] logging every " + IntervalSec.ToString("F0") + "s to " + Dir +
                        " (retention " + RetentionDays + "d)");
        }

        public override void Disable()
        {
            Active = false;
            CloseWriters();
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == _intervalSec)
            {
                IntervalSec = Mathf.Max(1f, _intervalSec.Value);
                Log.LogInfo("[StatsLog] IntervalSec -> " + IntervalSec.ToString("F0"));
            }
            else if (entry == _retentionDays)
            {
                RetentionDays = Math.Max(1, _retentionDays.Value);
                Log.LogInfo("[StatsLog] RetentionDays -> " + RetentionDays);
                ApplyRetention();
            }
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "dir=" + Dir + " intervalSec=" + IntervalSec.ToString("F0") +
                   " retentionDays=" + RetentionDays;
        }

        // ---- tick ------------------------------------------------------------------------

        internal static void Tick(float dt)
        {
            if (!Active || !ServerActive()) return;

            _frameAcc += dt;
            _frameCount++;
            if (dt > _frameWorst) _frameWorst = dt;

            _pollAcc += dt;
            if (_pollAcc >= PollIntervalSec)
            {
                _pollAcc = 0f;
                try { PollForEvents(); }
                catch (Exception e) { Log.LogError("[StatsLog] event poll failed: " + e); }
            }

            _writeAcc += dt;
            if (_writeAcc < IntervalSec) return;
            _writeAcc = 0f;

            try { WriteStatsRecord(); }
            catch (Exception e) { Log.LogError("[StatsLog] write failed: " + e); }

            _frameAcc = 0f; _frameCount = 0; _frameWorst = 0f;
        }

        // ---- join/leave + backoff transition detection (1s poll) --------------------------

        private static void PollForEvents()
        {
            var stats = PeerTelemetryModule.Snapshot();
            var seen = new HashSet<long>();

            for (int i = 0; i < stats.Length; i++)
            {
                var s = stats[i];
                seen.Add(s.Uid);
                if (!KnownPeers.ContainsKey(s.Uid))
                {
                    KnownPeers[s.Uid] = s.PlayerName;
                    WriteEvent("join", Json.Obj(
                        Json.KV("name", s.PlayerName), Json.KV("id", ShortId(s.Uid))));
                }

                int target; bool congested;
                bool have = AdaptiveBudgetModule.TryGetBudget(s.Uid, out target, out congested);
                bool wasCongested;
                bool knew = KnownCongested.TryGetValue(s.Uid, out wasCongested);
                if (have)
                {
                    if (!knew) { KnownCongested[s.Uid] = congested; }
                    else if (congested != wasCongested)
                    {
                        KnownCongested[s.Uid] = congested;
                        WriteEvent(congested ? "budget_backoff_start" : "budget_backoff_end", Json.Obj(
                            Json.KV("name", s.PlayerName), Json.KV("id", ShortId(s.Uid)),
                            Json.KV("targetBytes", target)));
                    }
                }
            }

            if (seen.Count != KnownPeers.Count)
            {
                var gone = new List<long>();
                foreach (var kv in KnownPeers) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                foreach (var uid in gone)
                {
                    WriteEvent("leave", Json.Obj(
                        Json.KV("name", KnownPeers[uid]), Json.KV("id", ShortId(uid))));
                    KnownPeers.Remove(uid);
                    KnownCongested.Remove(uid);
                }
            }

            // drain the tiny additive counters other modules feed
            int saveCount; double maxStallMs;
            AsyncSaveModule.ConsumeSaveStats(out saveCount, out maxStallMs);
            if (saveCount > 0)
                WriteEvent("save", Json.Obj(Json.KV("count", saveCount), Json.KV("maxStallMs", maxStallMs)));

            int gcCount = GcThrottleModule.ConsumeGcEvents();
            for (int i = 0; i < gcCount; i++) WriteEvent("gc", Json.Obj());

            var drops = SendQueueGuardModule.ConsumeDrops();
            for (int i = 0; i < drops.Count; i++)
                WriteEvent("queue_drop", Json.Obj(
                    Json.KV("endpoint", drops[i].Endpoint), Json.KV("count", drops[i].Count)));

            var reloads = ConfigWatcher.ConsumeReloadSummaries();
            for (int i = 0; i < reloads.Count; i++)
                WriteEvent("config_reload", Json.Obj(Json.KV("changes", reloads[i])));
        }

        // ---- stats record ------------------------------------------------------------------

        private static void WriteStatsRecord()
        {
            var zm = ZDOMan.instance;
            var scene = ZNetScene.instance;

            int peerCount = zm != null ? zm.m_peers.Count : 0;
            int zdos = zm != null ? zm.m_objectsByID.Count : -1;
            int sentPerSec = zm != null ? zm.m_zdosSentLastSec : -1;
            int recvPerSec = zm != null ? zm.m_zdosRecvLastSec : -1;
            int sceneObjs = scene != null ? scene.m_instances.Count : -1;

            float avgMs = _frameCount > 0 ? (_frameAcc / _frameCount) * 1000f : 0f;
            float fps = _frameAcc > 0f ? _frameCount / _frameAcc : 0f;
            float worstMs = _frameWorst * 1000f;

            var stats = PeerTelemetryModule.Snapshot();
            var names = new List<string>(stats.Length);
            var peersJson = new List<string>(stats.Length);

            for (int i = 0; i < stats.Length; i++)
            {
                var s = stats[i];
                names.Add(s.PlayerName);

                int target; bool congested;
                bool haveBudget = AdaptiveBudgetModule.TryGetBudget(s.Uid, out target, out congested);
                bool framed = CompressionModule.IsFramedFor(s.Uid);

                var kvs = new List<string>
                {
                    Json.KV("name", s.PlayerName),
                    Json.KV("id", ShortId(s.Uid)),
                    Json.KV("rttMs", s.Ping),
                    Json.KV("pendingBytes", s.PendingReliable + s.PendingUnreliable),
                    Json.KV("inFlightBytes", s.SentUnackedReliable),
                    Json.KV("queuedBytes", s.SocketQueueBytes),
                    Json.KV("framed", framed)
                };
                if (haveBudget)
                {
                    kvs.Add(Json.KV("budgetTargetBytes", target));
                    kvs.Add(Json.KV("budgetCongested", congested));
                }
                peersJson.Add(Json.ObjRaw(kvs));
            }

            long rawOut = CompressionModule.RawOut, wireOut = CompressionModule.WireOut;
            long rawIn = CompressionModule.RawIn, wireIn = CompressionModule.WireIn;
            long dRawOut = rawOut - _lastRawOut, dWireOut = wireOut - _lastWireOut;
            long dRawIn = rawIn - _lastRawIn, dWireIn = wireIn - _lastWireIn;
            _lastRawOut = rawOut; _lastWireOut = wireOut; _lastRawIn = rawIn; _lastWireIn = wireIn;

            string compression = Json.ObjRaw(new List<string>
            {
                Json.KV("rawOutDelta", dRawOut), Json.KV("wireOutDelta", dWireOut),
                Json.KV("ratioOut", dRawOut > 0 ? (double)dWireOut / dRawOut : 0.0),
                Json.KV("rawInDelta", dRawIn), Json.KV("wireInDelta", dWireIn),
                Json.KV("ratioIn", dRawIn > 0 ? (double)dWireIn / dRawIn : 0.0),
                Json.KV("framedPeers", CompressionModule.FramedPeers)
            });

            string line = Json.ObjRaw(new List<string>
            {
                Json.KV("ts", NowIso()),
                Json.KV("uptimeSec", Time.realtimeSinceStartup),
                Json.KVRaw("players", Json.ObjRaw(new List<string>
                {
                    Json.KV("count", peerCount), Json.KVArrStr("names", names)
                })),
                Json.KV("fps", fps), Json.KV("frameAvgMs", avgMs), Json.KV("frameWorstMs", worstMs),
                Json.KV("zdos", zdos), Json.KV("zdosSentPerSec", sentPerSec),
                Json.KV("zdosRecvPerSec", recvPerSec), Json.KV("sceneObjs", sceneObjs),
                Json.KVArrRaw("peers", peersJson),
                Json.KVRaw("compression", compression)
            });

            var w = GetWriter(ref _statsWriter, ref _statsDate, "stats");
            w.WriteLine(line);
            w.Flush();
        }

        // ---- events -----------------------------------------------------------------------

        private static void WriteEvent(string type, string fieldsObjJson)
        {
            // fieldsObjJson is a full "{...}" object; splice "ts"/"type" in front of its fields.
            string inner = fieldsObjJson.Length >= 2 ? fieldsObjJson.Substring(1, fieldsObjJson.Length - 2) : "";
            string line = "{\"ts\":" + Json.Str(NowIso()) + ",\"type\":" + Json.Str(type) +
                          (inner.Length > 0 ? "," + inner : "") + "}";
            var w = GetWriter(ref _eventsWriter, ref _eventsDate, "events");
            w.WriteLine(line);
            w.Flush();
        }

        // ---- file rotation + retention ------------------------------------------------------

        private static StreamWriter GetWriter(ref StreamWriter writer, ref string lastDate, string prefix)
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (writer != null && lastDate == today) return writer;

            if (writer != null) { try { writer.Dispose(); } catch { } }

            string path = Path.Combine(Dir, prefix + "-" + today + ".jsonl");
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
            lastDate = today;

            if (prefix == "stats") ApplyRetention();
            return writer;
        }

        private static void ApplyRetention()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var cutoff = DateTime.UtcNow.Date.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(Dir, "*-????-??-??.jsonl"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    int dash = name.IndexOf('-');
                    if (dash < 0 || name.Length - dash - 1 != 10) continue;
                    DateTime d;
                    if (!DateTime.TryParseExact(name.Substring(dash + 1), "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out d)) continue;
                    if (d < cutoff)
                    {
                        try { File.Delete(f); Log.LogInfo("[StatsLog] retention: deleted " + Path.GetFileName(f)); }
                        catch (Exception e) { Log.LogWarning("[StatsLog] retention: could not delete " + f + ": " + e.Message); }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("[StatsLog] retention sweep failed: " + e.Message); }
        }

        private static void CloseWriters()
        {
            try { if (_statsWriter != null) _statsWriter.Dispose(); } catch { }
            try { if (_eventsWriter != null) _eventsWriter.Dispose(); } catch { }
            _statsWriter = null; _eventsWriter = null;
            _statsDate = ""; _eventsDate = "";
        }

        // ---- helpers ------------------------------------------------------------------------

        private static string NowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        private static string ShortId(long uid)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < 8; i++) h = (h ^ (int)((uid >> (i * 8)) & 0xFF)) * 16777619;
                return ((uint)h).ToString("x8");
            }
        }
    }

    /// <summary>Minimal hand-rolled JSON writer - no JSON library is referenced by this project.</summary>
    internal static class Json
    {
        internal static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string KV(string key, string value) { return Str(key) + ":" + Str(value); }
        internal static string KV(string key, bool value) { return Str(key) + ":" + (value ? "true" : "false"); }
        internal static string KV(string key, int value) { return Str(key) + ":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        internal static string KV(string key, long value) { return Str(key) + ":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        internal static string KV(string key, float value) { return Str(key) + ":" + Num(value); }
        internal static string KV(string key, double value) { return Str(key) + ":" + Num(value); }

        internal static string KVArrStr(string key, List<string> values)
        {
            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++) parts[i] = Str(values[i]);
            return Str(key) + ":[" + string.Join(",", parts) + "]";
        }

        internal static string KVArrRaw(string key, List<string> rawJsonValues)
        {
            return Str(key) + ":[" + string.Join(",", rawJsonValues.ToArray()) + "]";
        }

        /// <summary>Splice an already-built JSON value (object/array) in as-is - NOT re-escaped.
        /// Use this instead of KV(string,string) whenever the value is itself JSON, or the
        /// object/array ends up double-encoded as a quoted string.</summary>
        internal static string KVRaw(string key, string rawJsonValue)
        {
            return Str(key) + ":" + rawJsonValue;
        }

        internal static string Obj(params string[] kvs)
        {
            return "{" + string.Join(",", kvs) + "}";
        }

        internal static string ObjRaw(List<string> kvs)
        {
            return "{" + string.Join(",", kvs.ToArray()) + "}";
        }
    }
}
