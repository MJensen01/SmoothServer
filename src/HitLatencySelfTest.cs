using System;
using System.Collections.Generic;

namespace SmoothServer
{
    /// <summary>
    /// Headless unit tests for the two hit-latency modules, in ILSelfTest's spirit: no game state,
    /// no Harmony, no network, 0 players. Every case is arithmetic over synthetic packages or over
    /// a synthetic <see cref="ClaimCtx"/>, and the count/failures fold into ILSelfTest's single
    /// PASS/FAIL line.
    ///
    /// What these exist to prove - each one is an ordering rule whose violation is silent and
    /// expensive on a live server:
    ///   * a routed RPC targeting a ZDO the peer has not been sent is NOT promoted (vanilla drops
    ///     such an RPC without a word - `ZRoutedRpc.HandleRoutedRPC`);
    ///   * a deny-listed method (`DestroyZDO`) is never promoted, at any setting;
    ///   * Compression's handshake RPCs are barriers and nothing overtakes them, so the framing
    ///     switch point stays at one byte position of one FIFO;
    ///   * a package is never split, copied or edited - the shaper moves whole byte[] references;
    ///   * the inflight cap is respected, and an oversized package still cannot deadlock;
    ///   * the hot window expires;
    ///   * every CombatOwnership guard actually refuses.
    /// </summary>
    internal static class HitLatencySelfTest
    {
        internal static int Run(List<string> fails)
        {
            int cases = 0;
            cases += WireCases(fails);
            cases += DecideCases(fails);
            cases += ShaperCases(fails);
            cases += HotCases(fails);
            cases += ClaimCases(fails);
            return cases;
        }

        // ---- synthetic packages ---------------------------------------------------------------

        /// <summary>A package whose first four bytes are `outerHash`, padded to `len`.</summary>
        private static byte[] Pkt(int outerHash, int len)
        {
            var b = new byte[len];
            var h = BitConverter.GetBytes(outerHash);
            Buffer.BlockCopy(h, 0, b, 0, 4);
            return b;
        }

        /// <summary>A RoutedRPC package with the given inner method hash and target ZDOID.</summary>
        private static byte[] Routed(int innerHash, long zdoUser, uint zdoId, int len = 64)
        {
            var b = Pkt(PriorityLaneModule.HashRoutedRpc, len);
            Buffer.BlockCopy(BitConverter.GetBytes(len - 8), 0, b, 4, 4);     // inner ZPackage length
            Buffer.BlockCopy(BitConverter.GetBytes(zdoUser), 0, b, LaneWire.RoutedZdoUserOffset, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(zdoId), 0, b, LaneWire.RoutedZdoIdOffset, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(innerHash), 0, b, LaneWire.RoutedInnerHashOffset, 4);
            return b;
        }

        // ---- 1. the wire reader -----------------------------------------------------------------

        private static int WireCases(List<string> fails)
        {
            int n = 0;

            // 1a. the seven documented package hashes are still what the game computes.
            n++;
            var named = new[]
            {
                new object[] { "RoutedRPC", PriorityLaneModule.HashRoutedRpc },
                new object[] { "ZDOData", PriorityLaneModule.HashZdoData },
                new object[] { "PeerInfo", PriorityLaneModule.HashPeerInfo },
                new object[] { "NetTime", PriorityLaneModule.HashNetTime },
                new object[] { "RefPos", PriorityLaneModule.HashRefPos },
                new object[] { "PlayerList", PriorityLaneModule.HashPlayerList },
            };
            foreach (var row in named)
            {
                int actual = ((string)row[0]).GetStableHashCode();
                if (actual != (int)row[1])
                    fails.Add("PriorityLane: \"" + row[0] + "\" hashes to " + actual + ", documented " + row[1]);
            }

            // 1b. every one of them reads back out of the first four bytes, ping (0) included.
            n++;
            int[] all = { PriorityLaneModule.HashPing, PriorityLaneModule.HashRoutedRpc,
                          PriorityLaneModule.HashZdoData, PriorityLaneModule.HashPeerInfo,
                          PriorityLaneModule.HashNetTime, PriorityLaneModule.HashRefPos,
                          PriorityLaneModule.HashPlayerList };
            foreach (var h in all)
                if (LaneWire.OuterHash(Pkt(h, 16)) != h)
                    fails.Add("PriorityLane: outer hash " + h + " did not read back");

            // 1c. the offset-44 inner reader.
            n++;
            int inner; long user; uint id;
            if (!LaneWire.TryReadRouted(Routed(12345, 77L, 9u), out inner, out user, out id) ||
                inner != 12345 || user != 77L || id != 9u)
                fails.Add("PriorityLane: routed reader returned " + inner + "/" + user + "/" + id);

            // 1d. a package too short to hold the inner hash is never read as one.
            n++;
            if (LaneWire.TryReadRouted(Pkt(PriorityLaneModule.HashRoutedRpc, LaneWire.RoutedMinLength - 1),
                                       out inner, out user, out id))
                fails.Add("PriorityLane: read a routed header out of a 47-byte package");

            // 1e. a null or 3-byte package is not classifiable and must not throw.
            n++;
            if (LaneWire.OuterHash(null) != int.MinValue || LaneWire.OuterHash(new byte[3]) != int.MinValue)
                fails.Add("PriorityLane: short/null package was classified");

            return n;
        }

