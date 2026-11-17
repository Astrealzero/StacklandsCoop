using System;
using System.Collections.Generic;
using StacklandsCoop.Protocol;

namespace StacklandsCoop.World
{
    /// <summary>One card as it travels on the wire. Positions are board-local X/Z.</summary>
    public sealed class CardRecord
    {
        public uint NetId;
        public uint ParentNetId;
        public ushort Index;
        public string CardId;
        public string BoardId;
        public float X;
        public float Z;
        public bool FaceUp;
        public bool Foil;
        public byte Damage;
        public float TimerCurrent;
        public float TimerTarget;

        /// <summary>0xFFFF means "this card has no Combatable"; otherwise the host's current HP.</summary>
        public ushort Health;

        /// <summary>Card this one is equipped on (0 = not equipped).</summary>
        public uint EquipNetId;
        /// <summary>Card this one works for (0 = not a worker).</summary>
        public uint WorkerNetId;
        /// <summary>Slot index inside the worker list, used to pick the right visual spot.</summary>
        public byte WorkerIndex;
    }

    /// <summary>Booster packs have no UniqueId, so they get their own netId range.</summary>
    public sealed class BoosterRecord
    {
        public uint NetId;
        public string BoosterId;
        public string BoardId;
        public float X;
        public float Z;
        public int TimesOpened;
    }

    /// <summary>
    /// Numbers a shop-like CardTarget keeps on itself instead of in the card stack:
    /// how much of the price has already been paid, etc. These are plain int fields the
    /// game decrements when cards are dropped on it, so without syncing them the client
    /// keeps showing the full price.
    /// </summary>
    public struct ShopRecord
    {
        public uint NetId;
        public byte Kind;  // 0 = BuyBoosterBox, 1 = Merchant, 2 = TravellingCart
        public int A;
        public int B;

        /// <summary>
        /// The host's rendered price text. Counters alone were not enough: the client recomputed
        /// the label from GetCost() and kept showing the full price, so the label itself travels
        /// (it is a few bytes and it is exactly what the player looks at).
        /// </summary>
        public string Text;

        /// <summary>
        /// Where the shop sits in the world. NetId is only valid when the shop is attached to a
        /// card; some shops are standalone targets, so the client also matches by position.
        /// </summary>
        public float X;
        public float Z;
    }

    public sealed class WorldSnapshot
    {
        public int Epoch;
        public uint Revision;
        public int Month;
        public float MonthTimer;
        public string BoardId = "main";
        public int QuestsCompleted;
        public string DefsHash = string.Empty;

        /// <summary>
        /// Host's WorldManager.SpeedUp (0 while the host paused). Timers advance at
        /// TimeScale == SpeedUp, so clients need it to animate bars at the right rate.
        /// </summary>
        public float SpeedUp = 1f;

        public List<CardRecord> Cards = new List<CardRecord>();
        public List<BoosterRecord> Boosters = new List<BoosterRecord>();

        /// <summary>
        /// NetIds of the cards that are fighting each other, one list per conflict. The
        /// client cannot run Conflict.UpdateConflictArrows (it is frozen), so it draws its
        /// own marker between the participants.
        /// </summary>
        public List<List<uint>> Conflicts = new List<List<uint>>();

        /// <summary>CardTarget counters (price paid so far) that live outside the card data.</summary>
        public List<ShopRecord> Shops = new List<ShopRecord>();
    }

    public enum ChunkKind : byte
    {
        Cards = 0,
        Boosters = 1
    }

    /// <summary>Packets for the full-snapshot sync: SyncStart / SyncChunk* / SyncEnd.</summary>
    public static class SnapshotCodec
    {
        public const int MaxCardsPerChunk = 64;
        public const int MaxBoostersPerChunk = 64;

        public static byte[] SyncStart(WorldSnapshot snap)
        {
            using (var w = new PacketWriter(MsgType.SyncStart))
            {
                w.I32(snap.Epoch);
                w.U32(snap.Revision);
                w.I32(snap.Month);
                w.F32(snap.MonthTimer);
                w.Str(snap.BoardId ?? string.Empty);
                w.I32(snap.QuestsCompleted);
                w.Str(snap.DefsHash ?? string.Empty);
                w.F32(snap.SpeedUp);
                w.I32(snap.Cards.Count);
                w.I32(snap.Boosters.Count);
                w.U16((ushort)Math.Min(65535, snap.Conflicts.Count));
                for (int i = 0; i < snap.Conflicts.Count; i++)
                {
                    List<uint> group = snap.Conflicts[i];
                    int n = Math.Min(255, group.Count);
                    w.U8((byte)n);
                    for (int j = 0; j < n; j++)
                    {
                        w.U32(group[j]);
                    }
                }

                w.U16((ushort)Math.Min(65535, snap.Shops.Count));
                for (int i = 0; i < snap.Shops.Count; i++)
                {
                    ShopRecord s = snap.Shops[i];
                    w.U32(s.NetId);
                    w.U8(s.Kind);
                    w.I32(s.A);
                    w.I32(s.B);
                    w.Str(s.Text ?? string.Empty);
                    w.F32(s.X);
                    w.F32(s.Z);
                }

                return w.ToArray();
            }
        }

