using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Steamworks;

namespace SmoothServer
{
    /// <summary>
    /// M12 - Nagle off (and, optionally, a bigger Steam send buffer). Both ends.
    ///
    /// Steam's networking layer coalesces small reliable sends for
    /// k_ESteamNetworkingConfig_NagleTime microseconds before putting them on the wire -
    /// 5000 us (5 ms) by default, and vanilla Valheim never changes it. That is a straight 0-5 ms
    /// of added latency on every ZDO update, in both directions, for a saving that matters on a
    /// modem and not at all on a modern link: SmoothServer already batches at the ZDO layer
    /// (SendCadence sweeps every peer on one tick) and compresses (Compression), so what reaches
    /// Steam is already a big packet. Setting NagleTime=0 sends it immediately.
    ///
    /// This is the cheapest real latency win in the mod, and the reason the FastLink profile
    /// turns it on. It is NOT on by default: the shipped default is vanilla's 5000, in the same
    /// spirit as CreateBudget's "vanilla value, raise it to test" - the module is enabled so it
    /// logs a readback of what the link is actually doing, but changes nothing until you (or a
    /// profile) ask it to.
    ///
    /// <b>Which Steam interface (NOTES §20).</b> The dedicated-server assembly_valheim.dll is
    /// compiled against SteamGameServerNetworkingUtils; the client build against
    /// SteamNetworkingUtils. Only the matching half of Steamworks is initialised in each process
    /// and the other one throws "Steamworks is not initialized." on every call. So this module
    /// picks the interface from the half of the mod it is running as, never calls the other one,
    /// and goes quiet (one Info line, then silence) if its own is unavailable.
    ///
    /// Applied as a postfix on ZSteamSocket.RegisterGlobalCallbacks - the same seam vanilla uses
    /// to write its own four config values, on both builds, before any socket exists - and
    /// re-applied on a live config edit.
    /// </summary>
    internal sealed class LowLatencyModule : FeatureModule
    {
        public override string Name => "LowLatency";
        public override ModuleSide Side => ModuleSide.Both;

        /// <summary>Steam's own default, which vanilla Valheim leaves alone.</summary>
        private const int VanillaNagleMicros = 5000;

        protected override string EnabledDescription =>
            "Let SmoothServer write Steam's global networking config on this side (server: " +
            "SteamGameServerNetworkingUtils, client: SteamNetworkingUtils). While on, the module " +
            "logs a before/after readback of every value it touches. Does nothing at all while " +
            "NagleMicros is 5000 and SendBufferBytes is 0 - those are the vanilla values.";

        private ConfigEntry<int> _nagleMicros;
        private ConfigEntry<int> _sendBufferBytes;

        internal static bool Active2;
        internal static int NagleMicros = VanillaNagleMicros;
        internal static int SendBufferBytes;

        private static bool _registered;      // RegisterGlobalCallbacks has run at least once
        private static bool _absent;          // this side's utils interface refused us
        private static string _lastReadback;

        protected override void Bind()
        {
            _nagleMicros = BindSynced("NagleMicros", VanillaNagleMicros,
                "Microseconds Steam may hold a small reliable message back to coalesce it with " +
                "the next one (k_ESteamNetworkingConfig_NagleTime). 0 = send immediately = " +
                "Nagle off, worth up to 5 ms of latency in each direction. Vanilla/Steam default " +
                "is 5000 (5 ms), which is also the default here, so this module changes nothing " +
                "until you lower it. Clamped to 0-100000." + Profiles.Note);

            _sendBufferBytes = BindSynced("SendBufferBytes", 0,
                "Bytes Steam will buffer per connection before it starts refusing sends " +
                "(k_ESteamNetworkingConfig_SendBufferSize). 0 = leave Steam's default (512 KB) " +
                "alone, which is the right answer unless SendQueueGuard is reporting drops; " +
                "2097152 (2 MB) is a sane raised value for a server pushing 256 KB budgets to " +
                "several peers. A bigger buffer buys burst headroom, not bandwidth - it cannot " +
                "make a link faster, it can only delay the moment a send is refused.");
        }

