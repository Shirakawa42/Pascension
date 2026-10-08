using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class Encoder
    {
        internal const int ObsDim = 2048, MaxActions = 64, ActionDim = 32, CardCapacity = 192;
        internal const string SchemaVersion = "shards-observation-v3";
        internal const int MaxCardDefinitions = 189;
        internal static readonly int[] AllegianceSlots = { 317, 318, 319, 509, 510, 511, 701 };
        internal static readonly object[] ExtraObservationFeatures = Enumerable.Range(0, 7).Select(i => (object)new
        {
            slot = AllegianceSlots[i], name = "own_allegiance_" + ((ShardsFaction)i).ToString().ToLowerInvariant(),
            faction = ((ShardsFaction)i).ToString(), normalization = 20,
            source = "AllegianceEffect.OwnedCount(current deciding player)"
        }).ToArray();
        private const int EntityOffset = 1856, EntityCapacity = 24, TraceOffset = 2000, TraceCapacity = 16;
        internal static string[] CardIds;
        private static Dictionary<string, int> _cardIndex;
        internal static void Initialize()
        {
            CardIds = ShardsCardDatabase.All.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (CardIds.Length > MaxCardDefinitions)
                throw new InvalidOperationException("Schema v3 reserves count slots 189..191; maximum catalog size is 189");
            _cardIndex = CardIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        }
        internal static int CardIndex(string id) => id != null && _cardIndex.TryGetValue(id, out int index) ? index : -1;
        internal static int HeroIndex(string id) => Array.IndexOf(ShardsEngine.DraftableCharacters, id);

        internal static void Encode(Adapter game, Span<float> obs, Span<float> candidates, Span<float> mask)
        {
            obs.Clear(); candidates.Clear(); mask.Clear();
            int actor = game.Actor;
            var state = game.Engine.State;
            var own = state.Players[actor];
            var opponent = state.Players[1 - actor];
            EncodeScalars(game, obs);
            // Only the deciding player's own collection is inspected. Temporary
            // fast-plays count exactly as in the authorized Allegiance rule.
            Span<float> allegiance = stackalloc float[7];
            Shards.FactionCountProbe.FactionCounter.OnePass(own, allegiance);
            for (int faction = 0; faction < AllegianceSlots.Length; faction++)
                obs[AllegianceSlots[faction]] = allegiance[faction];
            Count(own.Hand, obs, 0);
            Count(own.Deck, obs, 1); Count(own.Hand, obs, 1); Count(own.Discard, obs, 1);
            Count(own.PlayZone, obs, 1, permanentOnly: true); Count(own.Champions, obs, 1);
            Count(own.Discard, obs, 2);
            Count(own.PlayZone, obs, 3); Count(own.Champions, obs, 3); Count(own.Destinies, obs, 3);
            Count(opponent.Discard, obs, 4);
            Count(opponent.PlayZone, obs, 5); Count(opponent.Champions, obs, 5); Count(opponent.Destinies, obs, 5);
            Count(state.CenterRow, obs, 6);
            Count(state.DestinyRow, obs, 7); Count(state.ActiveMonsters, obs, 7);
            EncodePublicEntities(game, obs);
            int traceStart = Math.Max(0, game.SelectionTrace.Count - TraceCapacity);
            for (int i = 0; i < game.SelectionTrace.Count; i++)
            {
                var entry = game.SelectionTrace[i];
                int card = CardIndex(entry.option.DefId);
                if (card >= 0) obs[128 + 8 * CardCapacity + card] += entry.amount / 10f;
                if (i >= traceStart)
                {
                    int offset = TraceOffset + (i - traceStart) * 3;
                    obs[offset] = (card + 1) / 192f;
                    obs[offset + 1] = (entry.ordinal + 1) / 128f;
                    obs[offset + 2] = entry.amount / 1000f;
                }
            }
            for (int i = 0; i < game.VisibleCount; i++)
            {
                mask[i] = 1;
                EncodeCandidate(game, game.Visible(i), candidates.Slice(i * ActionDim, ActionDim));
            }
        }

        // Public/own fields only. Wire framing v1 carries explicit observation schema v2.
        internal static void EncodeScalars(Adapter game, Span<float> output)
        {
            var state = game.Engine.State;
            int actor = game.Actor;
            output[0] = actor;
            output[1] = state.TurnPlayerIndex == actor ? 1 : 0;
            output[2] = state.Round / 100f;
            output[3] = state.CenterDeck.Count / 200f;
            output[4] = game.Engine.PendingInput?.Decision != null ? 1 : 0;
            var d = game.Engine.PendingInput?.Decision;
            output[5] = d == null ? 0 : ((int)d.Kind + 1) / 8f;
            output[6] = d?.Min / 1000f ?? 0;
            output[7] = d?.Max / 1000f ?? 0;
            output[8] = d?.Ordered == true ? 1 : 0;
            output[9] = game.Selected.Count / 1000f;
            output[10] = game.Page / 10f;
            output[11] = game.Candidates.Count / 128f;
            output[12] = d?.Context == "soi.split" ? game.SplitTarget / 64f : 0;
            output[13] = d?.Context == "soi.split" ? game.Remaining / 1000f : 0;
            output[14] = d?.Context == "soi.split" ? game.Low / 1000f : 0;
            output[15] = d?.Context == "soi.split" ? game.High / 1000f : 0;
            for (int relative = 0; relative < 2; relative++)
            {
                var p = state.Players[relative == 0 ? actor : 1 - actor];
                int offset = 16 + relative * 48;
                output[offset] = p.Health / 50f; output[offset + 1] = p.Mastery / 30f;
                output[offset + 2] = p.Gems / 20f; output[offset + 3] = p.Power / 100f;
                output[offset + 4] = p.Hand.Count / 20f; output[offset + 5] = p.Deck.Count / 50f;
                output[offset + 6] = (HeroIndex(p.CharacterId) + 1) / 5f;
                output[offset + 7] = p.CharacterExhausted ? 1 : 0;
                output[offset + 8] = p.FocusedThisTurn ? 1 : 0;
                output[offset + 9] = p.HeroAbilityUsedThisTurn ? 1 : 0;
                output[offset + 10] = p.FirstBuyUsedThisTurn ? 1 : 0;
                output[offset + 11] = p.RelicRecruited ? 1 : 0;
                output[offset + 12] = p.DestinyTaken ? 1 : 0;
                output[offset + 13] = p.ExtraTurnUsed ? 1 : 0;
                output[offset + 14] = p.DoomGateFloodUsed ? 1 : 0;
                output[offset + 15] = p.ShieldsDoubledUntilNextTurn ? 1 : 0;
                output[offset + 16] = p.IgnoreShieldsThisTurn ? 1 : 0;
                output[offset + 17] = p.HealthToPowerThisTurn ? 1 : 0;
                output[offset + 18] = p.HealingDoubledThisTurn ? 1 : 0;
                output[offset + 19] = p.OverflowHealthToPowerThisTurn ? 1 : 0;
                output[offset + 20] = p.NextRecruitsToHand / 10f;
                output[offset + 21] = p.NextHomodeusChampionsIntoPlay / 10f;
                output[offset + 22] = p.NextChampionsIntoPlay / 10f;
                output[offset + 23] = p.CopyHomodeusAlliesThisTurn ? 1 : 0;
                output[offset + 24] = p.BonusDrawsOnBigHit / 10f;
                output[offset + 25] = p.MaxDamageDealtToOneOpponent / 100f;
                output[offset + 26] = p.CardsBanishedThisTurn / 10f;
                output[offset + 27] = p.RerollsThisTurn / 10f;
                output[offset + 28] = ShardsEngine.RerollCost(p) / 20f;
                output[offset + 29] = p.Champions.Count / 20f;
                int exhausted = 0, damage = 0;
                foreach (var champion in p.Champions)
                {
                    if (champion.Exhausted) exhausted++;
                    damage += champion.DamageThisTurn;
                }
                output[offset + 30] = exhausted / 20f;
                output[offset + 31] = damage / 100f;
                for (int faction = 0; faction < 7; faction++)
                {
                    output[offset + 32 + faction] = p.FactionPlays((ShardsFaction)faction) / 20f;
                    output[offset + 39 + faction] = p.FactionAllyPlays((ShardsFaction)faction) / 20f;
                }
            }
            // Stable context bytes supply identity without touching titles that can contain instance IDs.
            uint hash = 2166136261;
            foreach (char ch in d?.Context ?? "") hash = unchecked((hash ^ ch) * 16777619);
            for (int i = 0; i < 4; i++) output[112 + i] = ((hash >> (i * 8)) & 255) / 255f;
            output[116] = state.ExtraTurnForPlayer < 0 ? -1 : state.ExtraTurnForPlayer == actor ? 0 : 1;
            output[117] = state.PendingMonsterAttacks.Count / 20f;
            output[118] = state.Banished.Count / 100f;
            output[119] = state.DestinyRow.Count / 20f;
            // The deciding player is authorized to see this title. Current engine
            // templates contain public card names, costs and assigned damage, not IDs.
            hash = 2166136261;
            int number = 0, numberSlot = 124;
            bool digits = false;
            foreach (char ch in d?.Title ?? "")
            {
                hash = unchecked((hash ^ ch) * 16777619);
                if (ch >= '0' && ch <= '9')
                {
                    digits = true;
                    number = Math.Min(1_000_000, number * 10 + ch - '0');
                }
                else if (digits)
                {
                    if (numberSlot < 128) output[numberSlot++] = number / 1000f;
                    digits = false; number = 0;
                }
            }
            if (digits && numberSlot < 128) output[numberSlot] = number / 1000f;
            for (int i = 0; i < 4; i++) output[120 + i] = ((hash >> (i * 8)) & 255) / 255f;
        }

        // Concrete collection overloads avoid boxing List<T>.Enumerator at every
        // zone; this path executes for every wrapper observation.
        private static void Count(List<ShardsCard> cards, Span<float> output, int channel, bool permanentOnly = false)
        {
            foreach (var card in cards)
                if (card != null && (!permanentOnly || !card.FastPlayed))
                    output[128 + channel * CardCapacity + _cardIndex[card.DefId]] += .1f;
        }

        private static void Count(ShardsCard[] cards, Span<float> output, int channel, bool permanentOnly = false)
        {
            foreach (var card in cards)
                if (card != null && (!permanentOnly || !card.FastPlayed))
                    output[128 + channel * CardCapacity + _cardIndex[card.DefId]] += .1f;
        }

        private static void EncodePublicEntities(Adapter game, Span<float> output)
        {
            var state = game.Engine.State;
            var own = state.Players[game.Actor];
            var enemy = state.Players[1 - game.Actor];
            int written = 0, total = 0;
            EncodeEntityList(game, own.Champions, output, ref written, ref total);
            EncodeEntityList(game, enemy.Champions, output, ref written, ref total);
            EncodeEntityList(game, state.ActiveMonsters, output, ref written, ref total);
            EncodeEntityList(game, own.Destinies, output, ref written, ref total);
            EncodeEntityList(game, enemy.Destinies, output, ref written, ref total);
            EncodeEntityList(game, own.PlayZone, output, ref written, ref total);
            EncodeEntityList(game, enemy.PlayZone, output, ref written, ref total);
            // Previously unused scalars. Overflow compresses observation only;
            // aggregate channels and all legal candidates remain unchanged.
            output[62] = total / 64f;
            output[63] = (total - written) / 64f;
        }

        private static void EncodeEntityList(Adapter game, List<ShardsCard> cards, Span<float> output,
            ref int written, ref int total)
        {
            total += cards.Count;
            foreach (var card in cards)
            {
                if (written == EntityCapacity) break;
                int offset = EntityOffset + written++ * 6;
                output[offset] = (CardIndex(card.DefId) + 1) / 192f;
                output[offset + 1] = card.Owner < 0 ? -1 : card.Owner == game.Actor ? 0 : 1;
                output[offset + 2] = card.Exhausted ? 1 : 0;
                output[offset + 3] = card.DamageThisTurn / 50f;
                output[offset + 4] = AuthorizedDefense(game, card) / 50f;
                output[offset + 5] = PublicShield(game, card) / 20f;
            }
        }

        private static int AuthorizedDefense(Adapter game, ShardsCard card)
        {
            if (!card.Def.IsChampion || card.Owner < 0) return card.Def.Defense;
            if (card.Owner == game.Actor)
                return game.Engine.EffectiveDefense(game.Engine.State.Players[card.Owner], card);
            // Ferrata Guard's aura probes collection identities. Never evaluate
            // it for the opponent: only reuse values already announced to this
            // deciding player by the engine's authorized split-option contract.
            var decision = game.Engine.PendingInput?.Decision;
            if (decision?.Context == "soi.split")
                foreach (var option in decision.Options)
                    if (option.CardInstanceId == card.InstanceId)
                        return option.Amount + card.DamageThisTurn;
            return card.Def.Defense;
        }

        private static int PublicShield(Adapter game, ShardsCard card)
        {
            // Current registered DynamicShield functions use public mastery;
            // ShieldValue additionally reads public destinies and doubling state.
            return card.Owner >= 0 ? game.Engine.ShieldValue(game.Engine.State.Players[card.Owner], card) : card.Def.Shield;
        }

        private static void EncodeCandidate(Adapter game, Candidate c, Span<float> output)
        {
            output[c.Kind] = 1;
            var state = game.Engine.State;
            var actor = state.Players[game.Actor];
            ShardsCard card = null;
            int slot = -1;
            int instance = c.Action switch
            {
                ShardsPlayCardAction a => a.CardInstanceId, ShardsExhaustAction a => a.CardInstanceId,
                ShardsAttackMonsterAction a => a.CardInstanceId, ShardsTakeDestinyAction a => a.CardInstanceId,
                ShardsRecruitRelicAction a => a.CardInstanceId, _ => -1
            };
            if (c.Action is ShardsBuyCardAction buy) slot = buy.SlotIndex;
            if (c.Action is ShardsRerollRowAction reroll) slot = reroll.SlotIndex;
            if (slot >= 0) card = state.CenterRow[slot];
            if (instance >= 0) card = FindVisibleCard(state, game.Actor, instance);
            if (c.Option?.CardInstanceId >= 0) card = FindVisibleCard(state, game.Actor, c.Option.CardInstanceId);
            string defId = c.Option?.DefId ?? card?.DefId;
            if (instance >= 0 && card == null) throw new InvalidOperationException("Priority candidate card is not visible");
            if (c.Option?.CardInstanceId >= 0 && defId == null)
                throw new InvalidOperationException("Decision card has neither visible card nor revealed DefId");
            int cardIndex = CardIndex(defId);
            int heroIndex = HeroIndex(defId);
            bool volosMode = defId != null && defId.StartsWith(VolosAbilityChoice.FacePrefix, StringComparison.Ordinal);
            if (defId != null && cardIndex < 0 && heroIndex < 0 && !volosMode)
                throw new InvalidOperationException("Unknown revealed definition " + defId);
            output[16] = (cardIndex + 1) / 192f;
            if (cardIndex >= 0)
            {
                var def = ShardsCardDatabase.Get(defId);
                output[17] = (int)def.Faction / 6f; output[18] = (int)def.Type / 6f;
                output[19] = def.Cost / 13f; output[20] = def.Defense / 50f;
                output[21] = def.Shield / 20f;
                output[24] = game.Engine.EffectiveCost(actor, def) / 13f;
            }
            if (card != null)
            {
                output[22] = card.Exhausted ? 1 : 0; output[23] = card.DamageThisTurn / 50f;
                output[20] = AuthorizedDefense(game, card) / 50f;
                output[21] = PublicShield(game, card) / 20f;
            }
            output[25] = slot >= 0 ? slot / 6f : c.Ordinal / 128f;
            output[26] = (c.Option?.Amount ?? (c.Action as ShardsAttackMonsterAction)?.Amount ?? 0) / 1000f;
            output[27] = c.Option?.Required == true ? 1 : 0;
            int owner = c.Option?.OwnerIndex ?? card?.Owner ?? -1;
            output[28] = owner < 0 ? -1 : owner == game.Actor ? 0 : 1;
            if (c.Kind == 15)
            {
                output[29] = c.Low / 1000f; output[30] = c.High / 1000f;
            }
            else if (card != null)
            {
                output[29] = ((int)card.Zone + 1) / 16f;
                output[30] = card.FastPlayed ? 1 : 0;
            }
            output[31] = heroIndex >= 0 ? (heroIndex + 1) / 5f : card?.BanishAtCleanup == true ? 1 : 0;
        }

        // Deliberately never calls state.FindCard, which searches hidden zones too.
        private static ShardsCard FindVisibleCard(ShardsState state, int actor, int instance)
        {
            var own = state.Players[actor];
            ShardsCard found = Find(own.Hand, instance) ?? Find(own.SetAside, instance);
            if (found != null) return found;
            foreach (var p in state.Players)
            {
                found = Find(p.Discard, instance) ?? Find(p.PlayZone, instance) ??
                    Find(p.Champions, instance) ?? Find(p.Destinies, instance);
                if (found != null) return found;
            }
            foreach (var card in state.CenterRow) if (card?.InstanceId == instance) return card;
            return Find(state.DestinyRow, instance) ?? Find(state.ActiveMonsters, instance) ?? Find(state.Banished, instance);
        }

        private static ShardsCard Find(List<ShardsCard> cards, int instance)
        {
            foreach (var card in cards) if (card.InstanceId == instance) return card;
            return null;
        }
    }
}
