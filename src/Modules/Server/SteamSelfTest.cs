using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Steamworks;

namespace SmoothServer
{
    /// <summary>
    /// [General] SteamSelfTest - off by default, machine-local, no patches.
    ///
    /// Answers, at plugin load and in the log, the question that cost 0.3.0 a broken release:
    /// <b>which half of Steamworks is live in THIS process, and which one does THIS build of
    /// assembly_valheim.dll actually call?</b>
    ///
    /// The client build of ZSteamSocket calls SteamNetworkingSockets / SteamNetworkingUtils /
    /// SteamUser; the dedicated-server build is compiled against SteamGameServerNetworkingSockets /
    /// SteamGameServerNetworkingUtils / SteamGameServer (NOTES §20). Only the matching half is
    /// initialised, so the other throws "Steamworks is not initialized.". A mod that names one
    /// interface literally is therefore wrong on one of the two builds.
    ///
    /// Two independent probes:
    ///   1. <b>runtime</b> - call something harmless on each interface and report reached/threw;
    ///   2. <b>IL</b> - read the method body of ZSteamSocket.SendQueuedPackages / Recv and report
    ///      the declaring type of every Steam* call it contains. This is the authoritative answer
    ///      and needs no live connection.
    /// </summary>
    internal static class SteamSelfTest
    {
        internal static void Run()
        {
            var log = SmoothServerPlugin.Log;
            log.LogInfo("[SteamSelfTest] ---- begin (side=" +
                        (SmoothServerPlugin.IsServerSide ? "SERVER" : "CLIENT") + ") ----");

            Probe("SteamGameServer.GetSteamID()", () => SteamGameServer.GetSteamID().ToString());
            Probe("SteamUser.GetSteamID()", () => SteamUser.GetSteamID().ToString());
            Probe("SteamGameServerNetworkingUtils.GetLocalTimestamp()",
                () => SteamGameServerNetworkingUtils.GetLocalTimestamp().ToString());
            Probe("SteamNetworkingUtils.GetLocalTimestamp()",
                () => SteamNetworkingUtils.GetLocalTimestamp().ToString());
            Probe("SteamGameServerNetworkingSockets.GetIdentity()", delegate
            {
                SteamNetworkingIdentity id;
                bool ok = SteamGameServerNetworkingSockets.GetIdentity(out id);
                return ok ? id.GetSteamID().ToString() : "(no identity yet)";
            });
            Probe("SteamNetworkingSockets.GetIdentity()", delegate
            {
                SteamNetworkingIdentity id;
                bool ok = SteamNetworkingSockets.GetIdentity(out id);
                return ok ? id.GetSteamID().ToString() : "(no identity yet)";
            });

            ScanIL("SendQueuedPackages");
            ScanIL("Recv");
            ScanIL("RegisterGlobalCallbacks");
            ScanIL("GetConnectionQuality");

            log.LogInfo("[SteamSelfTest] ---- end ----");
        }

        private static void Probe(string what, Func<string> call)
        {
            try { SmoothServerPlugin.Log.LogInfo("[SteamSelfTest] OK    " + what + " -> " + call()); }
            catch (Exception e)
            {
                var m = e.InnerException != null ? e.InnerException.Message : e.Message;
                SmoothServerPlugin.Log.LogInfo("[SteamSelfTest] THREW " + what + " -> " + m.Trim());
            }
        }

        /// <summary>Reports the Steam* types called from ZSteamSocket.&lt;name&gt; on this build.</summary>
        private static void ScanIL(string name)
        {
            var log = SmoothServerPlugin.Log;
            try
            {
                var m = AccessTools.Method(typeof(ZSteamSocket), name);
                if (m == null) { log.LogInfo("[SteamSelfTest] IL    ZSteamSocket." + name + " -> not found"); return; }

                var seen = new StringBuilder();
                foreach (var pair in PatchProcessor.ReadMethodBody(m))
                {
                    var mi = pair.Value as MethodBase;
                    if (mi == null || mi.DeclaringType == null) continue;
                    var t = mi.DeclaringType.Name;
                    if (!t.StartsWith("Steam", StringComparison.Ordinal)) continue;
                    var entry = t + "." + mi.Name;
                    if (seen.ToString().Contains(entry)) continue;
                    if (seen.Length > 0) seen.Append(", ");
                    seen.Append(entry);
                }
                log.LogInfo("[SteamSelfTest] IL    ZSteamSocket." + name + " calls -> " +
                            (seen.Length == 0 ? "(no Steam* calls)" : seen.ToString()));
            }
            catch (Exception e)
            {
                log.LogInfo("[SteamSelfTest] IL    ZSteamSocket." + name + " -> scan failed: " + e.Message);
            }
        }
    }
}