        // ---- 2. the promotion decision -----------------------------------------------------------

        private static int DecideCases(List<string> fails)
        {
            int n = 0;
            var deny = new HashSet<int> { "DestroyZDO".GetStableHashCode() };
            var barriers = new HashSet<int>
            {
                Net.CompressionModule.RpcCaps.GetStableHashCode(),
                Net.CompressionModule.RpcReady.GetStableHashCode(),
                Net.CompressionModule.RpcOff.GetStableHashCode(),
            };
            int damage = "RPC_Damage".GetStableHashCode();
            Func<long, uint, bool> knows = (u, i) => u == 1L;      // peer knows only ZDOs of user 1
            LaneWire.Reason why;

            // 2a. a damage RPC whose target the peer has is promoted.
            n++;
            if (LaneWire.Decide(Routed(damage, 1L, 5u), false, deny, barriers, true, knows, out why) != LaneWire.Verdict.Prio)
                fails.Add("PriorityLane: a known-ZDO damage RPC was not promoted (" + why + ")");

            // 2b. THE ordering rule: an RPC targeting a ZDO the peer has not been sent is NOT
            //     promoted - vanilla would silently drop it if it overtook that ZDO.
            n++;
            if (LaneWire.Decide(Routed(damage, 2L, 5u), false, deny, barriers, true, knows, out why) != LaneWire.Verdict.Bulk ||
                why != LaneWire.Reason.UnknownZdo)
                fails.Add("PriorityLane: an RPC for an unknown ZDO was promoted (" + why + ")");

            // 2c. with RequireKnownZdo off it is promoted - the setting has to actually do something.
            n++;
            if (LaneWire.Decide(Routed(damage, 2L, 5u), false, deny, barriers, false, knows, out why) != LaneWire.Verdict.Prio)
                fails.Add("PriorityLane: RequireKnownZdo=false did not promote");

            // 2d. the deny-list wins over everything, even a ZDO the peer certainly has.
            n++;
            if (LaneWire.Decide(Routed("DestroyZDO".GetStableHashCode(), 1L, 5u), false, deny, barriers, true, knows, out why)
                    != LaneWire.Verdict.Bulk || why != LaneWire.Reason.DenyListed)
                fails.Add("PriorityLane: DestroyZDO was promoted (" + why + ")");

            // 2e. a broadcast RPC with no target ZDO has nothing to race.
            n++;
            if (LaneWire.Decide(Routed(damage, 0L, 0u), false, deny, barriers, true, knows, out why) != LaneWire.Verdict.Prio ||
                why != LaneWire.Reason.NoTargetZdo)
                fails.Add("PriorityLane: a target-less RPC was not promoted (" + why + ")");

            // 2f. Compression's three handshake RPCs are barriers, never prio.
            n++;
            foreach (var rpc in new[] { Net.CompressionModule.RpcCaps, Net.CompressionModule.RpcReady, Net.CompressionModule.RpcOff })
                if (LaneWire.Decide(Routed(rpc.GetStableHashCode(), 0L, 0u), false, deny, barriers, true, knows, out why)
                        != LaneWire.Verdict.Barrier)
                    fails.Add("PriorityLane: " + rpc + " was not treated as an ordering barrier");

            // 2g. ZDOData: bulk normally, prio exactly when a hot ZDO was force-sent to this peer.
            n++;
            if (LaneWire.Decide(Pkt(PriorityLaneModule.HashZdoData, 900), false, deny, barriers, true, knows, out why) != LaneWire.Verdict.Bulk)
                fails.Add("PriorityLane: a plain ZDOData package was promoted");
            if (LaneWire.Decide(Pkt(PriorityLaneModule.HashZdoData, 900), true, deny, barriers, true, knows, out why) != LaneWire.Verdict.Prio)
                fails.Add("PriorityLane: a hot ZDOData package was not promoted");

            // 2h. ping stays bulk on purpose (promoting it would hide the queue delay it measures).
            n++;
            foreach (var h in new[] { PriorityLaneModule.HashPing, PriorityLaneModule.HashPeerInfo,
                                      PriorityLaneModule.HashNetTime, PriorityLaneModule.HashRefPos,
                                      PriorityLaneModule.HashPlayerList })
                if (LaneWire.Decide(Pkt(h, 32), true, deny, barriers, true, knows, out why) != LaneWire.Verdict.Bulk)
                    fails.Add("PriorityLane: package " + h + " was promoted");

            return n;
        }

