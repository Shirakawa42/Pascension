using System;
using System.Collections.Generic;

namespace Shards.Bots
{
    /// <summary>Hand-maintained value annotations for Custom/Do effect nodes — the one
    /// place the statics walker cannot see through. Keyed "defId:slot". Coarse resource
    /// equivalents only (the tuner owns fine-grained weighting). The guard test
    /// (ShardsCardStaticsTests) FAILS whenever a def contains an unannotated Custom/Do —
    /// that failure is the reminder to add a line here when a balance patch or new DLC
    /// introduces bespoke logic.
    ///
    /// Note: a Custom nested under AtMastery/BestByMastery is only reached by the walker
    /// in buckets where it fires, so most annotations need no mastery check of their own.
    /// The `mastery` parameter exists for RUNTIME branches inside a flow (Gatekeeper).</summary>
    public static class ShardsCustomAnnotations
    {
        private static readonly Dictionary<string, Action<EffectAtoms, int, double>> Table = new()
        {
            // Aion card from discard to deck top — draw-velocity.
            ["dash:play"] = (a, m, w) => Add(a, draw: 0.4, w: w),

            // M20 extra turn, once per game (walker only reaches this at bucket ≥ 20).
            ["slipstream_shard:play"] = (a, m, w) => Add(a, draw: 2.5, w: w),

            // M20: banish up to 3 allies from hand/discard AND gain their effects.
            ["warpquartz:play"] = (a, m, w) =>
            {
                a.BanishCapacity += 3;
                Add(a, gems: 1.5, power: 1.5, w: w);
            },

            // Reset another champion — roughly a second exhaust's worth of value.
            ["g_48:exhaust"] = (a, m, w) => Add(a, power: 1.5, w: w),

            // Reveal top 3, a champion to hand, rest to discard — velocity.
            ["legion_carrier:play"] = (a, m, w) => Add(a, draw: 0.4, w: w),

            // M10: next recruit this turn goes to hand.
            ["anomaly_cleric:play"] = (a, m, w) => Add(a, draw: 0.5, w: w),

            // Copy the effect of a revealed ally.
            ["duplication_fabricator:play"] = (a, m, w) => a.CopyEffects += 1,

            // May reveal an Infinity Shard for +2 mastery (needs it in hand).
            ["shard_seer:play"] = (a, m, w) => Add(a, mastery: 0.5, w: w),

            // Top card to hand, lose health = its cost; M20: opponents lose it instead.
            ["oblivion_gatekeeper:exhaust"] = (a, m, w) =>
            {
                Add(a, draw: 1, health: m >= 20 ? 0 : -2, w: w);
                if (m >= 20) a.AllLoseHealth += 2;
            },

            // Ingeminex rewards: an EXTRA relic to hand / an extra destiny.
            ["ingeminex_corruption:reward"] = (a, m, w) => Add(a, gems: 2, draw: 1, w: w),
            ["ingeminex_agony:reward"] = (a, m, w) => Add(a, draw: 0.7, w: w),
            ["ingeminex_malice:reward"] = (a, m, w) => Add(a, draw: 0.7, w: w),

            // Fast-play a cheap row ally for free and KEEP it (cost ≤1, M20 ≤2).
            ["deadly_recruits:exhaust"] = (a, m, w) =>
            {
                a.Warps++;
                a.WarpMaxCost = Math.Max(a.WarpMaxCost, m >= 20 ? 2 : 1);
            },

            // Reveal center top, recruit or banish it (Aion may repeat).
            ["shard_defiant:exhaust"] = (a, m, w) => Add(a, gems: 1.5, w: w),

            // Destroy an own champion for 5 power — net of the champion cost.
            ["power_struggle:exhaust"] = (a, m, w) => Add(a, power: 2, w: w),

            // M10: banish this destiny, add 2 to the row, take 2.
            ["stolen_futures:exhaust"] = (a, m, w) => Add(a, mastery: 0.8, draw: 0.8, w: w),

            // M20: copy each Homodeus ally played this turn.
            ["general_decurion:exhaust"] = (a, m, w) => Add(a, gems: 1, power: 2, w: w),

            // Next Homodeus champion recruit enters play directly.
            ["numeri_drones:exhaust"] = (a, m, w) => Add(a, power: 0.8, w: w),

            // M20: double your power (typical mid-turn pools run 3-6).
            ["fao_cutul:exhaust"] = (a, m, w) => Add(a, power: 3, w: w),

            // M10: damage ignores shields this turn.
            ["ru_bo_vai:exhaust"] = (a, m, w) => Add(a, power: 1, w: w),

            // Health gains convert to power this turn.
            ["entropic_talons:play"] = (a, m, w) => Add(a, power: 1.5, w: w),

            // Draw 3 at end of turn after a 10+ damage hit on one opponent.
            ["heart_of_nothing:play"] = (a, m, w) => Add(a, draw: 0.8, w: w),

            // ---- Duel of Doom ----
            // Inspire: the next Homodeus champion recruit enters play directly.
            ["century_forge:play"] = (a, m, w) => Add(a, power: 0.8, w: w),
            // This turn, over-cap healing becomes power (marginal unless near full).
            ["nectar_alchemist:play"] = (a, m, w) => Add(a, power: 0.5, w: w),
            // This turn, all healing is doubled (proxy — depends on other heals).
            ["lifebloom_ritual:play"] = (a, m, w) => Add(a, health: 1.5, w: w),
            // Destroy target opponent — a lethal finisher (costs 14 gems).
            ["comet:play"] = (a, m, w) => Add(a, power: 12, w: w),
            // Lose 4 health, draw 2 (Echo redirects the loss to an opponent).
            ["bleak_communion:play"] = (a, m, w) => Add(a, draw: 2, health: -1, w: w),

            // ---- Duel of Doom errata (new def ids need their own annotations) ----
            ["slipstream_shard_duel:play"] = (a, m, w) => Add(a, draw: 2.5, w: w),
            ["heart_of_nothing_duel:play"] = (a, m, w) => Add(a, draw: 0.8, w: w),
            ["ru_bo_vai_duel:exhaust"] = (a, m, w) => Add(a, power: 1, w: w),
            ["reactor_drone_duel:play"] = (a, m, w) => Add(a, gems: 4, w: w),
            ["order_initiate_duel:play"] = (a, m, w) => Add(a, gems: 0.2, w: w), // free shop-removal, denial
            ["spore_cleric_duel:play"] = (a, m, w) => Add(a, health: 4.5, w: w),
            ["warpquartz_duel:play"] = (a, m, w) => { a.BanishCapacity += 1; Add(a, gems: 2, power: 2, w: w); },
            ["duplication_fabricator_duel:play"] = (a, m, w) => a.CopyEffects += 1,
            ["dash_duel:play"] = (a, m, w) => Add(a, draw: 0.4, w: w),
            ["world_piercer_duel:play"] = (a, m, w) => { a.ReturnsFromDiscard = true; Add(a, draw: 0.5, w: w); },
            ["legion_carrier_duel:play"] = (a, m, w) => Add(a, draw: 0.4, w: w),
            ["deadly_recruits_duel:exhaust"] = (a, m, w) => { a.Warps++; a.WarpMaxCost = Math.Max(a.WarpMaxCost, m >= 20 ? 4 : 2); },
            ["praetorian_02_duel:exhaust"] = (a, m, w) => Add(a, health: 1, w: w), // shields doubled — defensive
            // Doom Gate: shuffle 20 Ingeminex (board swing, hard to value) / destroy one.
            ["doom_gate:play"] = (a, m, w) => Add(a, w: w),
            ["doom_gate:exhaust"] = (a, m, w) => Add(a, w: w),
            // Riposte Doctrine: +3 more power when a shield was played/revealed (~common).
            ["riposte_doctrine:play"] = (a, m, w) => Add(a, power: 1.5, w: w),
            // Longshot: reveal top 2, fast-play cheap allies (free tempo).
            ["longshot:play"] = (a, m, w) => Add(a, power: 1, gems: 0.5, w: w),
            // Grim Tutor: fetch your best card now — draw-quality worth more than a draw —
            // at 3 life (a loss, not damage).
            ["grim_tutor:play"] = (a, m, w) => Add(a, draw: 1.4, health: -3, w: w),
        };

        public static bool TryApply(string defId, string slot, EffectAtoms atoms, int mastery, double mult)
        {
            if (!Table.TryGetValue(defId + ":" + slot, out var apply))
                return false;
            apply(atoms, mastery, mult);
            return true;
        }

        /// <summary>Fractional-resource helper (AddGain takes ints).</summary>
        private static void Add(EffectAtoms a,
            double gems = 0, double power = 0, double mastery = 0, double health = 0, double draw = 0, double w = 1)
        {
            a.Gains[EffectAtoms.Unconditional, EffectAtoms.Gems] += gems * w;
            a.Gains[EffectAtoms.Unconditional, EffectAtoms.Power] += power * w;
            a.Gains[EffectAtoms.Unconditional, EffectAtoms.Mastery] += mastery * w;
            a.Gains[EffectAtoms.Unconditional, EffectAtoms.Health] += health * w;
            a.Gains[EffectAtoms.Unconditional, EffectAtoms.Draw] += draw * w;
        }

    }
}
