using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerroritySmellatory.Tests
{
    // 0.4.0: a territory list too big for one message goes in pieces on the same per-peer ZRpc (Net/SnapshotParts.cs), the way
    // Better Continents' BakedTransfer sends a world's maps. Steam refuses a reliable message over 512 KiB. Everything here is the
    // shipping code: the sender, the assembler and the real snapshot writer and reader. What the game adds - the socket's queue, one
    // piece a frame - is Net/TerritoryServer.cs's PumpParts, and the end-to-end rig runs it with a piece size of a few kilobytes.
    internal static partial class Program
    {
        // A package's own header: protocol, id, total, offset, hash, and the length of the bytes (six ints).
        private const int PartHeaderBytes = 24;

        static partial void PartsGroups()
        {
            Run("parts: a list in pieces comes back whole - every size of list, every size of piece", PartsRoundTrip);
            Run("parts: a real snapshot over the 512 KiB limit goes in pieces and reads back as the same territories", PartsRealSnapshot);
            Run("parts: stored records keep a continent's members off the wire once per town", PartsStoredIsSmall);
            Run("parts: order, strangers, a newer list, a changed list, a bad hash, and packages that are not pieces", PartsRefusals);
            Run("parts: a server and a client over one ordered queue, random lists replacing each other mid-transfer: never an older list after a newer, always the last one", PartsSimulation);
            Run("parts: a list that stops coming is dropped; Clear and Expire", PartsExpire);
            Run("parts: the sender hands out exactly the list, once", PartsSender);
        }

        private static byte[] PartsBytesOf(int length, int seed)
        {
            var rng = new Random(seed);
            var data = new byte[length];
            rng.NextBytes(data);
            return data;
        }

        /// <summary>All the pieces of a list as packages, as the sender makes them.</summary>
        private static List<ZPackage> PartsOf(byte[] data, int partBytes, int id = 1)
        {
            var sender = new SnapshotParts.Sender(id, data);
            var pieces = new List<ZPackage>();
            for (ZPackage p = sender.Next(partBytes); p != null; p = sender.Next(partBytes)) pieces.Add(p);
            return pieces;
        }

        private static void PartsRoundTrip()
        {
            int failures = 0, runs = 0, tooBig = 0, wrongCount = 0;
            foreach (int total in new[] { 1, 2, 63, 64, 65, 1000, 4096, 4097, 100000 })
            {
                foreach (int partBytes in new[] { 1, 7, 64, 1000, 4096, 70000, 1 << 20 })
                {
                    if (total / partBytes > 20000) continue;   // a byte a piece of 100 000 bytes is not worth the time
                    byte[] data = PartsBytesOf(total, total * 31 + partBytes);
                    List<ZPackage> pieces = PartsOf(data, partBytes, id: runs + 1);
                    var assembler = new SnapshotParts.Assembler();
                    ZPackage whole = null;
                    string problem = null;
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        ZPackage got = assembler.Add(pieces[i].Transmit(), i, out string p);
                        if (p != null) problem = p;
                        if (i < pieces.Count - 1 && got != null) failures++;
                        if (i == pieces.Count - 1) whole = got;
                        if (pieces[i].GetArray().Length > partBytes + PartHeaderBytes) tooBig++;
                    }
                    runs++;
                    if (pieces.Count != SnapshotParts.PartCount(total, partBytes)) wrongCount++;
                    if (problem != null || whole == null || assembler.Pending || assembler.Parts != pieces.Count || !whole.GetArray().SequenceEqual(data)) failures++;
                }
            }
            Check(runs > 40 && failures == 0, $"{runs} lists of every size in every size of piece come back byte for byte, with no refusal, and the last piece alone completes ({failures} wrong)");
            Check(tooBig == 0, $"no piece is larger than its bytes and a {PartHeaderBytes}-byte header ({tooBig} were)");
            Check(wrongCount == 0, "PartCount says how many pieces there are, always");
            Check(SnapshotParts.PartCount(0, 10) == 0 && SnapshotParts.PartCount(-5, 10) == 0 && SnapshotParts.PartCount(10, 0) == 10 && SnapshotParts.PartCount(int.MaxValue, 1) == int.MaxValue,
                "(PartCount of nothing is none, and a piece size under one is one)");
        }

        private static List<Territory> PartsManyWithMembers(int territories, int members)
        {
            var list = new List<Territory>();
            for (int i = 0; i < territories; i++)
            {
                Territory t = Circle(i + 1, i * 40f, 0f, 15f, TerritoryFlags.BuildProtect | TerritoryFlags.Announce);
                t.Name = "Territory number " + (i + 1);
                t.OwnerId = 1000L + i;
                t.OwnerName = "Owner " + i;
                for (int m = 0; m < members; m++) t.Members.Add(new TerritoryMember(5000L + m, "Member " + m));
                list.Add(t);
            }
            return list;
        }

        private static void PartsRealSnapshot()
        {
            // 600 territories of 128 members each: well over the limit of one Steam message, and no list that real.
            List<Territory> list = PartsManyWithMembers(600, 128);
            var roster = new List<TerritoryMember> { new TerritoryMember(1L, "Admin") };
            byte[] whole = Protocol.WriteSnapshot(77L, 5, true, false, list, roster).GetArray();
            Check(whole.Length > 512 * 1024, $"the list is {whole.Length / 1024} KiB, over the 512 KiB one Steam message may be");

            List<ZPackage> pieces = PartsOf(whole, Protocol.PartBytes);
            Check(pieces.Count > 8 && pieces.All(p => p.GetArray().Length <= Protocol.PartBytes + PartHeaderBytes), $"it is {pieces.Count} pieces, none over {Protocol.PartBytes / 1024} KiB");

            var assembler = new SnapshotParts.Assembler();
            ZPackage joined = null;
            foreach (ZPackage piece in pieces) joined = assembler.Add(piece.Transmit(), 0f, out string _) ?? joined;
            Check(joined != null, "and the last piece completes it");
            Protocol.Snapshot read = Protocol.ReadSnapshot(joined);
            Check(read.Revision == 5 && read.WorldUid == 77L && read.YouAreAdmin && read.Territories.Count == 600 && read.OnlinePlayers.Count == 1, "which reads as the same snapshot");
            Territory last = read.Territories[599];
            Check(last.Members.Count == 128 && last.Members[127].Name == "Member 127" && last.OwnerName == "Owner 599" && last.Name == "Territory number 600", "down to the last member of the last territory");

            // A list that fits one piece is not split at all.
            byte[] small = Protocol.WriteSnapshot(77L, 5, false, false, PartsManyWithMembers(3, 3), null).GetArray();
            Check(small.Length <= Protocol.PartBytes, $"a small list ({small.Length} bytes) is under the piece size, so it travels as the one message it always was");
            Check(Protocol.PartBytes == 64 * 1024 && Protocol.PartBytes < 512 * 1024, "the piece size is 64 KiB, far under Steam's limit");
            Check(Protocol.RpcSnapshotPart == "TerroritySmellatory_SnapshotPart" && Protocol.RpcSnapshotPart != Protocol.RpcSnapshot && Protocol.RpcSnapshotPart != Protocol.RpcRequest && Protocol.RpcSnapshotPart != Protocol.RpcResult,
                "a piece has its own RPC name, and it is not another message's");
        }

        private static void PartsStoredIsSmall()
        {
            // The reviewer's worry: 150 towns that follow a continent with 128 members. The wire carries the STORED records, so the
            // continent's members are in the list once; effective records would have copied them into every town.
            var stored = new List<Territory>();
            Territory continent = Continent(1, 5000f);
            continent.Members.Clear();
            for (int m = 0; m < 128; m++) continent.Members.Add(new TerritoryMember(9000L + m, "Kin number " + m));
            stored.Add(continent);
            for (int i = 0; i < 150; i++) stored.Add(Sub(2 + i, 1, 100f + i * 20f, 0f, 8f, "Town " + i));

            int storedBytes = Protocol.WriteSnapshot(1L, 1, false, false, stored, null).GetArray().Length;
            int effectiveBytes = Protocol.WriteSnapshot(1L, 1, false, false, Inheritance.Resolve(stored), null).GetArray().Length;
            Check(effectiveBytes > 300 * 1024, $"(the effective records are {effectiveBytes / 1024} KiB: every town carries the continent's members)");
            Check(storedBytes < Protocol.PartBytes, $"the stored records are {storedBytes / 1024} KiB: under one piece, so one message");

            // And a client given the stored list gets the effective one back with the same code.
            Protocol.Snapshot read = Protocol.ReadSnapshot(Protocol.WriteSnapshot(1L, 1, false, false, stored, null).Transmit());
            List<Territory> resolved = Inheritance.Resolve(read.Territories);
            Check(resolved.Count == 151 && resolved[1].Members.Count == 128 && resolved[150].Members[127].Name == "Kin number 127" && resolved[1].PvP == continent.PvP,
                "a client that resolves the stored list has every town's 128 members all the same");
        }

        private static void PartsRefusals()
        {
            byte[] data = PartsBytesOf(1000, 5);
            List<ZPackage> a = PartsOf(data, 300, id: 1);   // 4 pieces: 300 300 300 100
            Check(a.Count == 4, "(a list of 1000 bytes at 300 is four pieces)");

            // Out of order: the second piece before the first is not the start; the third in place of the second spoils the list.
            var asm = new SnapshotParts.Assembler();
            Check(asm.Add(a[1].Transmit(), 0f, out string p1) == null && p1 != null && !asm.Pending, "a piece before the first is refused, and nothing is gathered");
            asm.Add(a[0].Transmit(), 0f, out _);
            Check(asm.Pending && asm.Add(a[2].Transmit(), 0f, out string p2) == null && p2 != null && !asm.Pending, "a piece that skips one drops the list gathered so far");
            Check(asm.Add(a[1].Transmit(), 0f, out string p2b) == null && p2b != null, "and the pieces that follow are refused until a list starts again");

            // A list that starts again from its first piece completes.
            ZPackage again = null;
            foreach (ZPackage piece in PartsOf(data, 300, id: 2)) again = asm.Add(piece.Transmit(), 0f, out _) ?? again;
            Check(again != null && again.GetArray().SequenceEqual(data), "a list that starts again from its first piece completes");

            // A duplicate piece in the middle.
            asm = new SnapshotParts.Assembler();
            asm.Add(a[0].Transmit(), 0f, out _);
            asm.Add(a[1].Transmit(), 0f, out _);
            Check(asm.Add(a[1].Transmit(), 0f, out string pd) == null && pd != null && !asm.Pending, "a piece sent twice drops the list: it is not the one that was next");

            // A newer list replaces one that is half gathered; the old one's later pieces are strangers.
            asm = new SnapshotParts.Assembler();
            asm.Add(a[0].Transmit(), 0f, out _);
            asm.Add(a[1].Transmit(), 0f, out _);
            byte[] newer = PartsBytesOf(700, 9);
            ZPackage got = null;
            string newerProblem = null;
            foreach (ZPackage piece in PartsOf(newer, 300, id: 2))
            {
                got = asm.Add(piece.Transmit(), 0f, out string np) ?? got;
                newerProblem = np ?? newerProblem;
            }
            Check(newerProblem == null && got != null && got.GetArray().SequenceEqual(newer), "a newer list's first piece replaces the half-gathered one, and the newer completes");
            Check(asm.Add(a[2].Transmit(), 0f, out string late) == null && late != null && !asm.Pending, "the old list's late pieces are refused");

            // A stranger in the middle of a list (another list's piece that is not a start) is ignored; the list goes on.
            asm = new SnapshotParts.Assembler();
            asm.Add(a[0].Transmit(), 0f, out _);
            List<ZPackage> other = PartsOf(PartsBytesOf(900, 3), 300, id: 7);
            Check(asm.Add(other[1].Transmit(), 0f, out string stray) == null && stray != null && asm.Pending, "a stray piece of another list is refused and the list being gathered is kept");
            ZPackage done = null;
            for (int i = 1; i < a.Count; i++) done = asm.Add(a[i].Transmit(), 0f, out _) ?? done;
            Check(done != null && done.GetArray().SequenceEqual(data), "and it completes");

            // The same list's pieces that disagree about the whole: a different total, a different hash.
            asm = new SnapshotParts.Assembler();
            asm.Add(a[0].Transmit(), 0f, out _);
            ZPackage wrongTotal = MakePart(1, 1001, 300, SnapshotParts.Hash(data), PartsSlice(data, 300, 300));
            Check(asm.Add(wrongTotal, 0f, out string pt) == null && pt != null && !asm.Pending, "a piece that gives another total length than the first drops the list");
            asm = new SnapshotParts.Assembler();
            asm.Add(a[0].Transmit(), 0f, out _);
            ZPackage wrongHash = MakePart(1, 1000, 300, SnapshotParts.Hash(data) ^ 1, PartsSlice(data, 300, 300));
            Check(asm.Add(wrongHash, 0f, out string ph) == null && ph != null && !asm.Pending, "a piece with another hash than the first drops the list");

            // A byte changed: the list comes together and does not match its hash.
            byte[] spoilt = (byte[])data.Clone();
            spoilt[500] ^= 0x40;
            asm = new SnapshotParts.Assembler();
            ZPackage result = null;
            string spoiltProblem = null;
            var withBadByte = new List<ZPackage>();
            var sender = new SnapshotParts.Sender(3, data);
            for (ZPackage p = sender.Next(300); p != null; p = sender.Next(300)) withBadByte.Add(p);
            // The sender hashes the good list; the third piece carries the spoilt bytes.
            withBadByte[1] = MakePart(3, 1000, 300, SnapshotParts.Hash(data), PartsSlice(spoilt, 300, 300));
            foreach (ZPackage piece in withBadByte)
            {
                result = asm.Add(piece.Transmit(), 0f, out string sp) ?? result;
                spoiltProblem = sp ?? spoiltProblem;
            }
            Check(result == null && spoiltProblem != null && spoiltProblem.Contains("hash") && !asm.Pending, "a list that does not match its hash when it is whole is refused, not handed on");

            // Packages that are not pieces at all.
            asm = new SnapshotParts.Assembler();
            Check(asm.Add(new ZPackage(), 0f, out string empty) == null && empty != null, "an empty package is refused");
            var truncated = new ZPackage();
            truncated.Write(2);
            truncated.Write(1);
            Check(asm.Add(truncated.Transmit(), 0f, out string trunc) == null && trunc != null, "a package cut off in its header is refused, not thrown");
            Check(asm.Add(MakePart(1, 1000, 0, 5, new byte[10], protocol: 0), 0f, out string zeroProto) == null && zeroProto != null, "protocol 0 is refused");
            Check(asm.Add(MakePart(1, 0, 0, 5, new byte[10]), 0f, out string zeroTotal) == null && zeroTotal != null, "a list of no bytes is refused");
            Check(asm.Add(MakePart(1, SnapshotParts.MaxTotalBytes + 1, 0, 5, new byte[10]), 0f, out string huge) == null && huge != null && !asm.Pending, "a list claiming more than the cap is refused before anything is allocated");
            Check(asm.Add(MakePart(1, 1000, -4, 5, new byte[10]), 0f, out string negative) == null && negative != null, "a negative start is refused");
            Check(asm.Add(MakePart(1, 1000, 995, 5, new byte[10]), 0f, out string overrun) == null && overrun != null, "a piece that runs past the end of the list is refused");
            Check(asm.Add(MakePart(1, 1000, 0, 5, new byte[0]), 0f, out string nothing) == null && nothing != null, "a piece with no bytes is refused");
            Check(!asm.Pending, "none of them left a half-gathered list behind");
        }

        /// <summary>
        /// The server's rule (a list that fits one piece is one whole message and replaces any list still going out; a bigger one starts a new
        /// transfer that replaces any list still going out, one piece at a time) and the client's (whole messages are applied, pieces are
        /// assembled), over ONE ordered queue like a reliable connection, with the server making new lists and pumping and the client
        /// receiving in random order of those moves. A newer list must never be followed by an older one, every list applied must be exactly
        /// what the server made, and once the server has finished the last list the client holds exactly that.
        /// </summary>
        private static void PartsSimulation()
        {
            int scenarios = 600, wrong = 0, stale = 0, applied = 0, converged = 0, withParts = 0;
            for (int seed = 0; seed < scenarios; seed++)
            {
                var rng = new Random(seed);
                int partBytes = rng.Next(40, 400);
                var queue = new Queue<KeyValuePair<bool, ZPackage>>();
                SnapshotParts.Sender outgoing = null;
                var lists = new Dictionary<int, byte[]>();
                int rev = 0, nextId = 1, lastApplied = 0;
                bool parted = false;
                var asm = new SnapshotParts.Assembler();

                void Apply(byte[] bytes)
                {
                    int r = BitConverter.ToInt32(bytes, 0);
                    if (r < lastApplied) stale++; else lastApplied = r;
                    if (!lists.TryGetValue(r, out byte[] sent) || !bytes.SequenceEqual(sent)) wrong++;
                    applied++;
                }
                void Deliver()
                {
                    if (queue.Count == 0) return;
                    KeyValuePair<bool, ZPackage> item = queue.Dequeue();
                    if (item.Key) { Apply(item.Value.GetArray()); return; }
                    ZPackage whole = asm.Add(item.Value.Transmit(), 0f, out string _);
                    if (whole != null) Apply(whole.GetArray());
                }
                void Pump()
                {
                    if (outgoing == null) return;
                    queue.Enqueue(new KeyValuePair<bool, ZPackage>(false, outgoing.Next(partBytes)));
                    if (outgoing.Done) outgoing = null;
                }

                for (int step = 0; step < 90; step++)
                {
                    switch (rng.Next(5))
                    {
                        case 0:
                            rev++;
                            int size = rng.Next(8, partBytes * 6);
                            var data = new byte[size];
                            rng.NextBytes(data);
                            BitConverter.GetBytes(rev).CopyTo(data, 0);
                            lists[rev] = data;
                            if (size <= partBytes)
                            {
                                outgoing = null;
                                queue.Enqueue(new KeyValuePair<bool, ZPackage>(true, new ZPackage(data)));
                            }
                            else
                            {
                                outgoing = new SnapshotParts.Sender(nextId++, data);
                                parted = true;
                            }
                            break;
                        case 1:
                        case 2:
                            Pump();
                            break;
                        default:
                            Deliver();
                            break;
                    }
                }
                while (outgoing != null) Pump();
                while (queue.Count > 0) Deliver();
                if (parted) withParts++;
                if (rev == 0 || lastApplied == rev) converged++;
            }
            Check(applied > 1000 && withParts > scenarios / 2, $"{scenarios} random runs applied {applied} lists, {withParts} of them with lists in pieces");
            Check(stale == 0, $"an older list was applied after a newer one {stale} times");
            Check(wrong == 0, $"a list that was applied differed from what the server made {wrong} times");
            Check(converged == scenarios, $"after the server finished its last list the client held it in {converged} of {scenarios} runs");
        }

        private static byte[] PartsSlice(byte[] data, int offset, int length)
        {
            var piece = new byte[Math.Min(length, data.Length - offset)];
            Buffer.BlockCopy(data, offset, piece, 0, piece.Length);
            return piece;
        }

        private static ZPackage MakePart(int id, int total, int offset, int hash, byte[] bytes, int protocol = 2)
        {
            var part = new ZPackage();
            part.Write(protocol);
            part.Write(id);
            part.Write(total);
            part.Write(offset);
            part.Write(hash);
            part.Write(bytes);
            return part.Transmit();
        }

        private static void PartsExpire()
        {
            byte[] data = PartsBytesOf(900, 11);
            List<ZPackage> pieces = PartsOf(data, 300);
            var asm = new SnapshotParts.Assembler();
            Check(!asm.Expire(1000f, 10f), "nothing gathered: nothing expires");
            asm.Add(pieces[0].Transmit(), 100f, out _);
            Check(!asm.Expire(105f, 10f) && asm.Pending, "five seconds without a piece is within ten");
            asm.Add(pieces[1].Transmit(), 108f, out _);
            Check(!asm.Expire(115f, 10f), "a piece restarts the clock");
            Check(asm.Expire(119f, 10f) && !asm.Pending, "eleven seconds after the last piece the list is dropped");
            Check(asm.Add(pieces[2].Transmit(), 120f, out string late) == null && late != null, "and a piece that arrives after that has nothing to join");

            asm.Add(pieces[0].Transmit(), 200f, out _);
            asm.Clear();
            Check(!asm.Pending && !asm.Expire(900f, 1f), "Clear drops what is gathered");
        }

        private static void PartsSender()
        {
            byte[] data = PartsBytesOf(250, 21);
            var sender = new SnapshotParts.Sender(9, data);
            Check(sender.Id == 9 && sender.Total == 250 && sender.Offset == 0 && !sender.Done && sender.Hash == SnapshotParts.Hash(data), "a sender knows its list's id, length and hash");
            int count = 0, bytes = 0;
            for (ZPackage p = sender.Next(100); p != null; p = sender.Next(100))
            {
                count++;
                bytes += p.GetArray().Length - PartHeaderBytes;
            }
            Check(count == 3 && bytes == 250 && sender.Done && sender.Offset == 250 && sender.Next(100) == null, "it hands out three pieces of 100, 100 and 50 bytes, and then nothing");

            var empty = new SnapshotParts.Sender(1, new byte[0]);
            Check(empty.Done && empty.Next(100) == null && empty.Total == 0, "a list of nothing is done at once");
            var nullList = new SnapshotParts.Sender(1, null);
            Check(nullList.Done && nullList.Total == 0, "(and so is no list at all)");

            var tiny = new SnapshotParts.Sender(2, new byte[] { 1, 2, 3 });
            Check(tiny.Next(0) != null && tiny.Next(-7) != null && tiny.Next(0) != null && tiny.Next(0) == null, "a piece size under one is taken as one");

            Check(SnapshotParts.Hash(new byte[0]) == unchecked((int)2166136261u) && SnapshotParts.Hash(new byte[] { 1 }) != SnapshotParts.Hash(new byte[] { 2 }) &&
                  SnapshotParts.Hash(new byte[] { 1, 2 }) != SnapshotParts.Hash(new byte[] { 2, 1 }), "the hash is FNV-1a: it sees the bytes and their order");
        }
    }
}
