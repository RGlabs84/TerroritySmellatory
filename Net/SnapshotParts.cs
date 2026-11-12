using System;

namespace TerroritySmellatory
{
    /// <summary>
    /// The territory list in pieces, for the day it is too big for one message (0.4.0).
    /// </summary>
    /// <remarks>
    /// Steam refuses a reliable message over 512 KiB and then keeps retrying it at the head of the connection's queue, so the
    /// player is dropped about half a minute later (libs-Tools/ZDO-WIRE-LIMITS-FACTS.md). ZRpc is no way round it: a ZRpc call IS one
    /// socket message. The way round is the one Better Continents' BakedTransfer takes for a world's image maps: the same peer's
    /// own ZRpc, one RPC name for a piece, and a sender that puts one piece a frame into the socket while less than two are waiting
    /// there (Net/TerritoryServer.cs, PumpParts), so a big list never blocks the other traffic behind it. A list that fits one piece
    /// still goes as the one message it always was.
    ///
    /// A piece carries: the protocol, the transfer's id, the whole list's length, where this piece starts, a hash of the WHOLE
    /// list, and its bytes. The transport is reliable and ordered, so the receiver insists on order and has no resend: a piece that
    /// is not the next one, a different list's piece in the middle of an assembly, a hash that does not match at the end, or a list
    /// that stops coming (<see cref="Assembler.Expire"/>) drops what was gathered, says so, and waits for the next list the server
    /// sends - which it does whenever anything changes. A new transfer's first piece (offset 0, a new id) replaces an unfinished
    /// one: that is how the sender abandons a list that went out of date while it was still being sent.
    ///
    /// Pure (a ZPackage and some integers, no game state), so the harness runs the shipping code.
    /// </remarks>
    internal static class SnapshotParts
    {
        /// <summary>The most a list may claim to be: what a client will allocate for one (the real worlds are well under 1 MiB).</summary>
        internal const int MaxTotalBytes = 64 * 1024 * 1024;

        /// <summary>FNV-1a over the bytes. Not for security: a check that the pieces were put back together in the right order.</summary>
        internal static int Hash(byte[] data)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < data.Length; i++) h = (h ^ data[i]) * 16777619u;
                return (int)h;
            }
        }

        /// <summary>How many pieces a list of <paramref name="total"/> bytes makes at <paramref name="partBytes"/> a piece.</summary>
        internal static int PartCount(int total, int partBytes) => total <= 0 ? 0 : (int)(((long)total + Math.Max(partBytes, 1) - 1) / Math.Max(partBytes, 1));

        /// <summary>The pieces of one list, handed out in order.</summary>
        internal sealed class Sender
        {
            private readonly byte[] _data;

            public Sender(int id, byte[] data)
            {
                _data = data ?? new byte[0];
                Id = id;
                Hash = SnapshotParts.Hash(_data);
            }

            /// <summary>Told apart from the other lists this server sent: a new list gets a new id.</summary>
            public int Id { get; }

            public int Hash { get; }

            public int Total => _data.Length;

            /// <summary>Bytes handed out so far.</summary>
            public int Offset { get; private set; }

            public bool Done => Offset >= _data.Length;

            /// <summary>The next piece, up to <paramref name="partBytes"/> of the list, as the package one ZRpc call carries; null once all have gone.</summary>
            public ZPackage Next(int partBytes)
            {
                if (Done) return null;
                int n = Math.Min(Math.Max(partBytes, 1), _data.Length - Offset);
                var bytes = new byte[n];
                Buffer.BlockCopy(_data, Offset, bytes, 0, n);

                var part = new ZPackage();
                part.Write(Protocol.Version);
                part.Write(Id);
                part.Write(_data.Length);
                part.Write(Offset);
                part.Write(Hash);
                part.Write(bytes);
                Offset += n;
                return part;
            }
        }

        /// <summary>The list being put back together on the receiving side.</summary>
        internal sealed class Assembler
        {
            private int _id;
            private int _total;
            private int _hash;
            private int _received;
            private byte[] _buffer;
            private float _lastAt;

            /// <summary>A list is partly here.</summary>
            public bool Pending => _buffer != null;

            /// <summary>Pieces gathered of the list being assembled; after a list completes, how many it took.</summary>
            public int Parts { get; private set; }

            public void Clear()
            {
                _buffer = null;
                _received = 0;
                _total = 0;
            }

            /// <summary>
            /// Takes one piece. Returns the whole list as a package, positioned at its start, when this piece was its last; otherwise
            /// null, with <paramref name="problem"/> set when the piece was refused (and, if that spoilt the list being gathered,
            /// that list dropped). Never throws: a package that is not a piece at all is a problem like any other.
            /// </summary>
            public ZPackage Add(ZPackage part, float now, out string problem)
            {
                problem = null;
                int id, total, offset, hash;
                byte[] bytes;
                try
                {
                    int protocol = part.ReadInt();
                    if (protocol < 1) { problem = "not a piece of a territory list"; return null; }
                    id = part.ReadInt();
                    total = part.ReadInt();
                    offset = part.ReadInt();
                    hash = part.ReadInt();
                    bytes = part.ReadByteArray();
                }
                catch (Exception ex)
                {
                    problem = $"a piece that cannot be read ({ex.GetType().Name})";
                    Clear();
                    return null;
                }

                if (total <= 0 || total > MaxTotalBytes) { problem = $"a list that claims to be {total} bytes"; Clear(); return null; }
                if (offset < 0 || bytes == null || bytes.Length == 0 || (long)offset + bytes.Length > total)
                {
                    problem = $"a piece at {offset} of {total} with {(bytes == null ? -1 : bytes.Length)} bytes";
                    Clear();
                    return null;
                }

                if (Pending && id != _id)
                {
                    if (offset != 0)
                    {
                        // The rest of a list that is no longer the one being gathered, or a start that never came: nothing here to join it to.
                        problem = $"a piece of list {id} at {offset} while list {_id} is being gathered";
                        return null;
                    }
                    Clear();   // the sender gave the old list up for a newer one
                }

                if (!Pending)
                {
                    if (offset != 0) { problem = $"a piece of list {id} at {offset}, and its start never came"; return null; }
                    _id = id;
                    _total = total;
                    _hash = hash;
                    _received = 0;
                    _buffer = new byte[total];
                    Parts = 0;
                }
                else if (total != _total || hash != _hash)
                {
                    problem = $"a piece of list {id} that says {total} bytes / hash {hash}, where it began with {_total} / {_hash}";
                    Clear();
                    return null;
                }

                if (offset != _received)
                {
                    problem = $"a piece of list {id} at {offset}, where {_received} was next";
                    Clear();
                    return null;
                }

                Buffer.BlockCopy(bytes, 0, _buffer, _received, bytes.Length);
                _received += bytes.Length;
                _lastAt = now;
                Parts++;
                if (_received < _total) return null;

                byte[] whole = _buffer;
                Clear();
                if (SnapshotParts.Hash(whole) != hash)
                {
                    problem = $"list {id} put together does not match its hash";
                    return null;
                }
                return new ZPackage(whole);
            }

            /// <summary>Drops a list that has been partly here for longer than <paramref name="seconds"/> without a piece. True when it did.</summary>
            public bool Expire(float now, float seconds)
            {
                if (!Pending || now - _lastAt <= seconds) return false;
                Clear();
                return true;
            }
        }
    }
}
