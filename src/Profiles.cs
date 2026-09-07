using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace SmoothServer
{
    /// <summary>
    /// The tuning profile the whole plugin runs at. Synced, so the SERVER dictates it.
    /// </summary>
    public enum NetProfile
    {
        /// <summary>Every setting keeps its own shipped default. Conservative, works anywhere.</summary>
        Default,

        /// <summary>
        /// "Make it feel like LAN": a small group on strong PCs and fat, low-latency links.
        /// Overrides a fixed list of settings (see <see cref="Profiles"/>) with much larger
        /// budgets and a 60 Hz send cadence.
        /// </summary>
        FastLink,

        /// <summary>Hands off - the plugin overrides nothing and every value in the cfg stands.</summary>
        Custom
    }

    /// <summary>
    /// 0.4.0 - preset support.
    ///
    /// A preset is deliberately NOT new gameplay code: it is a table of (section, key, value)
    /// triples that the plugin writes into the already-bound config entries once, at load, after
    /// every module has bound its config but before any module installs its patches - and again
    /// whenever the profile changes or the cfg file is reloaded from disk.
    ///
    /// Precedence, in one sentence: <b>Profile=FastLink wins over the values in the cfg file for
    /// the keys in <see cref="Table"/>; Profile=Default forces those same keys back to their
    /// shipped defaults; Profile=Custom touches nothing at all.</b> That is why every overridden
    /// key's description carries <see cref="Note"/> - so a reader of the cfg file finds out from
    /// the key itself, not from the changelog, that something else may be driving it.
    ///
    /// Because the entries are written (not shadowed), the values are visible in the cfg file, in
    /// `cfg.py get`, and in each module's own startup log line: there is exactly one place a
    /// number can come from. Hot reload works the same way - ConfigWatcher.AfterReload re-asserts
    /// the profile after every Config.Reload(), so hand-editing an overridden key while
    /// FastLink is on is immediately corrected (and logged), rather than half-applied.
    ///
    /// [Profiles] Profile is a SYNCED entry, so a client that joins a FastLink server runs
    /// FastLink too - both for the synced keys (which ServerSync pushes anyway) and for the
    /// machine-local ones (Client.SendRateMaxBytesPerSec, FrameRate.TargetFrameRate), which
    /// ServerSync does not push and which this table therefore sets on the client itself.
    /// </summary>
    internal static class Profiles
    {
        /// <summary>Appended to the cfg description of every key the profile table can drive.</summary>
        internal const string Note =
            " || PROFILE: this key is overridden while [Profiles] Profile=FastLink, and forced " +
            "back to the default above while Profile=Default. Set Profile=Custom if you want to " +
            "hand-tune it.";

        private struct Ov
        {
            public string Section;
            public string Key;
            public object Fast;
        }

        private static Ov New(string section, string key, object fast)
        {
            return new Ov { Section = section, Key = key, Fast = fast };
        }

        /// <summary>
        /// The whole preset. Boxed values must be the entry's exact CLR type (60f, not 60).
        /// Anything not listed here is never touched by a profile.
        /// </summary>
        private static readonly Ov[] Table =
        {
            New("SendCadence",     "SendHz",                  60f),      // 20  -> 60 Hz sweeps
            New("AdaptiveBudget",  "CeilingBytes",            262144),   // 128 -> 256 KB
            New("AdaptiveBudget",  "FloorBytes",              32768),    // 16  -> 32 KB
            New("SteamRates",      "SendRateMax",             4194304),  // 1   -> 4 MB/s (server)
            New("Client",          "HighWaterBytes",          131072),   // 48  -> 128 KB
            New("Client",          "SendRateMaxBytesPerSec",  4194304),  // 1   -> 4 MB/s (client)
            New("Compression",     "Enabled",                 true),
            New("LowLatency",      "NagleMicros",             0),        // 5000us -> Nagle off
            New("FrameRate",       "TargetFrameRate",         60),       // 0 (~30) -> 60
            New("CreateBudget",    "MaxCreatedPerFrame",      20),       // 10 -> 20
        };

        internal static ConfigEntry<NetProfile> ProfileCfg;

        private static bool _applying;

        internal static NetProfile Current
        {
            get { return ProfileCfg == null ? NetProfile.Default : ProfileCfg.Value; }
        }

        /// <summary>Bind [Profiles] Profile. Call before the modules bind their own config.</summary>
        internal static void Bind()
        {
            ProfileCfg = SmoothServerPlugin.BindSynced("Profiles", "Profile", NetProfile.Default,
                "Tuning preset for the whole mod. SYNCED - the server dictates it.\n" +
                "Default  = every setting keeps its shipped default (the listed keys are forced " +
                "back to those defaults).\n" +
                "FastLink = 'make it feel like LAN' for a small group on strong PCs and good " +
                "links. Overrides, at load and on every reload: [SendCadence] SendHz=60, " +
                "[AdaptiveBudget] CeilingBytes=262144 FloorBytes=32768, [SteamRates] " +
                "SendRateMax=4194304, [Client] HighWaterBytes=131072 " +
                "SendRateMaxBytesPerSec=4194304, [Compression] Enabled=true, [LowLatency] " +
                "NagleMicros=0, [FrameRate] TargetFrameRate=60, [CreateBudget] " +
                "MaxCreatedPerFrame=20. Nothing else is touched.\n" +
                "Custom   = the plugin overrides nothing; every value in this file stands.\n" +
                "Precedence: for the keys listed above, the profile beats the file (the new value " +
                "is written into the file, so what you read here is always what is running). " +
                "Switching FastLink -> Default reverts exactly those keys. Machine-specific " +
                "hand-tuning belongs on Profile=Custom.", null);

            // Not during a Config.Reload(): BepInEx raises SettingChanged entry by entry as it
            // reads the file, so applying here would be half-undone by the entries the reload
            // has not reached yet (and would flap the values that come after Profile in the
            // file). ConfigWatcher.AfterReload applies it once, cleanly, instead.
            ProfileCfg.SettingChanged += (s, a) =>
            {
                if (!ConfigWatcher.Reloading) Apply("profile changed");
            };
        }

        /// <summary>
        /// Write the profile into the bound entries. Idempotent: an entry already holding the
        /// wanted value is not re-assigned, so this never fires a spurious SettingChanged and
        /// never rewrites the cfg file for nothing (which would loop against ConfigWatcher).
        /// </summary>
        internal static void Apply(string why)
        {
            if (ProfileCfg == null || _applying) return;

            var log = SmoothServerPlugin.Log;
            var profile = ProfileCfg.Value;

            if (profile == NetProfile.Custom)
            {
                log.LogInfo("[Profiles] Custom (" + why + "): no overrides applied, every value in " +
                            "the cfg file stands");
                return;
            }

            _applying = true;
            var changed = new List<string>();
            var missing = new List<string>();
            try
            {
                foreach (var ov in Table)
                {
                    var entry = Find(ov.Section, ov.Key);
                    if (entry == null) { missing.Add(ov.Section + "." + ov.Key); continue; }

                    object want = profile == NetProfile.FastLink ? ov.Fast : entry.DefaultValue;
                    object cur;
                    try { cur = entry.BoxedValue; }
                    catch (Exception e) { log.LogWarning("[Profiles] cannot read " + ov.Section + "." + ov.Key + ": " + e.Message); continue; }

                    if (Equals(cur, want)) continue;

                    try
                    {
                        entry.BoxedValue = want;
                        changed.Add(ov.Section + "." + ov.Key + " " + Str(cur) + "->" + Str(want));
                    }
                    catch (Exception e)
                    {
                        log.LogWarning("[Profiles] cannot set " + ov.Section + "." + ov.Key +
                                       " to " + Str(want) + ": " + e.Message);
                    }
                }
            }
            finally { _applying = false; }

            log.LogInfo("[Profiles] " + profile + " (" + why + "): " +
                        (changed.Count == 0
                            ? "already in effect, 0 changes"
                            : changed.Count + " changed: " + string.Join(", ", changed.ToArray())));

            if (missing.Count > 0)
                log.LogWarning("[Profiles] " + missing.Count + " key(s) in the profile table are not " +
                               "bound in this build: " + string.Join(", ", missing.ToArray()));
        }

        /// <summary>One line for the module summary.</summary>
        internal static string Summary()
        {
            var parts = new List<string>();
            foreach (var ov in Table)
            {
                var e = Find(ov.Section, ov.Key);
                if (e == null) continue;
                try { parts.Add(ov.Section + "." + ov.Key + "=" + Str(e.BoxedValue)); }
                catch { /* best effort */ }
            }
            return "[Profiles] Profile=" + Current + " -> " + string.Join(" ", parts.ToArray());
        }

        private static ConfigEntryBase Find(string section, string key)
        {
            var cfg = SmoothServerPlugin.Cfg;
            if (cfg == null) return null;
            foreach (var kv in cfg)
            {
                if (kv.Key.Section == section && kv.Key.Key == key) return kv.Value;
            }
            return null;
        }

        private static string Str(object o)
        {
            if (o == null) return "null";
            if (o is float) return ((float)o).ToString("0.##");
            if (o is bool) return ((bool)o) ? "true" : "false";
            return o.ToString();
        }
    }
}
