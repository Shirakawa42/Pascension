using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class Encoder
    {
        internal const int ObsDim = 2816, MaxActions = 64, ActionDim = 32, CardCapacity = 192;
        internal const string SchemaVersion = "shards-observation-v6";
        internal const int PublicCollectionOffset = 2560;
        internal const int SupplementOffset = 2048;
        internal static readonly string[] ReadinessNames = {
            "datic_secrets", "paradigm_shift", "forged_in_flame", "biotech_enhancements",
            "crystal_gate", "true_leader", "synthesis", "stolen_futures", "primus_pilus_duel",
            "war_bound", "strategic_mastermind", "nature_dominance", "agony_of_choice_duel",
            "soul_syphon_duel", "the_last_city", "unconditional_conscription", "advanced_weapons",
            "advanced_medicine", "healing_hands", "power_struggle", "absorption_grid", "price_of_power",
            "deadly_recruits_duel_cost2", "deadly_recruits_duel_cost4",
            "mastery5", "mastery10", "mastery15", "mastery20", "mastery30",
            "hand_nonempty", "discard_nonempty", "center_has_recruitable_cost3"
        };
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
            HeroFeatures.Encode(game, obs);
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
            EncodeSupplement(game, obs.Slice(SupplementOffset, 512));
            EncodePublicCollection(opponent, obs.Slice(PublicCollectionOffset, 256));
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

        // User-authorized rule: the opponent's COMPLETE permanent collection is
        // public. Its allocation between hand/draw pile and draw order are not.
        // Aggregate into one histogram before emitting any features. Temporary
        // fast-played cards are excluded exactly as in the own FullDeck channel.
        private static void EncodePublicCollection(ShardsPlayer opponent, Span<float> output)
        {
            Span<int> counts = stackalloc int[256]; counts.Clear();
            bool yggdrasil = opponent.Destinies.Exists(c => c.DefId == "project_yggdrasil");
            PublicCollectionList(opponent.Deck, counts, yggdrasil);
            PublicCollectionList(opponent.Hand, counts, yggdrasil);
            PublicCollectionList(opponent.Discard, counts, yggdrasil);
            PublicCollectionList(opponent.PlayZone, counts, yggdrasil, permanentOnly: true);
            PublicCollectionList(opponent.Champions, counts, yggdrasil);
            // Integer aggregation followed by one normalization is essential:
            // floating sums of different costs could encode private zone order
            // through rounding even when the total composition stays identical.
            for (int i = 0; i < MaxCardDefinitions; i++) output[i] = counts[i] / 10f;
            for (int i = 199; i <= 205; i++) output[i] = counts[i] / 20f;
            output[206] = counts[206] / 50f; output[207] = counts[207] / 100f;
            output[208] = counts[208] / 50f;
            for (int i = 209; i <= 230; i++) output[i] = counts[i] / 20f;
            // Allegiance intentionally counts public temporary fast-plays too.
            Shards.FactionCountProbe.FactionCounter.OnePass(opponent, output.Slice(192, 7));
        }

        private static void PublicCollectionList(List<ShardsCard> cards, Span<int> output,
            bool yggdrasil, bool permanentOnly = false)
        {
            foreach (var card in cards)
            {
                if (card == null || permanentOnly && card.FastPlayed) continue;
                int code = CardIndex(card.DefId);
                if (code < 0) throw new InvalidOperationException("Unknown public collection definition " + card.DefId);
                output[code]++;
                var def = card.Def;
                uint factions = Shards.FactionCountProbe.FactionCounter.Membership(def, yggdrasil);
                for (int faction = 0; faction < 7; faction++)
                    if ((factions & (1u << faction)) != 0) output[199 + faction]++;
                output[206]++;
                output[207] += def.Cost;
                output[208] += def.Shield;
                if (def.Shield > 0) output[209]++;
                output[210 + (int)def.Type]++;
                output[217 + Math.Min(13, Math.Max(0, def.Cost))]++;
            }
        }

        // Appended features leave the complete legacy 2048-float prefix intact.
        // Gates describe current payout prerequisites, not action legality. No
        // action is removed merely because one of these values is false.
        private static void EncodeSupplement(Adapter game, Span<float> output)
        {
            var state = game.Engine.State;
            var own = state.Players[game.Actor];
            var enemy = state.Players[1 - game.Actor];
            var source = SupplementKnowledge.AuthorizedSource(game);
            if (source != null)
            {
                output[0] = 1;
                output[1] = (CardIndex(source.DefId) + 1) / 192f;
                output[2] = source.DefId == "reactor_drone_duel" &&
                    source.Owner >= 0 && state.Players[source.Owner].PlayZone.Contains(source) ? 1 : 0;
                output[3] = source.BanishAtCleanup ? 1 : 0;
                output[4] = ((int)source.Zone + 1) / 16f;
            }
            output[5] = own.NextRerollDiscount / 20f;
            output[6] = own.PlayedThisTurn.Count / 20f;
            int playedCinders = 0, playedShields = 0, playedSourceDefinition = 0;
            foreach (var card in own.PlayedThisTurn)
            {
                if (card.DefId == "cinder_scars_duel" || card.DefId == "cinder_scars") playedCinders++;
                if (card.Def.Shield > 0) playedShields++;
                if (source != null && card.DefId == source.DefId) playedSourceDefinition++;
            }
            output[7] = playedCinders / 20f; output[8] = playedShields / 20f;
            output[9] = source != null && own.PlayedThisTurn.Contains(source) ? 1 : 0;
            output[10] = playedSourceDefinition / 20f;
            output[11] = source == null || source.Owner < 0 ? -1 : source.Owner == game.Actor ? 0 : 1;
            int h = own.FactionPlays(ShardsFaction.Homodeus), u = own.FactionPlays(ShardsFaction.Undergrowth),
                o = own.FactionPlays(ShardsFaction.Order), w = own.FactionPlays(ShardsFaction.Wraethe),
                a = own.FactionPlays(ShardsFaction.Aion);
            int shieldAllies = 0, mercenaries = 0, cheapAllies = 0, odd = 0, even = 0, playedChampions = 0;
            foreach (var card in own.PlayedThisTurn)
            {
                var def = card.Def;
                if (def.Shield > 0 && !def.IsChampion) shieldAllies++;
                if (def.Type == ShardsCardType.Mercenary) mercenaries++;
                if (!def.IsChampion && def.Type != ShardsCardType.Starter && def.Cost <= 2) cheapAllies++;
                if (def.Cost > 0) { if ((def.Cost & 1) != 0) odd++; else even++; }
                if (def.IsChampion) playedChampions++;
            }
            int distinct = 0, factionCards = 0, factionMask = 0;
            foreach (var card in own.PlayedThisTurn)
            {
                if (card.Def.Faction == ShardsFaction.None || card.Def.Faction == ShardsFaction.Monster) continue;
                factionCards++;
                for (int faction = 1; faction <= 5; faction++)
                    if (ShardsEngine.CountsAs(own, card.Def, (ShardsFaction)faction)) factionMask |= 1 << faction;
            }
            for (int faction = 1; faction <= 5; faction++) if ((factionMask & (1 << faction)) != 0) distinct++;
            int wraetheDiscard = 0;
            foreach (var card in own.Discard)
                if (ShardsEngine.CountsAs(own, card.Def, ShardsFaction.Wraethe)) wraetheDiscard++;
            bool row2 = false, row4 = false, row3 = false;
            foreach (var card in state.CenterRow)
            {
                if (card == null || card.Def.CannotBeFastPlayed) continue;
                if (card.Def.Cost <= 3) row3 = true;
                if (!card.Def.IsChampion)
                {
                    if (card.Def.Cost <= 2) row2 = true;
                    if (card.Def.Cost <= 4) row4 = true;
                }
            }
            // No enemy condition is evaluated. These are the precise registered
            // own-card predicates (including actual played-card counts for Prism).
            Span<bool> ready = stackalloc bool[] {
                own.FactionAllyPlays(ShardsFaction.Order) >= 2, o > 0 && w > 0,
                w > 0 && h > 0, h > 0 && u > 0, o > 0 && u > 0,
                Math.Max(Math.Max(h,u),Math.Max(Math.Max(o,w),a)) >= 3,
                own.Mastery >= 15, own.Mastery >= 10, own.Champions.Count >= 3,
                own.Champions.Count >= 2, own.Health >= 40, u > 0,
                distinct >= 3 && factionCards >= 3, distinct >= 3 && factionCards >= 3,
                mercenaries >= 2, cheapAllies >= 2, odd >= 2, even >= 2,
                playedChampions > 0, own.Champions.Count > 0, shieldAllies > 0, wraetheDiscard > 0,
                row2, row4, own.Mastery >= 5, own.Mastery >= 10, own.Mastery >= 15,
                own.Mastery >= 20, own.Mastery >= 30, own.Hand.Count > 0, own.Discard.Count > 0, row3
            };
            for (int i = 0; i < ready.Length; i++) output[16 + i] = ready[i] ? 1 : 0;
            for (int relative = 0; relative < 2; relative++)
            {
                var top = game.Supplement.Top(relative == 0 ? game.Actor : 1 - game.Actor);
                for (int i = 0; i < top.Count; i++)
                {
                    output[48 + relative * 8 + i * 2] = (CardIndex(top[i]) + 1) / 192f;
                    output[49 + relative * 8 + i * 2] = 1;
                }
            }
            var acquired = game.Supplement.Acquired(1 - game.Actor);
            for (int i = 0; i < MaxCardDefinitions; i++) output[64 + i] = acquired[i] / 10f;
            int seen = 0;
            SupplementEntities(game, own.Champions, output, ref seen);
            SupplementEntities(game, enemy.Champions, output, ref seen);
            SupplementEntities(game, state.ActiveMonsters, output, ref seen);
            SupplementEntities(game, own.Destinies, output, ref seen);
            SupplementEntities(game, enemy.Destinies, output, ref seen);
            SupplementEntities(game, own.PlayZone, output, ref seen);
            SupplementEntities(game, enemy.PlayZone, output, ref seen);
            output[496] = seen / 64f; output[497] = Math.Max(0, seen - 64) / 64f;
            output[498] = shieldAllies / 20f; output[499] = mercenaries / 20f;
            output[500] = cheapAllies / 20f; output[501] = odd / 20f; output[502] = even / 20f;
            output[503] = playedChampions / 20f; output[504] = distinct / 5f;
            output[505] = factionCards / 20f; output[506] = wraetheDiscard / 20f;
            int cleanupBanish = 0, fastPlayed = 0;
            foreach (var card in own.PlayZone)
            { if (card.BanishAtCleanup) cleanupBanish++; if (card.FastPlayed) fastPlayed++; }
            output[507] = cleanupBanish / 20f; output[508] = fastPlayed / 20f;
        }

        private static void SupplementEntities(Adapter game, List<ShardsCard> cards,
            Span<float> output, ref int seen)
        {
            foreach (var card in cards)
            {
                int index = seen++;
                if (index < 24 || index >= 64) continue;
                int offset = 256 + (index - 24) * 6;
                output[offset] = (CardIndex(card.DefId) + 1) / 192f;
                output[offset + 1] = card.Owner < 0 ? -1 : card.Owner == game.Actor ? 0 : 1;
                output[offset + 2] = card.Exhausted ? 1 : 0;
                output[offset + 3] = card.DamageThisTurn / 50f;
                output[offset + 4] = AuthorizedDefense(game, card) / 50f;
                output[offset + 5] = PublicShield(game, card) / 20f;
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
            output[4] = game.Decision != null ? 1 : 0;
            var d = game.Decision;
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
            // Both permanent collection multisets are public under the corrected
            // user information contract. The only current auras read public
            // hero identity, collection totals + public fast-play, or a constant.
            // A future unreviewed aura must not gain authorization implicitly.
            var owner = game.Engine.State.Players[card.Owner];
            bool reviewed = true;
            foreach (var source in owner.Champions)
                if (source.Def.DefenseAura != null && !ReviewedPublicAura(source.DefId)) reviewed = false;
            foreach (var source in owner.Destinies)
                if (source.Def.DefenseAura != null && !ReviewedPublicAura(source.DefId)) reviewed = false;
            if (reviewed) return game.Engine.EffectiveDefense(owner, card);
            // Fail closed to information already announced by a split contract,
            // or printed defense, if an unreviewed aura enters future content.
            var decision = game.Decision;
            if (decision?.Context == "soi.split")
                foreach (var option in decision.Options)
                    if (option.CardInstanceId == card.InstanceId)
                        return option.Amount + card.DamageThisTurn;
            return card.Def.Defense;
        }

        private static bool ReviewedPublicAura(string id) =>
            id == "ferrata_guard" || id == "ferrata_guard_duel" || id == "one_mind_one_army";

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
