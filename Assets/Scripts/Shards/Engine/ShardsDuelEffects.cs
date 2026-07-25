using System.Collections.Generic;
using Pascension.Engine.Decisions;

namespace Shards.Engine
{
    /// <summary>Duel of Doom effect vocabulary. All of these only appear on cards from the
    /// "duel" set (gated by the Duel DLC flag), but they are ordinary effects/helpers with
    /// no gating of their own.</summary>
    public static class ShardsDuel
    {
        private static readonly ShardsFaction[] AllFactions =
        {
            ShardsFaction.Homodeus, ShardsFaction.Undergrowth, ShardsFaction.Order,
            ShardsFaction.Wraethe, ShardsFaction.Aion
        };

        /// <summary>Distinct factions among the cards the player played this turn (used by
        /// Multitask Brain and the "3+ different factions" destiny gates). Starters/None
        /// never count; CountsAsEveryFaction (Prism) and Project Yggdrasil honored.</summary>
        public static int DistinctFactionsPlayed(ShardsPlayer player)
        {
            var seen = new HashSet<ShardsFaction>();
            foreach (var card in player.PlayedThisTurn)
            {
                if (card.Def.Faction == ShardsFaction.None || card.Def.Faction == ShardsFaction.Monster) continue;
                foreach (var f in AllFactions)
                    if (ShardsEngine.CountsAs(player, card.Def, f))
                        seen.Add(f);
            }
            return seen.Count;
        }

        /// <summary>Faction cards played this turn — the "cards" half of "played CARDS of
        /// 3+ different factions": a single wildcard (Prism) never satisfies it alone.</summary>
        public static int PlayedFactionCards(ShardsPlayer player)
        {
            int n = 0;
            foreach (var card in player.PlayedThisTurn)
                if (card.Def.Faction != ShardsFaction.None && card.Def.Faction != ShardsFaction.Monster)
                    n++;
            return n;
        }
    }

    /// <summary>"Allegiance &lt;Faction&gt; N": the inner effect fires only if the controller
    /// OWNS at least N cards of that faction — counting deck, hand, discard, play zone and
    /// champions (banished and set-aside cards don't count; the card itself is owned and
    /// counts). Honors Project Yggdrasil / Prism via <see cref="ShardsEngine.CountsAs"/>.</summary>
    public sealed class AllegianceEffect : IShardsEffect, IShardsConditionalEffect
    {
        private readonly ShardsFaction _faction;
        private readonly int _required;
        private readonly IShardsEffect _inner;
        public ShardsFaction Faction => _faction;
        public int Required => _required;
        public IShardsEffect Inner => _inner;

        public AllegianceEffect(ShardsFaction faction, int required, IShardsEffect inner)
        {
            _faction = faction;
            _required = required;
            _inner = inner;
        }

        public static int OwnedCount(ShardsPlayer p, ShardsFaction faction)
        {
            int n = 0;
            Count(p.Deck); Count(p.Hand); Count(p.Discard); Count(p.PlayZone); Count(p.Champions);
            return n;

            void Count(List<ShardsCard> list)
            {
                foreach (var c in list)
                    if (ShardsEngine.CountsAs(p, c.Def, faction))
                        n++;
            }
        }