        // ---- 3. the shaper ---------------------------------------------------------------------

        private static LanePacket P(byte[] data, bool prio = false, bool barrier = false)
        {
            return new LanePacket { Data = data, Prio = prio, Barrier = barrier };
        }

        private static int ShaperCases(List<string> fails)
        {
            int n = 0;
            const int NoPrioCap = int.MaxValue;

            // 3a. prio before bulk, FIFO within each.
            n++;
            var q = new LaneQueue();
            var b1 = new byte[100]; var b2 = new byte[100]; var p1 = new byte[100]; var p2 = new byte[100];
            q.Enqueue(P(b1)); q.Enqueue(P(p1, prio: true)); q.Enqueue(P(b2)); q.Enqueue(P(p2, prio: true));
            var got = new List<byte[]>();
            q.Drain(10000, NoPrioCap, false, got);
            if (got.Count != 4 || !ReferenceEquals(got[0], p1) || !ReferenceEquals(got[1], p2) ||
                !ReferenceEquals(got[2], b1) || !ReferenceEquals(got[3], b2))
                fails.Add("PriorityLane: drain order was not prio-then-bulk with FIFO inside each");
            if (q.Count != 0 || q.HeldBytes != 0)
                fails.Add("PriorityLane: held accounting did not return to zero (" + q.Count + "/" + q.HeldBytes + ")");

            // 3b. the inflight cap: never hand Steam more than the allowance.
            n++;
            q = new LaneQueue();
            for (int i = 0; i < 10; i++) q.Enqueue(P(new byte[1000]));
            got.Clear();
            q.Drain(2500, NoPrioCap, false, got);
            int sent = 0; foreach (var g in got) sent += g.Length;
            if (sent > 2500) fails.Add("PriorityLane: drained " + sent + "B against a 2500B allowance");
            if (got.Count != 2) fails.Add("PriorityLane: expected 2 packages under a 2500B allowance, got " + got.Count);
            if (q.HeldBytes != 8000) fails.Add("PriorityLane: held bytes after a capped drain = " + q.HeldBytes);

            // 3c. a package larger than the whole cap still goes out when nothing is in flight.
            n++;
            q = new LaneQueue();
            q.Enqueue(P(new byte[9000]));
            got.Clear();
            q.Drain(0, NoPrioCap, true, got);
            if (got.Count != 1) fails.Add("PriorityLane: an oversized package deadlocked the lane");
            // ... and does NOT when something already is.
            q = new LaneQueue();
            q.Enqueue(P(new byte[9000]));
            got.Clear();
            q.Drain(0, NoPrioCap, false, got);
            if (got.Count != 0) fails.Add("PriorityLane: drained past a zero allowance with bytes in flight");

            // 3d. THE Compression rule: a prio package queued after a barrier must not overtake it.
            n++;
            q = new LaneQueue();
            var early = new byte[10]; var ready = new byte[10]; var late = new byte[10];
            q.Enqueue(P(early, prio: true));
            q.Enqueue(P(ready, barrier: true));
            q.Enqueue(P(late, prio: true));
            got.Clear();
            q.Drain(10000, NoPrioCap, false, got);
            if (got.Count != 3 || !ReferenceEquals(got[0], early) || !ReferenceEquals(got[1], ready) ||
                !ReferenceEquals(got[2], late))
                fails.Add("PriorityLane: a prio package crossed an SS_Ready barrier");

            // 3e. a barrier is never promoted even if something marked it prio.
            n++;
            q = new LaneQueue();
            var bar = new byte[10]; var after = new byte[10];
            q.Enqueue(P(bar, prio: true, barrier: true));
            q.Enqueue(P(after, prio: true));
            got.Clear();
            q.Drain(10000, NoPrioCap, false, got);
            if (got.Count != 2 || !ReferenceEquals(got[0], bar))
                fails.Add("PriorityLane: a barrier package was promoted");

            // 3f. frames are never split, copied or edited: every drained array is an input array,
            //     exactly once, byte for byte the same object.
            n++;
            q = new LaneQueue();
            var inputs = new List<byte[]>();
            for (int i = 0; i < 20; i++)
            {
                var a = new byte[16 + i];
                a[0] = (byte)i;
                inputs.Add(a);
                q.Enqueue(P(a, prio: (i % 3) == 0));
            }
            got.Clear();
            q.Drain(int.MaxValue, NoPrioCap, false, got);
            if (got.Count != inputs.Count) fails.Add("PriorityLane: drained " + got.Count + " of " + inputs.Count + " packages");
            var seen = new HashSet<byte[]>();
            foreach (var g in got)
            {
                if (!inputs.Contains(g)) fails.Add("PriorityLane: drained an array that was never queued");
                if (!seen.Add(g)) fails.Add("PriorityLane: drained the same array twice");
            }

            // 3g. the starvation guard hands bulk a turn once MaxPrioBytesPerDrain is used up.
            n++;
            q = new LaneQueue();
            for (int i = 0; i < 5; i++) q.Enqueue(P(new byte[1000], prio: true));
            var bulk = new byte[10];
            q.Enqueue(P(bulk));
            got.Clear();
            q.Drain(int.MaxValue, 2000, false, got);
            int bulkAt = got.IndexOf(bulk);
            if (bulkAt != 2) fails.Add("PriorityLane: starvation guard let " + bulkAt + " prio packages out before bulk (wanted 2)");

            return n;
        }

