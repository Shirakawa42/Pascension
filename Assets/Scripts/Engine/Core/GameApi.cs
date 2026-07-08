using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Decisions;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;
using Pascension.Engine.Stack;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Core
{
    /// <summary>
    /// An engine-internal effect waiting to run outside the stack (monster rewards,
    /// inn choices, cleanup keep-decisions). Processed by the engine loop one at a time.
    /// </summary>
    public sealed class PendingInternalEffect
    {
        public IEffect Effect;
        public int ControllerIndex;
        public CardInstance Source;
        public List<TargetRef> Targets = new();
        public string Description = "";
    }

    /// <summary>
    /// The single mutation toolkit. Every state change flows through here so that events
    /// are always emitted and static abilities are always consulted. Effects reach it via
    /// EffectContext; engine systems use it directly.
    /// </summary>
    public sealed class GameApi
    {
        public readonly GameState State;
        public readonly EventLog Log;
        public readonly Queue<PendingInternalEffect> InternalEffects = new();

        public GameApi(GameState state, EventLog log)
        {
            State = state;
            Log = log;
        }

        public void Emit(GameEvent e) => Log.Append(e);

        // ---------- Static-ability queries ----------

        /// <summary>All active static abilities for a player: equipped items, relics, hero passives (level-gated). Stable order.</summary>
        public IEnumerable<IStaticAbility> ActiveStatics(PlayerState p)
        {
            foreach (var card in p.Permanents())
                foreach (var s in card.Def.StaticAbilities)
                    yield return s;
            foreach (var (minLevel, ability) in p.Hero.PassiveStatics)
                if (p.Level >= minLevel)
                    yield return ability;
        }

        /// <summary>All active trigger sources for a player (permanents + hero passives).</summary>
        public IEnumerable<(TriggerSource source, TriggeredAbility ability)> ActiveTriggers(PlayerState p)
        {
            foreach (var card in p.Permanents())
                foreach (var t in card.Def.TriggeredAbilities)
                    yield return (new TriggerSource(card, p.Index), t);
            foreach (var (minLevel, ability) in p.Hero.PassiveTriggers)
                if (p.Level >= minLevel)
                    yield return (new TriggerSource(null, p.Index), ability);
        }

        public int GetBuyCost(PlayerState buyer, CardDefinition def)
        {
            int cost = def.Cost;
            foreach (var s in ActiveStatics(buyer))
                if (s is IBuyCostModifier m)
                    cost += m.CostDelta(State, buyer, def);
            return cost < 0 ? 0 : cost;
        }

        public int EffectiveMonsterHp(CardInstance monster) => State.Continuous.EffectiveMonsterHp(monster);

        // ---------- Resources ----------

        public void GainAp(PlayerState p, int amount)
        {
            if (amount == 0) return;
            p.Ap += amount;
            if (p.Ap < 0) p.Ap = 0;
            Emit(new ApChangedEvent { PlayerIndex = p.Index, Delta = amount, NewValue = p.Ap });
        }

        public void GainDamage(PlayerState p, int amount, bool fromCardSpell)
        {
            if (fromCardSpell)
            {
                foreach (var s in ActiveStatics(p))
                    if (s is IDamageGainModifier m)
                        amount += m.Bonus(State, p, amount, true);
                p.DamageCardsThisTurn++;
            }
            if (amount == 0) return;
            p.DamagePool += amount;
            if (p.DamagePool < 0) p.DamagePool = 0;
            Emit(new DamagePoolChangedEvent { PlayerIndex = p.Index, Delta = amount, NewValue = p.DamagePool });
        }

        public void GainXp(PlayerState p, int amount)
        {
            if (amount <= 0) return;
            foreach (var s in ActiveStatics(p))
                if (s is IXpGainModifier m)
                    amount += m.Bonus(State, p, amount);
            p.Xp += amount;
            Emit(new XpGainedEvent { PlayerIndex = p.Index, Amount = amount, NewXp = p.Xp });
            // Level-ups are applied by state-based actions.
        }

        // ---------- Card movement ----------

        /// <summary>Detach a card from wherever it currently is. Market row slots become empty (no auto-refill).</summary>
        public void RemoveFromCurrentZone(CardInstance card)
        {
            switch (card.Zone)
            {
                case ZoneType.Deck: State.Players[card.Owner].Deck.Remove(card); break;
                case ZoneType.Hand: State.Players[card.Owner].Hand.Remove(card); break;
                case ZoneType.Discard: State.Players[card.Owner].Discard.Remove(card); break;
                case ZoneType.Exile: State.Players[card.Owner].Exile.Remove(card); break;
                case ZoneType.PlayedThisTurn: State.Players[card.Owner].PlayedThisTurn.Remove(card); break;
                case ZoneType.Relics: State.Players[card.Owner].Relics.Remove(card); break;
                case ZoneType.Equipment:
                {
                    var p = State.Players[card.Owner];
                    for (int i = 0; i < p.Equipment.Length; i++)
                        if (p.Equipment[i] == card)
                            p.Equipment[i] = null;
                    card.Slot = EquipSlot.None;
                    break;
                }
                case ZoneType.MarketRow:
                {
                    if (State.Market.TryLocate(card.InstanceId, out var tier, out int slot))
                        State.Market.RowFor(tier)[slot] = null;
                    break;
                }
                case ZoneType.Pile:
                {
                    for (int t = 0; t < Market.Tiers; t++)
                        State.Market.Piles[t].Remove(card);
                    break;
                }
                case ZoneType.Stack:
                    // The card is carried by its StackItem; nothing to detach here.
                    break;
            }
        }

        /// <summary>Move a card into a player-owned list zone (Hand/Discard/Exile/PlayedThisTurn/Relics/Deck-top/bottom).</summary>
        public void MoveCard(CardInstance card, ZoneType to, int newOwner, bool deckBottom = false)
        {
            var from = card.Zone;
            RemoveFromCurrentZone(card);
            card.Owner = newOwner;
            card.Zone = to;
            card.Timestamp = State.NextTimestamp();
            var p = State.Players[newOwner];
            switch (to)
            {
                case ZoneType.Hand: p.Hand.Add(card); break;
                case ZoneType.Discard: p.Discard.Add(card); break;
                case ZoneType.Exile: p.Exile.Add(card); break;
                case ZoneType.PlayedThisTurn: p.PlayedThisTurn.Add(card); break;
                case ZoneType.Relics: p.Relics.Add(card); break;
                case ZoneType.Deck:
                    if (deckBottom) p.Deck.Add(card);
                    else p.Deck.Insert(0, card);
                    break;
            }
            State.Continuous.RemoveForInstance(card.InstanceId);
            card.MarkedDamage = 0;
            card.Tapped = false;
            Emit(new CardMovedEvent { InstanceId = card.InstanceId, DefId = card.DefId, OwnerIndex = newOwner, From = from, To = to });
        }

        /// <summary>Exile a market card (dead monsters, RBG / Cataclysm) into the shared market-exile zone.</summary>
        public void ExileFromMarket(CardInstance card)
        {
            var from = card.Zone;
            RemoveFromCurrentZone(card);
            card.Zone = ZoneType.Exile;
            card.MarkedDamage = 0;
            State.MarketExile.Add(card);
            State.Continuous.RemoveForInstance(card.InstanceId);
            Emit(new CardMovedEvent { InstanceId = card.InstanceId, DefId = card.DefId, OwnerIndex = card.Owner, From = from, To = ZoneType.Exile });
        }

        /// <summary>Put a card on the bottom of its tier's pile (Random Bullshit Go).</summary>
        public void BottomOfPile(CardInstance card, CardTier tier)
        {
            var from = card.Zone;
            RemoveFromCurrentZone(card);
            card.Zone = ZoneType.Pile;
            State.Market.PileFor(tier).Add(card);
            Emit(new CardMovedEvent { InstanceId = card.InstanceId, DefId = card.DefId, OwnerIndex = card.Owner, From = from, To = ZoneType.Pile });
        }

        public void Equip(PlayerState p, CardInstance card)
        {
            var slot = card.Def.Slot;
            var existing = p.EquipmentIn(slot);
            if (existing != null)
                MoveCard(existing, ZoneType.Exile, p.Index); // replaced equipment is exiled (GDD)
            var from = card.Zone;
            RemoveFromCurrentZone(card);
            card.Owner = p.Index;
            card.Zone = ZoneType.Equipment;
            card.Slot = slot;
            card.Tapped = false;
            card.Timestamp = State.NextTimestamp();
            p.SetEquipment(slot, card);
            Emit(new CardMovedEvent { InstanceId = card.InstanceId, DefId = card.DefId, OwnerIndex = p.Index, From = from, To = ZoneType.Equipment });
        }

        public void Tap(CardInstance card)
        {
            card.Tapped = true;
            Emit(new CardTappedEvent { InstanceId = card.InstanceId, Tapped = true });
        }

        // ---------- Drawing ----------

        public void DrawCards(PlayerState p, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (p.Deck.Count == 0)
                {
                    if (p.Discard.Count == 0) return;
                    // Shuffle discard into deck. Cards in PlayedThisTurn are NOT included (GDD).
                    foreach (var c in p.Discard)
                    {
                        c.Zone = ZoneType.Deck;
                        p.Deck.Add(c);
                    }
                    p.Discard.Clear();
                    State.Rng.Shuffle(p.Deck);
                    Emit(new DeckShuffledEvent { PlayerIndex = p.Index });
                }
                var card = p.Deck[0];
                p.Deck.RemoveAt(0);
                card.Zone = ZoneType.Hand;
                p.Hand.Add(card);
                Emit(new CardDrawnEvent { PlayerIndex = p.Index, InstanceId = card.InstanceId, DefId = card.DefId });
            }
        }

        // ---------- Market ----------

        public void RefillSlot(CardTier tier, int slot)
        {
            var pile = State.Market.PileFor(tier);
            var row = State.Market.RowFor(tier);
            if (row[slot] != null) return;
            if (pile.Count == 0)
            {
                Emit(new MarketRefilledEvent { Tier = tier, SlotIndex = slot, InstanceId = -1, DefId = null });
                return;
            }
            var card = pile[0];
            pile.RemoveAt(0);
            card.Zone = ZoneType.MarketRow;
            row[slot] = card;
            Emit(new MarketRefilledEvent { Tier = tier, SlotIndex = slot, InstanceId = card.InstanceId, DefId = card.DefId });
        }

        // ---------- Stack ----------

        public StackItem PushSpell(CardInstance card, int controller, List<TargetRef> targets, bool isFree)
        {
            var from = card.Zone;
            RemoveFromCurrentZone(card);
            card.Zone = ZoneType.Stack;
            Emit(new CardMovedEvent { InstanceId = card.InstanceId, DefId = card.DefId, OwnerIndex = controller, From = from, To = ZoneType.Stack });

            var item = new StackItem
            {
                Id = State.NextStackItemId++,
                Kind = StackItemKind.Spell,
                ControllerIndex = controller,
                SpellCard = card,
                Effect = card.Def.SpellEffect,
                Targets = targets ?? new List<TargetRef>(),
                IsFree = isFree,
                Description = card.Def.Name
            };
            State.Stack.Push(item);
            Emit(new StackPushedEvent
            {
                StackItemId = item.Id,
                Kind = item.Kind.ToString(),
                ControllerIndex = controller,
                DefId = card.DefId,
                SourceInstanceId = card.InstanceId,
                Description = item.Description,
                Targets = new List<TargetRef>(item.Targets)
            });
            return item;
        }

        public StackItem PushAbility(StackItemKind kind, CardInstance source, int controller, IEffect effect, List<TargetRef> targets, string description, int amount = 0)
        {
            var item = new StackItem
            {
                Id = State.NextStackItemId++,
                Kind = kind,
                ControllerIndex = controller,
                SourceCard = source,
                Effect = effect,
                Targets = targets ?? new List<TargetRef>(),
                Amount = amount,
                Description = description
            };
            State.Stack.Push(item);
            Emit(new StackPushedEvent
            {
                StackItemId = item.Id,
                Kind = kind.ToString(),
                ControllerIndex = controller,
                DefId = source?.DefId,
                SourceInstanceId = source?.InstanceId ?? -1,
                Description = description,
                Targets = new List<TargetRef>(item.Targets)
            });
            return item;
        }

        // ---------- Internal (off-stack) effects ----------

        public void QueueInternal(IEffect effect, int controller, CardInstance source, string description, List<TargetRef> targets = null)
        {
            InternalEffects.Enqueue(new PendingInternalEffect
            {
                Effect = effect,
                ControllerIndex = controller,
                Source = source,
                Description = description,
                Targets = targets ?? new List<TargetRef>()
            });
        }

        public DecisionRequest NewDecision(DecisionRequest req)
        {
            req.Id = State.NextDecisionId++;
            return req;
        }
    }
}
