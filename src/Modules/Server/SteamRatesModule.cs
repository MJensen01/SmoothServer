using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;

namespace SmoothServer
{
    /// <summary>
    /// M3 - Steam send-rate bounds, decoupled.
    ///
    /// Vanilla ZSteamSocket.RegisterGlobalCallbacks pins BOTH k_ESteamNetworkingConfig_SendRateMin
    /// and SendRateMax to 153600 B/s at Global scope. These are bounds on Steam's per-connection
    /// *bandwidth estimate*:
    ///   * raising Max lets the estimator climb during a burst - safe;
    ///   * raising Min FORBIDS the congestion controller from backing off below that for a peer on
    ///     a weak downlink, so the excess turns into buffering and loss rather than throttling.
    /// BetterNetworking's config couples the two so you cannot raise Max without raising Min.
    /// We do not: SendRateMin defaults to 0 = "leave vanilla's 153600 alone".
    ///
    /// Applied as a postfix on ZSteamSocket.RegisterGlobalCallbacks (which is what actually writes
    /// the vanilla values), and re-applied on a live config edit. We read the value back through
    /// GetConfigValue on BOTH the user and the game-server utils interface and log both, which
    /// answers open question 1 of SMOOTHSERVER-PHASE2 §7 ("do SteamNetworkingUtils and
    /// SteamGameServerNetworkingUtils share a config store?") with a measurement instead of a guess.
    /// </summary>
    internal sealed class SteamRatesModule : FeatureModule
    {
        public override string Name => "SteamRates";

        private const int VanillaRate = 153600;

        private ConfigEntry<int> _sendRateMax;
        private ConfigEntry<int> _sendRateMin;
        private ConfigEntry<bool> _writeGameServerUtils;
        private ConfigEntry<bool> _writeUserUtils;

        internal static bool Active;
        internal static int SendRateMax = 1048576;
        internal static int SendRateMin;              // 0 = leave vanilla
        internal static bool WriteGameServerUtils = true;
        internal static bool WriteUserUtils = true;

        private static bool _pending;                 // apply on the next tick (server side only)
        private static bool _appliedOnce;

        public override void Configure(ConfigFile cfg)
        {
            EnabledCfg = cfg.Bind("SteamRates", "Enabled", true,
                "Raise Steam's per-connection SendRateMax above vanilla's 153600 B/s.");
            _sendRateMax = cfg.Bind("SteamRates", "SendRateMax", 1048576,
                "Upper bound (bytes/sec) on Steam's per-connection bandwidth estimate. Vanilla " +
                "153600. Raising this only lets the estimator climb during bursts - it is a " +
                "ceiling, not a target. 0 = leave vanilla.");
            _sendRateMin = cfg.Bind("SteamRates", "SendRateMin", 0,
                "Lower bound (bytes/sec). 0 = LEAVE VANILLA (recommended). Raising this forbids " +
                "Steam's congestion control from backing off for a peer on a weak link, which " +
                "converts congestion into buffering and loss. BetterNetworking couples this to " +
                "SendRateMax; we deliberately do not.");
            _writeGameServerUtils = cfg.Bind("SteamRates", "WriteGameServerUtils", true,
                "Write through SteamGameServerNetworkingUtils (the dedicated-server interface).");
            _writeUserUtils = cfg.Bind("SteamRates", "WriteUserUtils", true,
                "Also write through SteamNetworkingUtils - the interface vanilla ZSteamSocket " +
                "itself uses, even on a dedicated server.");
            Watch(_sendRateMax); Watch(_sendRateMin);
            Watch(_writeGameServerUtils); Watch(_writeUserUtils);
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var target = AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks");
            if (target == null)
                throw new Exception("SmoothServer SteamRates: ZSteamSocket.RegisterGlobalCallbacks not found");

            Harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(SteamRatesModule), nameof(Postfix)));

