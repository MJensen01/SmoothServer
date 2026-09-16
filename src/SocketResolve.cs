using System;
using System.Collections.Generic;
using System.Reflection;

namespace SmoothServer
{
    /// <summary>
    /// Finds the real <see cref="ZSteamSocket"/> behind a peer's ISocket. Shared by every module
    /// that needs the Steam socket itself rather than "whatever ZNetPeer.m_socket happens to hold
    /// right now" - PeerTelemetry (Steam's real-time status) and Compression (the send queue our
    /// Harmony patches frame). Not a module: no side, no config, no patches.
    ///
    /// <b>Why a plain cast is not enough.</b> ServerSync - which we vendor, and so do
    /// NoVikingLeftBehind and third-party mods such as Hugo's Armory - swaps a decorator socket
    /// (its <c>BufferingSocket</c>, a ZPlayFabSocket subclass that forwards every ISocket call to
    /// its <c>Original</c>) into <c>ZNetPeer.m_socket</c> for the duration of ZNet.RPC_PeerInfo and
    /// puts the original back afterwards. Each copy's restore only recognises <i>its own</i>
    /// BufferingSocket type, so with several copies loaded the unwind does not complete and the
    /// peer holds another mod's decorator for the rest of the session. Every ISocket call keeps
    /// working - the decorator forwards - so nothing looks broken; but
    /// <c>peer.m_socket as ZSteamSocket</c> is null forever, which silently switched off both
    /// PeerTelemetry's Steam numbers and Compression's handshake on Matt's server (0.5.1).
    /// </summary>
    internal static class SocketResolve
    {
        private static readonly Dictionary<Type, FieldInfo> InnerFieldCache = new Dictionary<Type, FieldInfo>();

        /// <summary>
        /// The real <see cref="ZSteamSocket"/> behind an ISocket, unwrapping any decorator sockets
        /// in front of it (they hold what they wrap in a field called <c>Original</c>). Returns the
        /// argument itself when it already is a ZSteamSocket, and null when there is no Steam socket
        /// in the chain - e.g. a genuine PlayFab/crossplay peer.
        /// </summary>
        internal static ZSteamSocket ResolveSteamSocket(ISocket sock)
        {
            for (int depth = 0; sock != null && depth < 8; depth++)
            {
                var zs = sock as ZSteamSocket;
                if (zs != null) return zs;
                var inner = InnerSocket(sock);
                if (inner == null || ReferenceEquals(inner, sock)) return null;
                sock = inner;
            }
            return null;
        }

        /// <summary>The ISocket a decorator wraps, or null. One reflection pass per socket type.</summary>
        private static ISocket InnerSocket(ISocket sock)
        {
            var t = sock.GetType();
            FieldInfo f;
            if (!InnerFieldCache.TryGetValue(t, out f))
            {
                f = FindInnerField(t);
                InnerFieldCache[t] = f;
            }
            if (f == null) return null;
            try { return f.GetValue(sock) as ISocket; }
            catch { return null; }
        }

        private static FieldInfo FindInnerField(Type t)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo any = null;
                FieldInfo[] fields;
                try { fields = cur.GetFields(Flags); }
                catch { continue; }

                for (int i = 0; i < fields.Length; i++)
                {
                    if (!typeof(ISocket).IsAssignableFrom(fields[i].FieldType)) continue;
                    // ServerSync calls it "Original"; prefer that over any other ISocket field.
                    if (fields[i].Name.IndexOf("Original", StringComparison.OrdinalIgnoreCase) >= 0)
                        return fields[i];
                    if (any == null) any = fields[i];
                }
                if (any != null) return any;
            }
            return null;
        }
    }
}