        // ---- 4. the hot window -------------------------------------------------------------------

        private static int HotCases(List<string> fails)
        {
            int n = 0;
            var hot = new HotSet();
            var id = new ZDOID(42L, 7u);

            n++;
            hot.Mark(id, 99L, 100f, 3u);
            if (!hot.IsHot(id, 100.5f, 1.5f)) fails.Add("PriorityLane: a just-damaged ZDO was not hot");
            if (hot.IsHot(id, 102f, 1.5f)) fails.Add("PriorityLane: a ZDO stayed hot past HotWindowMs");

            n++;
            hot.Expire(102f, 1.5f);
            if (hot.Count != 0) fails.Add("PriorityLane: an expired hot entry was not dropped");
            hot.Expire(102f, 1.5f);                      // idempotent, must not throw on an empty set

            n++;
            hot.Clear();
            for (int i = 0; i < 400; i++) hot.Mark(new ZDOID(1L, (uint)i), 1L, 0f, 0u);
            if (hot.Count > 300) fails.Add("PriorityLane: the hot set is unbounded (" + hot.Count + " entries)");

            return n;
        }

        // ---- 5. the claim policy ------------------------------------------------------------------

        private static ClaimCtx Ok()
        {
            return new ClaimCtx
            {
                Enabled = true,
                AttackerIsLocal = true,
                AlreadyOwner = false,
                IsPlayerZdo = false,
                Distance = 3f,
                MaxDistance = 16f,
                RequireWard = true,
                PrivateAreaOk = true,
                ExcludeVehicles = true,
                OnVehicle = false,
                SkipOnlinePeerPieces = true,
                IsPiece = false,
                OwnerIsOnlinePeer = false,
            };
        }

