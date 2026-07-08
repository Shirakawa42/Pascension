using Pascension.Content.Effects;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Effects.Common;
using Pascension.Engine.Heroes;
using Pascension.Engine.Targeting;

namespace Pascension.Content.Heroes
{
    // ---- Hero passive statics (level-aware where the L6 upgrade changes the numbers) ----

    /// <summary>Ignis — Kindle: the first card each turn that gives you damage gives +1 more (+2 at level 6).</summary>
    public sealed class KindlePassive : IDamageGainModifier
    {
        public string Description => "Kindle: your first damage card each turn gives bonus damage";

        public int Bonus(GameState state, PlayerState player, int baseAmount, bool fromCardSpell) =>
            fromCardSpell && player.DamageCardsThisTurn == 0 ? (player.Level >= 6 ? 2 : 1) : 0;
    }

    /// <summary>Wren — Pathfinder: your first move each turn goes 1 extra step.</summary>
    public sealed class PathfinderPassive : IMoveBonusModifier
    {
        public string Description => "Pathfinder: your first move each turn goes 1 extra step";

        public int BonusSteps(GameState state, PlayerState player, int paidSteps) =>
            player.MovesThisTurn == 0 ? 1 : 0;
    }

    /// <summary>Wren — Trailblazer (L6): choose 2 inn rewards instead of 1.</summary>
    public sealed class TrailblazerPassive : IInnChoiceCountModifier
    {
        public string Description => "Trailblazer: choose 2 inn rewards instead of 1";

        public int ModifyChoiceCount(GameState state, PlayerState player, int current) =>
            current < 2 ? 2 : current;
    }

    /// <summary>Cornelius — Haggle: first buy each turn costs 1 less (first two buys at level 6).</summary>
    public sealed class HagglePassive : IBuyCostModifier
    {
        public string Description => "Haggle: your first buy each turn costs 1 less";

        public int CostDelta(GameState state, PlayerState buyer, CardDefinition card) =>
            buyer.BuysThisTurn < (buyer.Level >= 6 ? 2 : 1) ? -1 : 0;
    }

    /// <summary>Nyx — Up the Sleeve: keep 1 unplayed card at cleanup (2 at level 6); still draw up to 5 total.</summary>
    public sealed class UpTheSleevePassive : ICleanupKeepModifier
    {
        public string Description => "Up the Sleeve: keep unplayed cards at end of turn";

        public int MaxKeepCount(GameState state, PlayerState player, int current)
        {
            int mine = player.Level >= 6 ? 2 : 1;
            return mine > current ? mine : current;
        }
    }

    /// <summary>The four launch heroes. Registered by ContentRegistry.</summary>
    public static class HeroCatalog
    {
        public static void Register()
        {
            HeroDatabase.Register(new HeroDefinition
            {
                Id = "ignis",
                Name = "Ignis the Pyromancer",
                Archetype = "Damage",
                Description = "A prodigy of destructive magic who solves every problem with more fire.",
                ArtPrompt = "a fierce young pyromancer with short spiky crimson hair and amber eyes, dark red battle robes with ember patterns, fire swirling around one raised hand, confident smirk",
                PassiveStatics = { (1, new KindlePassive()) },
                Active = new ActivatedAbility("Flame Lash: +2 damage", new GainDamageEffect(2))
                    { ApCost = 1, OncePerTurn = true },
                Ultimate = new ActivatedAbility("Inferno: +6 damage", new GainDamageEffect(6))
                    { ApCost = 3, OncePerTurn = true }
            });

            HeroDatabase.Register(new HeroDefinition
            {
                Id = "wren",
                Name = "Wren the Scout",
                Archetype = "Movement",
                Description = "A restless pathfinder who has never met a shortcut she didn't take.",
                ArtPrompt = "an agile scout girl with a long dark braid and green hooded cloak, leather straps and map cases, standing on a cliff edge scanning the horizon, wind-swept, longbow on her back",
                PassiveStatics = { (1, new PathfinderPassive()), (6, new TrailblazerPassive()) },
                Active = new ActivatedAbility("Dash: move 2 steps", new FreeMoveEffect(2))
                    { ApCost = 1, OncePerTurn = true },
                Ultimate = new ActivatedAbility("Blitz: move 4 steps, draw a card",
                        new CompositeEffect(new FreeMoveEffect(4), new DrawCardsEffect(1)))
                    { ApCost = 4, OncePerTurn = true }
            });

            HeroDatabase.Register(new HeroDefinition
            {
                Id = "cornelius",
                Name = "Cornelius the Merchant",
                Archetype = "Economy",
                Description = "A silver-tongued trader convinced that every dungeon is just an unexploited market.",
                ArtPrompt = "a portly cheerful merchant with a magnificent waxed mustache, plum velvet coat with gold buttons, weighing a gem on a small scale, coins and ledgers, warm shop light",
                PassiveStatics = { (1, new HagglePassive()) },
                Active = new ActivatedAbility("Trade: discard a card, gain 2 AP", new TradeEffect())
                    { ApCost = 0, OncePerTurn = true, UsableIf = (state, p) => p.Hand.Count > 0 },
                Ultimate = new ActivatedAbility("Express Delivery: your next buy this turn goes to your hand",
                        new ExpressDeliveryEffect())
                    { ApCost = 2, OncePerTurn = true }
            });

            HeroDatabase.Register(new HeroDefinition
            {
                Id = "nyx",
                Name = "Nyx the Trickster",
                Archetype = "Control",
                Description = "A grinning shadow who wins games nobody else realized were being played.",
                ArtPrompt = "a sly androgynous trickster with silver-white hair and mismatched purple and gold eyes, dark harlequin-inspired coat, juggling glowing cards between fingers, playful smile, moonlit rooftop",
                PassiveStatics = { (1, new UpTheSleevePassive()) },
                Active = new ActivatedAbility("Hex: target monster gets -2 HP until end of turn",
                        new ModifyMonsterHpEffect(-2, ModifierDuration.EndOfTurn),
                        TargetSpec.Monster("Hex target monster"))
                    { ApCost = 2, OncePerTurn = true },
                Ultimate = new ActivatedAbility("Master Plan: draw 3 cards", new DrawCardsEffect(3))
                    { ApCost = 3, OncePerTurn = true }
            });
        }
    }
}
