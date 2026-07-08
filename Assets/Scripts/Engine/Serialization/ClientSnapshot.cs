using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;

namespace Pascension.Engine.Serialization
{
    /// <summary>A card as one player may see it. DefId is null for hidden cards.</summary>
    public sealed class CardSnap
    {
        public int InstanceId;
        public string DefId;
        public bool Tapped;
        public int MarkedDamage;
    }

    public sealed class PlayerSnap
    {
        public int Index;
        public string Name;
        public string HeroId;
        public int Level;
        public int Xp;
        public int Position;
        public int LastInnCheckpoint;
        public int Ap;
        public int DamagePool;
        public bool Conceded;
        public bool HeroActiveUsed;
        public bool HeroUltimateUsed;

        public int DeckCount;
        public int HandCount;
        /// <summary>Populated only for the viewer's own seat.</summary>
        public List<CardSnap> Hand = new();
        /// <summary>Discard/exile/played/relics/equipment are open information.</summary>
        public List<CardSnap> Discard = new();
        public List<CardSnap> Exile = new();
        public List<CardSnap> PlayedThisTurn = new();
        public List<CardSnap> Relics = new();
        public CardSnap[] Equipment = new CardSnap[3];
    }

    public sealed class StackItemSnap
    {
        public int Id;
        public string Kind;
        public int ControllerIndex;
        public string DefId;
        public string Description;
        public int Amount;
        public List<Targeting.TargetRef> Targets = new();
    }

    public sealed class PendingSnap
    {
        public PendingInputKind Kind;
        public int PlayerIndex;
        /// <summary>Populated only when the viewer is the pending player.</summary>
        public List<PlayerAction> LegalActions;
        /// <summary>Populated only when the viewer is the pending player.</summary>
        public DecisionRequest Decision;
    }

    /// <summary>Full masked view for one player: UI rebuild, network join, reconnection.</summary>
    public sealed class ClientSnapshot
    {
        public int ViewerIndex;
        /// <summary>Event-log length when the snapshot was taken (clients resync gaps from here).</summary>
        public int EventSeq;

        public int TurnPlayerIndex;
        public Phase Phase;
        public int Round;
        public bool GameOver;
        public int WinnerIndex;

        public List<PlayerSnap> Players = new();
        /// <summary>Face-up market rows [tier][slot]; null entries are empty slots.</summary>
        public CardSnap[][] MarketRows = new CardSnap[3][];
        public int[] PileCounts = new int[3];
        public CardSnap Boss;
        public int BossHp;
        public List<StackItemSnap> Stack = new();
        public PendingSnap Pending;
    }

    public static class SnapshotBuilder
    {
        public static ClientSnapshot Build(GameEngine engine, int viewerIndex)
        {
            var state = engine.State;
            var snap = new ClientSnapshot
            {
                ViewerIndex = viewerIndex,
                EventSeq = engine.Log.Count,
                TurnPlayerIndex = state.TurnPlayerIndex,
                Phase = state.Phase,
                Round = state.Round,
                GameOver = state.GameOver,
                WinnerIndex = state.WinnerIndex,
                BossHp = state.Rules.BossHp
            };

            foreach (var p in state.Players)
            {
                var ps = new PlayerSnap
                {
                    Index = p.Index,
                    Name = p.Name,
                    HeroId = p.HeroId,
                    Level = p.Level,
                    Xp = p.Xp,
                    Position = p.Position,
                    LastInnCheckpoint = p.LastInnCheckpoint,
                    Ap = p.Ap,
                    DamagePool = p.DamagePool,
                    Conceded = p.Conceded,
                    HeroActiveUsed = p.HeroActiveUsedThisTurn,
                    HeroUltimateUsed = p.HeroUltimateUsedThisTurn,
                    DeckCount = p.Deck.Count,
                    HandCount = p.Hand.Count
                };
                if (p.Index == viewerIndex)
                    foreach (var c in p.Hand)
                        ps.Hand.Add(Snap(c, true));
                foreach (var c in p.Discard) ps.Discard.Add(Snap(c, true));
                foreach (var c in p.Exile) ps.Exile.Add(Snap(c, true));
                foreach (var c in p.PlayedThisTurn) ps.PlayedThisTurn.Add(Snap(c, true));
                foreach (var c in p.Relics) ps.Relics.Add(Snap(c, true));
                for (int i = 0; i < 3; i++)
                    ps.Equipment[i] = p.Equipment[i] == null ? null : Snap(p.Equipment[i], true);
                snap.Players.Add(ps);
            }

            for (int t = 0; t < 3; t++)
            {
                snap.PileCounts[t] = state.Market.Piles[t].Count;
                var row = state.Market.Rows[t];
                snap.MarketRows[t] = new CardSnap[row.Length];
                for (int s = 0; s < row.Length; s++)
                    snap.MarketRows[t][s] = row[s] == null ? null : Snap(row[s], true);
            }

            if (state.Boss != null)
                snap.Boss = Snap(state.Boss, true);

            foreach (var item in state.Stack.Items)
            {
                snap.Stack.Add(new StackItemSnap
                {
                    Id = item.Id,
                    Kind = item.Kind.ToString(),
                    ControllerIndex = item.ControllerIndex,
                    DefId = item.SpellCard?.DefId ?? item.SourceCard?.DefId,
                    Description = item.Description,
                    Amount = item.Amount,
                    Targets = new List<Targeting.TargetRef>(item.Targets)
                });
            }

            var pending = engine.PendingInput;
            if (pending != null)
            {
                snap.Pending = new PendingSnap { Kind = pending.Kind, PlayerIndex = pending.PlayerIndex };
                if (pending.PlayerIndex == viewerIndex)
                {
                    snap.Pending.LegalActions = pending.LegalActions;
                    snap.Pending.Decision = pending.Decision;
                }
            }

            return snap;
        }

        private static CardSnap Snap(Cards.CardInstance c, bool revealed) => new()
        {
            InstanceId = c.InstanceId,
            DefId = revealed ? c.DefId : null,
            Tapped = c.Tapped,
            MarkedDamage = c.MarkedDamage
        };
    }
}