        public bool ConditionMet(ShardsContext ctx) =>
            OwnedCount(ctx.Controller, _faction) >= _required;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (OwnedCount(ctx.Controller, _faction) < _required) yield break;
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Scry N (center deck): look at the top N cards; choose any to move to the
    /// bottom, the rest stay on top in their current order. The center deck's TOP is the
    /// END of the list (RefillSlot pops from the end); its BOTTOM is index 0.</summary>
    public sealed class Scry : IShardsEffect
    {
        private readonly int _count;
        public int Count => _count;
        public Scry(int count) => _count = count;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var deck = ctx.Engine.State.CenterDeck;
            int n = System.Math.Min(_count, deck.Count);
            if (n == 0) yield break;

            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Scry {n}: choose cards to move to the bottom of the center deck",
                Context = "soi.scry",
                Min = 0,
                Max = n
            };
            for (int i = 0; i < n; i++)
            {
                var card = deck[deck.Count - 1 - i]; // top-down
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name)
                { CardInstanceId = card.InstanceId, DefId = card.DefId });
            }
            yield return ShardsStep.AwaitDecision(request);

            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                int idx = deck.FindIndex(c => c.InstanceId == id);
                if (idx < 0) continue;
                var card = deck[idx];
                deck.RemoveAt(idx);
                deck.Insert(0, card); // to the bottom
            }
        }
    }

    /// <summary>Index of Futures: look at the center deck's top N cards and put them back
    /// in ANY order. The decision's pick order IS the new order — first picked = new top.
    /// Duplicate picks are rejected by the engine's answer validation.</summary>
    public sealed class ReorderCenterTop : IShardsEffect
    {
        private readonly int _count;
        public int Count => _count;
        public ReorderCenterTop(int count) => _count = count;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var deck = ctx.Engine.State.CenterDeck;
            int n = System.Math.Min(_count, deck.Count);
            if (n == 0) yield break;

            var top = new List<ShardsCard>();
            for (int i = 0; i < n; i++)
                top.Add(deck[deck.Count - 1 - i]); // top-down

            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Put the top {n} cards back in any order — first picked ends up ON TOP",
                Context = "soi.reorder",
                Min = n,
                Max = n
            };
            foreach (var card in top)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name)
                { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);

            // Remove the peeked cards, then re-insert per the chosen order (last pushed
            // last = deepest): iterate the answer BACKWARDS appending to the deck end.
            var byId = new Dictionary<int, ShardsCard>();
            foreach (var card in top)
            {
                deck.Remove(card);
                byId[card.InstanceId] = card;
            }
            var order = ctx.Answer.ChosenOptionIds;
            for (int i = order.Count - 1; i >= 0; i--)
                if (byId.TryGetValue(order[i], out var card))
                {
                    byId.Remove(order[i]);
                    deck.Add(card); // list end = top
                }
            foreach (var leftover in byId.Values)
                deck.Add(leftover); // safety: never lose a card
        }
    }

    /// <summary>Whisper Extractor: a TARGET opponent draws a card, then the controller
    /// looks at their hand and chooses one card — they discard it. Card-neutral for the
    /// victim (the draw replaces what they lose) but the controller strips their best
    /// option. Both decisions are shown ONLY to the controller (leak-safe: pending
    /// decisions only ever reach the deciding viewer).
    ///
    /// The hand options are SORTED by def id, then instance id — hand order is hidden
    /// information (`ShardsCardDrawnEvent` redacts the def for every other viewer), so an
    /// unsorted list would hand the controller the one thing the engine hides: which card
    /// was just drawn. Same anti-leak rule as World Piercer and Grim Tutor.</summary>
    public sealed class OpponentDrawsThenDiscards : IShardsEffect
    {
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var opponents = new List<ShardsPlayer>(engine.State.LivingOpponentsOf(ctx.ControllerIndex));
            if (opponents.Count == 0) yield break;

            var target = opponents[0];
            if (opponents.Count > 1)
            {
                var pick = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.ChooseMode,
                    Title = "Choose an opponent — they draw a card, then discard one you choose",
                    Context = "soi.target",
                    Min = 1,
                    Max = 1
                };
                foreach (var opponent in opponents)
                    pick.Options.Add(new DecisionOption(opponent.Index, opponent.Name));
                yield return ShardsStep.AwaitDecision(pick);

                // Ill-behaved clients must never redirect this at a non-opponent.
                int chosenSeat = ctx.Answer.ChosenOptionIds.Count > 0 ? ctx.Answer.ChosenOptionIds[0] : -1;
                var chosenTarget = opponents.Find(o => o.Index == chosenSeat);
                if (chosenTarget != null) target = chosenTarget;
            }

            engine.DrawCards(target.Index, 1);
            if (target.Hand.Count == 0) yield break; // deck AND discard were empty

            var candidates = new List<ShardsCard>(target.Hand);
            candidates.Sort((a, b) =>
            {
                int byDef = string.CompareOrdinal(a.DefId, b.DefId);
                return byDef != 0 ? byDef : a.InstanceId.CompareTo(b.InstanceId);
            });

            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Choose a card in " + target.Name + "'s hand — they discard it",
                Context = "soi.handpick",
                Min = 1,
                Max = 1
            };
            foreach (var card in candidates)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name)
                { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);

            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            int chosenId = ctx.Answer.ChosenOptionIds[0];
            var discarded = target.Hand.Find(c => c.InstanceId == chosenId);
            if (discarded == null) yield break;
            target.Hand.Remove(discarded);
            discarded.Zone = ShardsZone.Discard;
            target.Discard.Add(discarded);
            engine.Emit(new ShardsCardsRevealedEvent
            { PlayerIndex = target.Index, DefIds = new List<string> { discarded.DefId } });
        }
    }
}