        private static int ClaimCases(List<string> fails)
        {
            int n = 0;
            string why;

            n++;
            if (!ClaimPolicy.ShouldClaim(Ok(), out why))
                fails.Add("CombatOwnership: the ordinary case refused (" + why + ")");
            if (!ClaimPolicy.PassesCheapGates(Ok()))
                fails.Add("CombatOwnership: the ordinary case failed the cheap gates");

            // Each guard, one at a time, must refuse.
            var table = new List<KeyValuePair<string, ClaimCtx>>();
            var c = Ok(); c.Enabled = false; table.Add(new KeyValuePair<string, ClaimCtx>("disabled", c));
            c = Ok(); c.AttackerIsLocal = false; table.Add(new KeyValuePair<string, ClaimCtx>("somebody else's hit", c));
            c = Ok(); c.AlreadyOwner = true; table.Add(new KeyValuePair<string, ClaimCtx>("already owner", c));
            c = Ok(); c.IsPlayerZdo = true; table.Add(new KeyValuePair<string, ClaimCtx>("player zdo", c));
            c = Ok(); c.Distance = 16.1f; table.Add(new KeyValuePair<string, ClaimCtx>("out of range", c));
            c = Ok(); c.OnVehicle = true; table.Add(new KeyValuePair<string, ClaimCtx>("on a ship/cart", c));
            c = Ok(); c.PrivateAreaOk = false; table.Add(new KeyValuePair<string, ClaimCtx>("ward", c));
            c = Ok(); c.IsPiece = true; c.OwnerIsOnlinePeer = true;
            table.Add(new KeyValuePair<string, ClaimCtx>("online peer's piece", c));

            n++;
            foreach (var row in table)
                if (ClaimPolicy.ShouldClaim(row.Value, out why))
                    fails.Add("CombatOwnership: claimed anyway with guard '" + row.Key + "' tripped");

            // A player ZDO is refused even with every setting turned off - it is not a setting.
            n++;
            c = Ok();
            c.IsPlayerZdo = true; c.RequireWard = false; c.ExcludeVehicles = false; c.SkipOnlinePeerPieces = false;
            if (ClaimPolicy.ShouldClaim(c, out why) || ClaimPolicy.PassesCheapGates(c))
                fails.Add("CombatOwnership: a player ZDO was claimable with the guards off");

            // The switchable guards really are switchable.
            n++;
            c = Ok(); c.OnVehicle = true; c.ExcludeVehicles = false;
            if (!ClaimPolicy.ShouldClaim(c, out why)) fails.Add("CombatOwnership: ExcludeShipsAndVagons=false had no effect");
            c = Ok(); c.PrivateAreaOk = false; c.RequireWard = false;
            if (!ClaimPolicy.ShouldClaim(c, out why)) fails.Add("CombatOwnership: RequirePrivateAreaAccess=false had no effect");
            c = Ok(); c.IsPiece = true; c.OwnerIsOnlinePeer = true; c.SkipOnlinePeerPieces = false;
            if (!ClaimPolicy.ShouldClaim(c, out why)) fails.Add("CombatOwnership: SkipPiecesOwnedByOnlinePeer=false had no effect");

            // A server-owned or unowned object IS claimable: nobody's client is simulating it, and
            // the server itself hands ownership out in ReleaseNearbyZDOS (ZDOMan.cs:973).
            n++;
            c = Ok(); c.IsPiece = true; c.OwnerIsOnlinePeer = false;
            if (!ClaimPolicy.ShouldClaim(c, out why))
                fails.Add("CombatOwnership: a server-owned/unowned piece was not claimable (" + why + ")");

            return n;
        }
    }
}
