using System;
using System.Collections.Generic;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Weight-independent atoms extracted from one effect slot at one mastery
    /// bucket: resource sums per condition class, PerCount per-unit rates, and
    /// structural capabilities. Everything a tuned value model needs, no lambdas.</summary>
    public sealed class EffectAtoms
    {
        public const int Unconditional = 0, UnifyClass = 1, DominionClass = 2, IfClass = 3, FactionClass = 4;
        public const int Gems = 0, Power = 1, Mastery = 2, Health = 3, Draw = 4;

        /// <summary>[conditionClass, resource] resource totals.</summary>
        public readonly double[,] Gains = new double[5, 5];
        /// <summary>PerCount per-unit amounts (units are a tuned expectation).</summary>
        public readonly double[] PerUnit = new double[5];

        public int Warps;              // WarpUpTo count in the slot
        public int WarpMaxCost;
        public int RecruitsRow;        // RecruitFromRow count (mandatory pollution!)
        public int RecruitMaxCost;
        public int DestroysChampions;  // targeted destroy effects
        public bool DestroysAllChampions;
        public int BanishCapacity;     // deck-thinning potential
        public bool ReturnsFromDiscard;
        public int CopyEffects;        // Ojas/Taur/Fabricator style
        public int OppMasteryLoss;
        public int AllLoseHealth;      // symmetric burn (aggression)
        public int AllLoseMastery;
        /// <summary>Contains a Custom/Do with NO annotation — the guard test fails on these.</summary>
        public bool Opaque;

        public void AddGain(int conditionClass, int gems, int power, int mastery, int health, int draw, double mult)
        {
            Gains[conditionClass, Gems] += gems * mult;
            Gains[conditionClass, Power] += power * mult;
            Gains[conditionClass, Mastery] += mastery * mult;
            Gains[conditionClass, Health] += health * mult;
            Gains[conditionClass, Draw] += draw * mult;
        }
    }

    /// <summary>Per-def, per-mastery-bucket atoms for the play/exhaust/reward slots.
    /// Buckets are exact: every AtMastery/BestByMastery threshold in the content is a
    /// multiple of 5 (0,5,…,30 → 7 buckets). Built once per def, shared and cached.</summary>
    public sealed class CardStatics
    {
        public const int Buckets = 7; // mastery 0,5,10,15,20,25,30

        public ShardsCardDef Def;
        public EffectAtoms[] Play = new EffectAtoms[Buckets];
        public EffectAtoms[] Exhaust = new EffectAtoms[Buckets];
        public EffectAtoms[] Reward = new EffectAtoms[Buckets];

        public static int BucketOf(int mastery) => Math.Min(Buckets - 1, Math.Max(0, mastery / 5));
    }

    public static class ShardsCardStatics
    {
        private static readonly Dictionary<ShardsCardDef, CardStatics> Cache = new();
        private static readonly object Lock = new();

        public static CardStatics Get(ShardsCardDef def)
        {
            lock (Lock)
            {
                if (Cache.TryGetValue(def, out var statics))
                    return statics;
                statics = Build(def);
                Cache[def] = statics;
                return statics;
            }
        }

        private static CardStatics Build(ShardsCardDef def)
        {
            var statics = new CardStatics { Def = def };
            for (int b = 0; b < CardStatics.Buckets; b++)
            {
                int mastery = b * 5;
                statics.Play[b] = WalkSlot(def, def.PlayEffect, "play", mastery);
                statics.Exhaust[b] = WalkSlot(def, def.ExhaustEffect, "exhaust", mastery);
                statics.Reward[b] = WalkSlot(def, def.RewardEffect, "reward", mastery);
            }
            return statics;
        }

        private static EffectAtoms WalkSlot(ShardsCardDef def, IShardsEffect effect, string slot, int mastery)
        {
            var atoms = new EffectAtoms();
            if (effect == null) return atoms;
            Walk(def, effect, slot, mastery, atoms, EffectAtoms.Unconditional, 1.0);
            return atoms;
        }

        private static void Walk(ShardsCardDef def, IShardsEffect effect, string slot, int mastery,
            EffectAtoms atoms, int conditionClass, double mult)
        {
            switch (effect)
            {
                case null:
                    return;
                case Gain gain:
                    atoms.AddGain(conditionClass, gain.Gems, gain.Power, gain.Mastery, gain.Health, gain.Draw, mult);
                    return;
                case ShardsComposite composite:
                    foreach (var part in composite.Parts)
                        Walk(def, part, slot, mastery, atoms, conditionClass, mult);
                    return;
                case AtMastery tier:
                    if (mastery >= tier.Threshold)
                        Walk(def, tier.Inner, slot, mastery, atoms, conditionClass, mult);
                    return;
                case BestByMastery best:
                {
                    IShardsEffect chosen = null;
                    int chosenThreshold = int.MinValue;
                    foreach (var (threshold, inner) in best.Tiers)
                        if (mastery >= threshold && threshold >= chosenThreshold)
                        {
                            chosenThreshold = threshold;
                            chosen = inner;
                        }
                    Walk(def, chosen, slot, mastery, atoms, conditionClass, mult);
                    return;
                }
                case Unify unify:
                    Walk(def, unify.Inner, slot, mastery, atoms, EffectAtoms.UnifyClass, mult);
                    return;
                case Dominion dominion:
                    Walk(def, dominion.Inner, slot, mastery, atoms, EffectAtoms.DominionClass, mult);
                    return;
                case If conditional:
                    Walk(def, conditional.Inner, slot, mastery, atoms, EffectAtoms.IfClass, mult);
                    return;
                case FactionTrigger trigger:
                    Walk(def, trigger.Inner, slot, mastery, atoms, EffectAtoms.FactionClass, mult);
                    return;
                case PerCount per:
                {
                    var unit = per.PerUnit;
                    atoms.PerUnit[EffectAtoms.Gems] += unit.gems * mult;
                    atoms.PerUnit[EffectAtoms.Power] += unit.power * mult;
                    atoms.PerUnit[EffectAtoms.Mastery] += unit.mastery * mult;
                    atoms.PerUnit[EffectAtoms.Health] += unit.health * mult;
                    atoms.PerUnit[EffectAtoms.Draw] += unit.draw * mult;
                    return;
                }
                case WarpUpTo warp:
                    atoms.Warps++;
                    atoms.WarpMaxCost = Math.Max(atoms.WarpMaxCost, warp.MaxCost < 0 ? 99 : warp.MaxCost);
                    return;
                case RecruitFromRow recruit:
                    atoms.RecruitsRow++;
                    atoms.RecruitMaxCost = Math.Max(atoms.RecruitMaxCost, recruit.MaxCost);
                    return;
                case DestroyEnemyChampions destroy:
                    atoms.DestroysChampions++;
                    // The "all" flag is private; Ghostwillow M15 is the only all-destroy
                    // and its atoms differ little for valuation. Count is enough.
                    _ = destroy;
                    return;
                case BanishUpTo banish:
                    atoms.BanishCapacity += banish.Count;
                    return;
                case ReturnFromDiscard:
                    atoms.ReturnsFromDiscard = true;
                    return;
                case CopyPlayedEffect copy:
                    atoms.CopyEffects += copy.Copies;
                    return;
                case OpponentLosesMastery loss:
                    atoms.OppMasteryLoss += loss.Amount;
                    return;
                case AllPlayersLoseHealth burn:
                    atoms.AllLoseHealth += burn.Amount;
                    return;
                case AllPlayersLoseMastery drain:
                    atoms.AllLoseMastery += drain.Amount;
                    return;
                case AllPlayersDiscard:
                case AllPlayersDestroyBiggestChampion:
                    // Symmetric disruption — folded into the annotation channel when a
                    // card needs finer credit; structurally neutral here.
                    return;
                case Custom:
                case Do:
                    if (ShardsCustomAnnotations.TryApply(def.Id, slot, atoms, mastery, mult))
                        return;
                    atoms.Opaque = true;
                    return;
                default:
                    // A NEW effect class nobody taught the walker about: flag it loudly —
                    // the guard test fails and points here.
                    atoms.Opaque = true;
                    return;
            }
        }
    }
}
