using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Core;

namespace Shards.Engine
{
    public sealed class ShardsCardSnap
    {
        public int InstanceId;
        public string DefId;
        public bool Exhausted;
        /// <summary>Champion/Ingeminex damage marked this turn.</summary>
        public int DamageThisTurn;
        /// <summary>Owning player (-1 = market card). Lets the banish browser group
        /// cards by who banished them.</summary>
        public int Owner = -1;
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
        public bool DestinyTaken;

        public int DeckCount;
        public int HandCount;
        /// <summary>Populated only for the viewer.</summary>
        public List<ShardsCardSnap> Hand;
        /// <summary>Public zones.</summary>
        public List<ShardsCardSnap> Discard = new();
        public List<ShardsCardSnap> PlayZone = new();
        public List<ShardsCardSnap> Champions = new();
        /// <summary>Owned destinies sit face up in front of the player (public).</summary>
        public List<ShardsCardSnap> Destinies = new();
        /// <summary>Viewer-only: unearned set-aside relics.</summary>
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
        /// <summary>Shared face-up destiny row (ItH) — public.</summary>
        public List<ShardsCardSnap> DestinyRow = new();
        /// <summary>Revealed Ingeminex beside the row (ItH) — public.</summary>
        public List<ShardsCardSnap> ActiveMonsters = new();
        /// <summary>Shared removed-from-game pile — public.</summary>
        public List<ShardsCardSnap> Banished = new();
        public List<ShardsPlayerSnap> Players = new();
        public PendingSnapInfo Pending;

        /// <summary>Instance ids whose conditional effect (Unify/Dominion/If/per-count…;
        /// mastery thresholds deliberately excluded — they'd glow forever) is satisfied
        /// RIGHT NOW: the viewer's hand + center row probed for the viewer, every
        /// player's ready champions/destinies probed for their owner. Drives the
        /// faction-color condition glow.</summary>
        public List<int> ConditionGlowIds = new();
        /// <summary>Champions/Ingeminex the VIEWER could destroy with their current
        /// power (attack rules + effective defense applied) — red glow.</summary>
        public List<int> KillableIds = new();
        /// <summary>Center-row slots the viewer can afford to buy right now.</summary>
        public List<int> BuyableSlots = new();

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
            foreach (var card in state.DestinyRow) snapshot.DestinyRow.Add(Snap(card));
            foreach (var card in state.ActiveMonsters) snapshot.ActiveMonsters.Add(Snap(card));
            foreach (var card in state.Banished) snapshot.Banished.Add(Snap(card));

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
                    DestinyTaken = player.DestinyTaken,
                    DeckCount = player.Deck.Count,
                    HandCount = player.Hand.Count
                };
                foreach (var card in player.Discard) snap.Discard.Add(Snap(card));
                foreach (var card in player.PlayZone) snap.PlayZone.Add(Snap(card));
                foreach (var card in player.Champions) snap.Champions.Add(Snap(card));
                foreach (var card in player.Destinies) snap.Destinies.Add(Snap(card));
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

            BuildGlowHints(engine, viewerIndex, snapshot);
            return snapshot;
        }

        /// <summary>UI affordances computed host-side (the client has no engine):
        /// condition-met glow ids, viewer-killable targets, viewer-affordable slots.</summary>
        private static void BuildGlowHints(ShardsEngine engine, int viewerIndex, ShardsSnapshot snapshot)
        {
            var state = engine.State;
            if (viewerIndex < 0 || viewerIndex >= state.Players.Count) return;
            var viewer = state.Players[viewerIndex];

            bool Lit(IShardsEffect effect, int controllerIndex, ShardsCard source) =>
                effect != null && ShardsGlowProbe.ConditionLit(effect,
                    new ShardsContext { Engine = engine, ControllerIndex = controllerIndex, Source = source });

            // "Affordable" only exists while the viewer can actually still buy: their
            // own turn, holding PRIORITY. The moment END TURN is submitted the pending
            // input flips to the end-phase decisions and the halos die with it — even
            // though gems are only reset later, at cleanup.
            var pendingInput = engine.PendingInput;
            bool viewerCanBuy = !state.GameOver && state.TurnPlayerIndex == viewerIndex &&
                                pendingInput != null && pendingInput.PlayerIndex == viewerIndex &&
                                pendingInput.Kind == PendingInputKind.Priority;

            for (int s = 0; s < state.CenterRow.Length; s++)
            {
                var card = state.CenterRow[s];
                if (card == null) continue;
                if (viewerCanBuy && viewer.Gems >= engine.EffectiveCost(viewer, card.Def))
                    snapshot.BuyableSlots.Add(s);
                if (Lit(card.Def.PlayEffect, viewerIndex, card))
                    snapshot.ConditionGlowIds.Add(card.InstanceId);
            }

            foreach (var card in viewer.Hand)
                if (Lit(card.Def.PlayEffect, viewerIndex, card))
                    snapshot.ConditionGlowIds.Add(card.InstanceId);

            foreach (var player in state.Players)
            {
                foreach (var card in player.Champions)
                    if (!card.Exhausted && player.Gems >= card.Def.ExhaustGemCost &&
                        Lit(card.Def.ExhaustEffect, player.Index, card))
                        snapshot.ConditionGlowIds.Add(card.InstanceId);
                foreach (var card in player.Destinies)
                    if (!card.Exhausted && player.Gems >= card.Def.ExhaustGemCost &&
                        Lit(card.Def.ExhaustEffect, player.Index, card))
                        snapshot.ConditionGlowIds.Add(card.InstanceId);
            }

            if (viewer.Eliminated) return;
            foreach (var opponent in state.LivingOpponentsOf(viewerIndex))
                foreach (var champion in opponent.Champions)
                {
                    int remaining = engine.EffectiveDefense(opponent, champion) - champion.DamageThisTurn;
                    if (remaining > 0 && viewer.Power >= remaining &&
                        engine.CanAttackChampion(viewer, opponent, champion))
                        snapshot.KillableIds.Add(champion.InstanceId);
                }
            foreach (var monster in state.ActiveMonsters)
            {
                int remaining = monster.Def.Defense - monster.DamageThisTurn;
                if (remaining > 0 && viewer.Power >= remaining)
                    snapshot.KillableIds.Add(monster.InstanceId);
            }
        }

        private static ShardsCardSnap Snap(ShardsCard card) => new()
        {
            InstanceId = card.InstanceId,
            DefId = card.DefId,
            Exhausted = card.Exhausted,
            DamageThisTurn = card.DamageThisTurn,
            Owner = card.Owner
        };
    }
}