        protected override void ApplyPatches()
        {
            ReadConfig();

            var reg = AccessTools.Method(typeof(ZSteamSocket), "RegisterGlobalCallbacks");
            if (reg == null)
                throw new Exception("SmoothServer LowLatency: ZSteamSocket.RegisterGlobalCallbacks not found");

            Harmony.Patch(reg, postfix: new HarmonyMethod(typeof(LowLatencyModule), nameof(Postfix)));

            Active2 = true;
            Log.LogInfo("[LowLatency] nagleMicros=" + NagleMicros +
                        (NagleMicros == VanillaNagleMicros ? " (vanilla, no change)" : " (Nagle " + (NagleMicros == 0 ? "OFF" : "shortened") + ")") +
                        " sendBufferBytes=" + (SendBufferBytes == 0 ? "leave-vanilla" : SendBufferBytes.ToString()) +
                        " side=" + (SmoothServerPlugin.IsServerSide ? "server" : "client"));

            // If Steam already registered its callbacks before we patched (hot reload of the
            // module, or another mod forced it early), apply right now instead of waiting.
            if (_registered) Apply("module enabled");
        }

        public override void Disable()
        {
            Active2 = false;
            base.Disable();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != _nagleMicros && entry != _sendBufferBytes) return;
            ReadConfig();
            if (_registered) Apply("config changed");
            else Log.LogInfo("[LowLatency] config changed -> nagleMicros=" + NagleMicros +
                             " sendBufferBytes=" + SendBufferBytes + " (Steam not up yet, will apply later)");
        }

        public override string StatusDetail()
        {
            if (!Applied) return null;
            return "nagleMicros=" + NagleMicros + " sendBufferBytes=" +
                   (SendBufferBytes == 0 ? "vanilla" : SendBufferBytes.ToString()) +
                   (_lastReadback == null ? " (not applied yet)" : "  " + _lastReadback);
        }

        private void ReadConfig()
        {
            NagleMicros = Math.Max(0, Math.Min(100000, _nagleMicros.Value));
            SendBufferBytes = Math.Max(0, _sendBufferBytes.Value);
        }

        private static void Postfix()
        {
            _registered = true;
            if (!Active2) return;
            Apply("RegisterGlobalCallbacks");
        }

        // ---- the actual writes ---------------------------------------------------------------

        private static void Apply(string why)
        {
            if (!Active2 || _absent) return;

            bool server = SmoothServerPlugin.IsServerSide;
            string which = server ? "SteamGameServerNetworkingUtils" : "SteamNetworkingUtils";

            int beforeNagle = Read(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime);
            if (_absent) return;      // the read is also the probe: this side has no interface

            int beforeBuf = Read(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize);

            bool wroteNagle = Write(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime, NagleMicros);
            bool wroteBuf = SendBufferBytes > 0 &&
                            Write(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBufferBytes);

            int afterNagle = Read(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_NagleTime);
            int afterBuf = Read(server, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize);

            _lastReadback = "NagleTime " + Show(beforeNagle) + " -> " + Show(afterNagle) + " us" +
                            " | SendBufferSize " + Show(beforeBuf) + " -> " + Show(afterBuf) + " B";

            SmoothServerPlugin.Log.LogInfo("[LowLatency] (" + (server ? "server" : "client") + ", " + which +
                ", " + why + ") " + _lastReadback +
                "  wroteNagle=" + wroteNagle + " wroteSendBuffer=" + (SendBufferBytes > 0 ? wroteBuf.ToString() : "skipped(0)") +
                (afterNagle == 0 ? "  -- Nagle is OFF" : ""));
        }

        private static string Show(int v)
        {
            return v == int.MinValue ? "n/a" : v.ToString();
        }

        private static void MarkAbsent()
        {
            if (_absent) return;
            _absent = true;
            SmoothServerPlugin.Log.LogInfo("[LowLatency] " +
                (SmoothServerPlugin.IsServerSide ? "SteamGameServerNetworkingUtils" : "SteamNetworkingUtils") +
                " is not initialised in this process - nothing to tune here, going quiet");
        }

        private static bool Write(bool gameServer, ESteamNetworkingConfigValue key, int value)
        {
            if (_absent) return false;
            GCHandle h = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                if (gameServer)
                    return SteamGameServerNetworkingUtils.SetConfigValue(key,
                        ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                        ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());

                return SteamNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, h.AddrOfPinnedObject());
            }
            catch { MarkAbsent(); return false; }
            finally { h.Free(); }
        }

        /// <summary>Reads an int32 config value; int.MinValue when the read is not possible.</summary>
        private static int Read(bool gameServer, ESteamNetworkingConfigValue key)
        {
            if (_absent) return int.MinValue;

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
            catch { MarkAbsent(); return int.MinValue; }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