        public static byte[] ChunkCards(List<CardRecord> cards, int offset, int count)
        {
            using (var w = new PacketWriter(MsgType.SyncChunk))
            {
                w.U8((byte)ChunkKind.Cards);
                w.U16((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    CardRecord c = cards[offset + i];
                    w.U32(c.NetId);
                    w.U32(c.ParentNetId);
                    w.U16(c.Index);
                    w.Str(c.CardId);
                    w.Str(c.BoardId);
                    w.F32(c.X);
                    w.F32(c.Z);
                    w.U8(c.FaceUp ? (byte)1 : (byte)0);
                    w.U8(c.Foil ? (byte)1 : (byte)0);
                    w.U8(c.Damage);
                    w.F32(c.TimerCurrent);
                    w.F32(c.TimerTarget);
                    w.U16(c.Health);
                w.U32(c.EquipNetId);
                w.U32(c.WorkerNetId);
                w.U8(c.WorkerIndex);
                }

                return w.ToArray();
            }
        }

        public static byte[] ChunkBoosters(List<BoosterRecord> boosters, int offset, int count)
        {
            using (var w = new PacketWriter(MsgType.SyncChunk))
            {
                w.U8((byte)ChunkKind.Boosters);
                w.U16((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    BoosterRecord b = boosters[offset + i];
                    w.U32(b.NetId);
                    w.Str(b.BoosterId);
                    w.Str(b.BoardId);
                    w.F32(b.X);
                    w.F32(b.Z);
                    w.U8((byte)Math.Max(0, Math.Min(255, b.TimesOpened)));
                }

                return w.ToArray();
            }
        }

        public static byte[] SyncEnd(uint revision)
        {
            using (var w = new PacketWriter(MsgType.SyncEnd))
            {
                w.U32(revision);
                return w.ToArray();
            }
        }

        /// <summary>Read the SyncStart header. Shared by ClientWorld and SelfTest.</summary>
        public static WorldSnapshot ReadStart(PacketReader r)
        {
            var snap = new WorldSnapshot
            {
                Epoch = r.I32(),
                Revision = r.U32(),
                Month = r.I32(),
                MonthTimer = r.F32(),
                BoardId = r.Str(),
                QuestsCompleted = r.I32(),
                DefsHash = r.Str(),
                SpeedUp = r.F32()
            };
            r.I32(); // card count, diagnostics only
            r.I32(); // booster count
            int groups = r.U16();
            for (int i = 0; i < groups; i++)
            {
                int n = r.U8();
                var group = new List<uint>(n);
                for (int j = 0; j < n; j++)
                {
                    group.Add(r.U32());
                }

                snap.Conflicts.Add(group);
            }

            int shops = r.U16();
            for (int i = 0; i < shops; i++)
            {
                snap.Shops.Add(new ShopRecord
                {
                    NetId = r.U32(),
                    Kind = r.U8(),
                    A = r.I32(),
                    B = r.I32(),
                    Text = r.Str(),
                    X = r.F32(),
                    Z = r.F32()
                });
            }

            return snap;
        }

        public static CardRecord ReadCard(PacketReader r)
        {
            var card = new CardRecord
            {
                NetId = r.U32(),
                ParentNetId = r.U32(),
                Index = r.U16(),
                CardId = r.Str(),
                BoardId = r.Str(),
                X = r.F32(),
                Z = r.F32()
            };
            card.FaceUp = r.U8() != 0;
            card.Foil = r.U8() != 0;
            card.Damage = r.U8();
            card.TimerCurrent = r.F32();
            card.TimerTarget = r.F32();
            card.Health = r.U16();
            card.EquipNetId = r.U32();
            card.WorkerNetId = r.U32();
            card.WorkerIndex = r.U8();
            return card;
        }

        public static BoosterRecord ReadBooster(PacketReader r)
        {
            return new BoosterRecord
            {
                NetId = r.U32(),
                BoosterId = r.Str(),
                BoardId = r.Str(),
                X = r.F32(),
                Z = r.F32(),
                TimesOpened = r.U8()
            };
        }

        /// <summary>
        /// Round-trips one fully populated snapshot through the real encode/decode path.
        /// Every field added to the wire format gets covered here, so a forgotten field or a
        /// swapped pair shows up at boot instead of as a mystery in the middle of a game.
        /// Returns null when everything matches, otherwise a short description of the mismatch.
        /// </summary>
        public static string SelfTest()
        {
            var snap = new WorldSnapshot
            {
                Epoch = 7,
                Revision = 12345,
                Month = 3,
                MonthTimer = 12.5f,
                BoardId = "forest",
                QuestsCompleted = 9,
                DefsHash = "e9068b30",
                SpeedUp = 2f
            };
            snap.Cards.Add(new CardRecord
            {
                NetId = 42,
                ParentNetId = 7,
                Index = 2,
                CardId = "berry",
                BoardId = "forest",
                X = 1.25f,
                Z = -3.5f,
                FaceUp = true,
                Foil = true,
                Damage = 1,
                TimerCurrent = 0.75f,
                TimerTarget = 4f,
                Health = 3,
                EquipNetId = 0,
                WorkerNetId = 7,
                WorkerIndex = 2
            });
            snap.Boosters.Add(new BoosterRecord
            {
                NetId = 1000001,
                BoosterId = "starter",
                BoardId = "main",
                X = 0.5f,
                Z = 0.25f,
                TimesOpened = 2
            });
            snap.Conflicts.Add(new List<uint> { 42, 43 });
            snap.Shops.Add(new ShopRecord { NetId = 42, Kind = 0, A = 3, B = 5, Text = "2 gold", X = 1.5f, Z = -2.25f });

            WorldSnapshot back;
            using (var r = new PacketReader(SyncStart(snap)))
            {
                back = ReadStart(r);
            }

            using (var r = new PacketReader(ChunkCards(snap.Cards, 0, snap.Cards.Count)))
            {
                if ((ChunkKind)r.U8() != ChunkKind.Cards)
                {
                    return "chunk kind mismatch";
                }

                int count = r.U16();
                for (int i = 0; i < count; i++)
                {
                    back.Cards.Add(ReadCard(r));
                }
            }

            using (var r = new PacketReader(ChunkBoosters(snap.Boosters, 0, snap.Boosters.Count)))
            {
                if ((ChunkKind)r.U8() != ChunkKind.Boosters)
                {
                    return "booster chunk kind mismatch";
                }

                int count = r.U16();
                for (int i = 0; i < count; i++)
                {
                    back.Boosters.Add(ReadBooster(r));
                }
            }

            if (back.Epoch != snap.Epoch || back.Revision != snap.Revision || back.Month != snap.Month ||
                Math.Abs(back.MonthTimer - snap.MonthTimer) > 0.0001f ||
                back.BoardId != snap.BoardId || back.QuestsCompleted != snap.QuestsCompleted ||
                back.DefsHash != snap.DefsHash || Math.Abs(back.SpeedUp - snap.SpeedUp) > 0.0001f)
            {
                return "header mismatch";
            }

            if (back.Cards.Count != 1 || back.Boosters.Count != 1)
            {
                return "record count mismatch";
            }

            CardRecord got = back.Cards[0];
            CardRecord want = snap.Cards[0];
            if (got.NetId != want.NetId || got.ParentNetId != want.ParentNetId || got.Index != want.Index ||
                got.CardId != want.CardId || got.BoardId != want.BoardId ||
                Math.Abs(got.X - want.X) > 0.0001f || Math.Abs(got.Z - want.Z) > 0.0001f ||
                got.FaceUp != want.FaceUp || got.Foil != want.Foil || got.Damage != want.Damage ||
                Math.Abs(got.TimerCurrent - want.TimerCurrent) > 0.0001f ||
                Math.Abs(got.TimerTarget - want.TimerTarget) > 0.0001f || got.Health != want.Health ||
                got.EquipNetId != want.EquipNetId || got.WorkerNetId != want.WorkerNetId || got.WorkerIndex != want.WorkerIndex)
            {
                return "card record mismatch";
            }

            BoosterRecord bGot = back.Boosters[0];
            BoosterRecord bWant = snap.Boosters[0];
            if (bGot.NetId != bWant.NetId || bGot.BoosterId != bWant.BoosterId || bGot.BoardId != bWant.BoardId ||
                Math.Abs(bGot.X - bWant.X) > 0.0001f || Math.Abs(bGot.Z - bWant.Z) > 0.0001f ||
                bGot.TimesOpened != bWant.TimesOpened)
            {
                return "booster record mismatch";
            }

            if (back.Conflicts.Count != 1 || back.Conflicts[0].Count != 2 ||
                back.Conflicts[0][0] != 42 || back.Conflicts[0][1] != 43)
            {
                return "conflict mismatch";
            }

            if (back.Shops.Count != 1 || back.Shops[0].NetId != 42 || back.Shops[0].Kind != 0 ||
                back.Shops[0].A != 3 || back.Shops[0].B != 5 || back.Shops[0].Text != "2 gold" ||
                Math.Abs(back.Shops[0].X - 1.5f) > 0.001f || Math.Abs(back.Shops[0].Z + 2.25f) > 0.001f)
            {
                return "shop mismatch";
            }

            return null;
        }
    }
}
