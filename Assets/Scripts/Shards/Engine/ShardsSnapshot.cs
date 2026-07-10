using System.Collections.Generic;
using Pascension.Core;

namespace Shards.Engine
{
    public sealed class ShardsCardSnap
    {
        public int InstanceId;
        public string DefId;
        public bool Exhausted;
    }

    public sealed class ShardsPlayerSnap
    {
        public int Index;
        public string Name;
        public string CharacterId;
        public int Health;
        public int Mastery;
        public int Gems;
        public int Power;
        public bool CharacterExhausted;
        public bool FocusedThisTurn;
        public bool Eliminated;
        public bool RelicRecruited;
        public string DestinyId;

        public int DeckCount;
        public int HandCount;
        /// <summary>Populated only for the viewer.</summary>
        public List<ShardsCardSnap> Hand;
        /// <summary>Public zones.</summary>
        public List<ShardsCardSnap> Discard = new();
        public List<ShardsCardSnap> PlayZone = new();
        public List<ShardsCardSnap> Champions = new();
        /// <summary>Viewer-only: unearned relics/destinies.</summary>
        public List<ShardsCardSnap> SetAside;
    }

    /// <summary>Per-viewer masked view: hands and deck orders hidden for others.</summary>
    public sealed class ShardsSnapshot : SnapshotBase
    {
        public int TurnPlayerIndex;
        public int Round;
        public int Dlc;
        public bool GameOver;
        public int WinnerIndex;

        public int CenterDeckCount;
        public List<ShardsCardSnap> CenterRow = new();
        public List<ShardsPlayerSnap> Players = new();
        public PendingSnapInfo Pending;

        public sealed class PendingSnapInfo
        {
            public int Kind;
            public int PlayerIndex;
        }
    }

    public static class ShardsSnapshotBuilder
    {
        public static ShardsSnapshot Build(ShardsEngine engine, int viewerIndex)
        {
            var state = engine.State;
            var snapshot = new ShardsSnapshot
            {
                ViewerIndex = viewerIndex,
                EventSeq = engine.Log.Count,
                TurnPlayerIndex = state.TurnPlayerIndex,
                Round = state.Round,
                Dlc = (int)state.Dlc,
                GameOver = state.GameOver,
                WinnerIndex = state.WinnerIndex,
                CenterDeckCount = state.CenterDeck.Count
            };

            foreach (var card in state.CenterRow)
                snapshot.CenterRow.Add(card == null ? null : Snap(card));

            foreach (var player in state.Players)
            {
                var snap = new ShardsPlayerSnap
                {
                    Index = player.Index,
                    Name = player.Name,
                    CharacterId = player.CharacterId,
                    Health = player.Health,
                    Mastery = player.Mastery,
                    Gems = player.Gems,
                    Power = player.Power,
                    CharacterExhausted = player.CharacterExhausted,
                    FocusedThisTurn = player.FocusedThisTurn,
                    Eliminated = player.Eliminated,
                    RelicRecruited = player.RelicRecruited,
                    DestinyId = player.DestinyId,
                    DeckCount = player.Deck.Count,
                    HandCount = player.Hand.Count
                };
                foreach (var card in player.Discard) snap.Discard.Add(Snap(card));
                foreach (var card in player.PlayZone) snap.PlayZone.Add(Snap(card));
                foreach (var card in player.Champions) snap.Champions.Add(Snap(card));
                if (player.Index == viewerIndex)
                {
                    snap.Hand = new List<ShardsCardSnap>();
                    foreach (var card in player.Hand) snap.Hand.Add(Snap(card));
                    snap.SetAside = new List<ShardsCardSnap>();
                    foreach (var card in player.SetAside) snap.SetAside.Add(Snap(card));
                }
                snapshot.Players.Add(snap);
            }

            var pending = engine.PendingInput;
            if (pending != null)
                snapshot.Pending = new ShardsSnapshot.PendingSnapInfo
                {
                    Kind = (int)pending.Kind,
                    PlayerIndex = pending.PlayerIndex
                };
            return snapshot;
        }

        private static ShardsCardSnap Snap(ShardsCard card) => new()
        {
            InstanceId = card.InstanceId,
            DefId = card.DefId,
            Exhausted = card.Exhausted
        };
    }
}