            Active = true;
            _pending = true;
            Log.LogInfo("[SteamRates] SendRateMax=" + (SendRateMax == 0 ? "vanilla(" + VanillaRate + ")" : SendRateMax.ToString()) +
                        " SendRateMin=" + (SendRateMin == 0 ? "vanilla(" + VanillaRate + ", untouched)" : SendRateMin.ToString()) +
                        " writeGameServerUtils=" + WriteGameServerUtils + " writeUserUtils=" + WriteUserUtils);
        }

        public override void Disable()
        {
            Active = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ReadConfig();
            _pending = true;
            Log.LogInfo("[SteamRates] config changed -> SendRateMax=" + SendRateMax + " SendRateMin=" + SendRateMin);
        }

        private void ReadConfig()
        {
            SendRateMax = Math.Max(0, _sendRateMax.Value);
            SendRateMin = Math.Max(0, _sendRateMin.Value);
            WriteGameServerUtils = _writeGameServerUtils.Value;
            WriteUserUtils = _writeUserUtils.Value;
        }

        private static void Postfix()
        {
            if (!Active) return;
            _pending = true;      // ZNet may not exist yet; the tick applies it once it does
        }

        internal static void Tick(float dt)
        {
            if (!Active || !_pending) return;
            if (!ServerActive()) return;
            _pending = false;
            Apply();
        }

        private static void Apply()
        {
            int beforeMaxUser = ReadValue(false, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            int beforeMaxGs = ReadValue(true, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            SmoothServerPlugin.Log.LogInfo("[SteamRates] before: SendRateMax userUtils=" + Show(beforeMaxUser) +
                                           " gameServerUtils=" + Show(beforeMaxGs));

            bool any = false;
            if (SendRateMax > 0)
                any |= Write(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, SendRateMax);
            if (SendRateMin > 0)
                any |= Write(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, SendRateMin);

            int afterMaxUser = ReadValue(false, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            int afterMaxGs = ReadValue(true, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            int afterMinUser = ReadValue(false, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin);
            int afterMinGs = ReadValue(true, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin);

            SmoothServerPlugin.Log.LogInfo("[SteamRates] applied=" + any +
                " -> SendRateMax userUtils=" + Show(afterMaxUser) + " gameServerUtils=" + Show(afterMaxGs) +
                " | SendRateMin userUtils=" + Show(afterMinUser) + " gameServerUtils=" + Show(afterMinGs) +
                " (vanilla is " + VanillaRate + " for both)");

            if (!_appliedOnce)
            {
                _appliedOnce = true;
                if (beforeMaxUser == beforeMaxGs && afterMaxUser == afterMaxGs && afterMaxUser != beforeMaxUser)
                    SmoothServerPlugin.Log.LogInfo("[SteamRates] the two utils interfaces track the same value on this build");
                else if (afterMaxUser != afterMaxGs)
                    SmoothServerPlugin.Log.LogInfo("[SteamRates] the two utils interfaces have SEPARATE config stores on this build");
            }
        }

        private static string Show(int v)
        {
            return v == int.MinValue ? "n/a" : v.ToString();
        }

        private static bool Write(ESteamNetworkingConfigValue key, int value)
        {
            bool ok = false;
            GCHandle h = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                if (WriteGameServerUtils)
                {
                    try
                    {
                        ok |= SteamGameServerNetworkingUtils.SetConfigValue(key,
                            ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                            ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                    }
                    catch (Exception e) { SmoothServerPlugin.Log.LogWarning("[SteamRates] gameServerUtils write failed: " + e.Message); }
                }
                if (WriteUserUtils)
                {
                    try
                    {
                        ok |= SteamNetworkingUtils.SetConfigValue(key,
                            ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                            ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
                    }
                    catch (Exception e) { SmoothServerPlugin.Log.LogWarning("[SteamRates] userUtils write failed: " + e.Message); }
                }
            }
            finally { h.Free(); }
            return ok;
        }

        /// <summary>Reads an int32 config value. Returns int.MinValue when the read is not possible.</summary>
        private static int ReadValue(bool gameServer, ESteamNetworkingConfigValue key)
        {
            IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buf, 0);
                ulong cb = sizeof(int);
                ESteamNetworkingConfigDataType type;
                ESteamNetworkingGetConfigValueResult r;
                if (gameServer)
                    r = SteamGameServerNetworkingUtils.GetConfigValue(key,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out type, buf, ref cb);
                else
                    r = SteamNetworkingUtils.GetConfigValue(key,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out type, buf, ref cb);

                if (r != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OK &&
                    r != ESteamNetworkingGetConfigValueResult.k_ESteamNetworkingGetConfigValue_OKInherited)
                    return int.MinValue;

                return Marshal.ReadInt32(buf);
            }
            catch { return int.MinValue; }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
