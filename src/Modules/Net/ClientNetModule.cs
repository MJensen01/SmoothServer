using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;

namespace SmoothServer.Net
{
    /// <summary>
    /// M10 - the client half of the send path.
    ///
    /// The zone owner is a *client*: the first peer whose active area covers a sector owns and
    /// simulates every mob in it, and its uploads go through exactly the same two vanilla
    /// bottlenecks the server half already fixes -
    ///
    ///   * ZDOMan.SendZDOs' 10240-byte high-water mark (a bandwidth-DELAY product, because
    ///     ZSteamSocket.GetSendQueueSize includes m_cbSentUnackedReliable: at 100 ms RTT that
    ///     caps a peer at ~102 KB/s no matter how fat the pipe is), and
    ///   * Steam's per-connection SendRateMax, pinned to 153600 B/s by
    ///     ZSteamSocket.RegisterGlobalCallbacks.
    ///
    /// Both are raised here, on the client, with the same transpiler SendBudgetModule uses on
    /// the server. There is no collision: SendBudget is a Server-side module and is
    /// disabled(side) in a player's game, so the ZDOMan.SendZDOs literals are untouched when
    /// this module runs, and this module is disabled(side) on the dedicated server.
    ///
    /// SendRateMin is deliberately NOT touched (vanilla 153600). It is a floor on Steam's
    /// bandwidth ESTIMATE: raising it forbids the congestion controller from backing off for a
    /// peer on a weak downlink, and the excess becomes buffering and loss rather than throttling.
    /// BetterNetworking's config couples the two so you cannot raise Max without raising Min;
    /// that is a design mistake and we do not copy it.
    /// </summary>
    internal sealed class ClientNetModule : FeatureModule
    {
        public override string Name => "ClientNet";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Client";

        private const int VanillaHighWater = 10240;

        private ConfigEntry<int> _highWater;
        private ConfigEntry<int> _sendRateMax;

        internal static bool Active2;
        internal static int HighWaterBytes = 49152;

        private int _appliedRate = -1;

        protected override void Bind()
        {
            _highWater = BindSynced("HighWaterBytes", 49152,
                "Client: bytes of queued data above which this client stops adding ZDOs for a " +
                "peer. Vanilla 10240. 48 KB is BetterNetworking's largest setting and roughly " +
                "5x the vanilla bandwidth-delay ceiling at 100 ms RTT.");
            _sendRateMax = BindLocal("SendRateMaxBytesPerSec", 1048576,
                "Client: Steam's per-connection maximum send rate, in bytes/sec. Vanilla pins " +
                "this to 153600. This is a ceiling for bursts, not a target - Steam's estimator " +
                "still decides the real rate. SendRateMin is deliberately left at vanilla. " +
                "Machine-local: it describes YOUR uplink, not the server's.");
        }

        protected override void ApplyPatches()
        {
            HighWaterBytes = Math.Max(4096, _highWater.Value);

            var target = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
            if (target == null) throw new Exception("SmoothServer ClientNet: ZDOMan.SendZDOs not found");
            Harmony.Patch(target, transpiler: new HarmonyMethod(typeof(ClientNetModule), nameof(Transpiler)));

            var reg = AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks");
            if (reg == null) throw new Exception("SmoothServer ClientNet: ZSteamSocket.RegisterGlobalCallbacks not found");
            Harmony.Patch(reg, postfix: new HarmonyMethod(typeof(ClientNetModule), nameof(RegisterGlobalCallbacksPostfix)));

            Active2 = true;
            Instance = this;
            Log.LogInfo("[ClientNet] highWater=" + HighWaterBytes + "B sendRateMax=" + _sendRateMax.Value + "B/s");
        }

        internal static ClientNetModule Instance;

        public override void Disable()
        {
            Active2 = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry == EnabledCfg) Active2 = Applied && Enabled;
            else if (entry == _highWater) HighWaterBytes = Math.Max(4096, _highWater.Value);
            else if (entry == _sendRateMax) ApplySendRate();
            else return;
            Log.LogInfo("[ClientNet] highWater=" + HighWaterBytes + "B sendRateMax=" + _sendRateMax.Value + "B/s");
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "highWater=" + HighWaterBytes + "B sendRateMax=" +
                   (_appliedRate >= 0 ? _appliedRate + "B/s (applied)" : _sendRateMax.Value + "B/s (pending)");
        }

        // Called from patched IL. Falls back to vanilla off-client so the same DLL on a
        // dedicated server (where this module is disabled(side) anyway) is byte-for-byte vanilla.
        internal static int GetHighWaterBytes()
        {
            if (!Active2 || !ClientActive()) return VanillaHighWater;
            return HighWaterBytes;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int hits = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int v;
                if (!ILUtil.TryGetI4(list[i], out v)) continue;
                if (v != VanillaHighWater) continue;
                ILUtil.ReplaceWithCall(list[i], typeof(ClientNetModule), nameof(GetHighWaterBytes));
                hits++;
            }

            if (hits != 2)
            {
                var msg = "SmoothServer ClientNet transpiler: expected exactly 2x " + VanillaHighWater +
                          " in ZDOMan.SendZDOs, found " + hits +
                          " - game IL changed (or another mod patched it first), refusing to patch";
                SmoothServerPlugin.Log.LogError(msg);
                throw new Exception(msg);
            }

            SmoothServerPlugin.Log.LogInfo("[ClientNet] transpiler OK: " + hits +
                                           "x highWater replaced (assertion 2 passed)");
            return list;
        }

        // ---- Steam send rate --------------------------------------------------------------

        private static void RegisterGlobalCallbacksPostfix()
        {
            if (Instance != null) Instance.ApplySendRate();
        }

        private void ApplySendRate()
        {
            if (!Active2 || _sendRateMax == null) return;
            int want = Math.Max(150 * 1024, _sendRateMax.Value);
            int before = GetConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            if (!SetConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, want)) return;
            int after = GetConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax);
            int min = GetConfigInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin);
            _appliedRate = after;
            Log.LogInfo("[ClientNet] Steam SendRateMax: " + before + " -> " + after +
                        " (attempted " + want + "); SendRateMin left at " + min);
        }

        private static int GetConfigInt(ESteamNetworkingConfigValue key)
        {
            ulong size = 4;
            var buf = new byte[4];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                ESteamNetworkingConfigDataType type;
                SteamNetworkingUtils.GetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    out type, pin.AddrOfPinnedObject(), ref size);
            }
            catch { return -1; }
            finally { pin.Free(); }
            return BitConverter.ToInt32(buf, 0);
        }

        private static bool SetConfigInt(ESteamNetworkingConfigValue key, int value)
        {
            var pin = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                SteamNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pin.AddrOfPinnedObject());
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[ClientNet] could not set " + key + ": " + e.Message);
                return false;
            }
            finally { pin.Free(); }
        }
    }
}
