using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Into the Horizon (DLC 3): 25 faction center cards + 5 Ingeminex monsters
    /// in the center deck, and 30 Destiny cards (6 dealt face up; take one free at
    /// Mastery 5, once per game).</summary>
    public static class ShardsHorizonSet
    {
        private const string SET = "into_the_horizon";
        private const ShardsFaction A = ShardsFaction.Aion;
        private const ShardsFaction H = ShardsFaction.Homodeus;
        private const ShardsFaction O = ShardsFaction.Order;
        private const ShardsFaction U = ShardsFaction.Undergrowth;
        private const ShardsFaction W = ShardsFaction.Wraethe;

        public static void Register()
        {
            RegisterCenter();
            RegisterIngeminex();
            RegisterDestinies();
        }

        // ------------------------------------------------------------- center (25)

        private static void RegisterCenter()
        {
            SoiCard.New("j_chord", "J-Chord").InSet(SET).Faction(A)
                .Type(ShardsCardType.Champion).Cost(3).Qty(1).Defense(3)
                .Exhausts(new BestByMastery((0, new WarpUpTo(3)), (15, new WarpUpTo(5))))
                .Text("Exhaust — Warp 3: fast-play an ally costing 3 or less for free. M15: Warp 5 instead.")
                .Art("a sound-weaver strumming rifts open like guitar strings").Register();

            SoiCard.New("g_48", "G-48").InSet(SET).Faction(H)
                .Type(ShardsCardType.Champion).Cost(4).Qty(1).Defense(5)
                .Exhausts(new Custom(ResetChampion))
                .Text("Exhaust: reset another champion you control (it may exhaust again this turn).")
                .Art("a maintenance automaton jump-starting a battle frame").Register();

            SoiCard.New("legion_carrier", "Legion Carrier").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(2).Qty(2)
                .Plays(E.Seq(E.Gems(2), new Custom(LegionCarrierFlow)))
                .Text("Gain 2 gems. You may reveal your deck's top 3 cards: up to one revealed champion to your hand, the rest to your discard pile.")
                .Art("a dropship bay disgorging ranks of soldiers onto a landing pad").Register();

            SoiCard.New("torian_commandos", "Torian Commandos").InSet(SET).Faction(H)
                .Type(ShardsCardType.Ally).Cost(3).Qty(3).Shield(4)
                .Plays(E.Mix(gems: 2, power: 2))
                .Text("Shield 4. Gain 2 gems and 2 power.")
                .Art("a strike team rappelling through smoke with riot shields").Register();

            SoiCard.New("anomaly_cleric", "Anomaly Cleric").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(5).Qty(1)
                .Plays(E.Seq(E.Mix(gems: 3, mastery: 1),
                    E.At(10, new Do(ctx => ctx.Controller.NextRecruitsToHand++))))
                .Text("Gain 3 gems and 1 mastery. M10: the next card you recruit this turn goes to your hand.")
                .Art("a cleric calmly cataloguing a floating impossible object").Register();

            SoiCard.New("duplication_fabricator", "Duplication Fabricator").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(1).Qty(2)
                .Plays(E.Seq(E.Mastery(1), new Custom(FabricatorFlow)))
                .Text("Gain 1 mastery. Every player reveals their deck's top card; copy the effect of one revealed ally.")
                .Art("a mirrored forge printing a perfect copy of whatever it sees").Register();

            SoiCard.New("shard_seer", "Shard Seer").InSet(SET).Faction(O)
                .Type(ShardsCardType.Ally).Cost(2).Qty(3)
                .Plays(E.Seq(E.Draw(1), new Custom(ShardSeerFlow)))
                .Text("Draw a card. You may reveal an Infinity Shard from your hand to gain 2 mastery.")
                .Art("a seer gazing into a shard that reflects every possible future").Register();

            SoiCard.New("carnivorous_vine", "Carnivorous Vine").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(4).Qty(2)
                .Plays(E.Seq(E.Mix(health: 3, power: 3),
                    new PerCount(ctx => System.Math.Max(0, ctx.Controller.FactionAllyPlays(U) - 1),
                        health: 2, power: 2)))
                .Text("Gain 3 health and 3 power, plus 2 health and 2 power per OTHER Undergrowth ally you played this turn.")
                .Art("a flowering vine with a fanged bloom swallowing sunlight").Register();

            SoiCard.New("orm_madu", "Orm Madu").InSet(SET).Faction(U)
                .Type(ShardsCardType.Champion).Cost(7).Qty(1).Defense(7)
                .Exhausts(E.Seq(E.Health(7), If.FullHealth(E.Mastery(1))))
                .Text("Exhaust: gain 7 health; then, if you are at 50 health, gain 1 mastery.")
                .Art("a serpent of moss and river-stone coiled around a spring").Register();

            SoiCard.New("the_rotten", "The Rotten").InSet(SET).Faction(U)
                .Type(ShardsCardType.Mercenary).Cost(3).Qty(3)
                .Plays(E.Mix(power: 5, mastery: 1))
                .Text("Gain 5 power and 1 mastery.")
                .Art("a shambling compost titan sprouting new life from decay").Register();

            SoiCard.New("cinder_scars", "Cinder Scars").InSet(SET).Faction(W)
                .Type(ShardsCardType.Mercenary).Cost(2).Qty(3)
                .Plays(E.Seq(E.Draw(1),
                    new If(ctx => ctx.Controller.PlayedThisTurn.Exists(c =>
                        c.DefId == "cinder_scars" && c != ctx.Source), E.Power(3)),
                    E.At(10, new BanishUpTo(1))))
                .Text("Draw a card. If you played another Cinder Scars this turn, gain 3 power. M10: you may banish a card from your hand or discard pile.")
                .Art("smoldering scar-lines of ash tracing a silhouette in the dark").Register();

            SoiCard.New("oblivion_gatekeeper", "Oblivion Gatekeeper").InSet(SET).Faction(W)
                .Type(ShardsCardType.Champion).Cost(4).Qty(1).Defense(5)
                .Exhausts(new Custom(GatekeeperFlow))
                .Text("Exhaust: reveal your deck's top card, lose health equal to its cost (not damage), and put it into your hand. M20: every opponent loses that health instead.")
                .Art("a towering warden holding open a door made of night").Register();

            var dispossessed = SoiCard.New("the_dispossessed", "The Dispossessed").InSet(SET).Faction(W)
                .Type(ShardsCardType.Ally).Cost(1).Qty(2)
                .Plays(E.Power(3))
                .Text("Gain 3 power. While in your discard pile: when you play a Wraethe card, you may return this to your hand.")
                .Art("evicted spirits carrying the doors of their former homes");
            dispossessed.Def.ReturnFromDiscardOnFactionPlay = W;
            dispossessed.Register();
        }

        private static IEnumerable<ShardsStep> ResetChampion(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var exhausted = player.Champions.FindAll(c => c.Exhausted && c != ctx.Source);
            if (exhausted.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Reset a champion you control?",
                Context = "soi.reset",
                Min = 0,
                Max = 1
            };
            foreach (var card in exhausted)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = player.Champions.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen != null)
                chosen.Exhausted = false;
        }

        private static IEnumerable<ShardsStep> LegionCarrierFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var engine = ctx.Engine;
            var confirm = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseMode,
                Title = "Reveal your deck's top 3 cards?",
                Context = "soi.confirm",
                Min = 0,
                Max = 1
            };
            confirm.Options.Add(new DecisionOption(1, "Reveal"));
            yield return ShardsStep.AwaitDecision(confirm);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;

            var revealed = new List<ShardsCard>();
            for (int i = 0; i < 3; i++)
            {
                var top = engine.PeekTopOfDeck(player);
                if (top == null) break;
                player.Deck.Remove(top);
                revealed.Add(top);
            }
            if (revealed.Count == 0) yield break;
            var defIds = new List<string>();
            foreach (var card in revealed) defIds.Add(card.DefId);
            engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = defIds });

            var champions = revealed.FindAll(c => c.Def.IsChampion);
            ShardsCard toHand = null;
            if (champions.Count > 0)
            {
                var pick = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Put one revealed champion into your hand?",
                    Context = "soi.reveal",
                    Min = 0,
                    Max = 1
                };
                foreach (var card in champions)
                    pick.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
                yield return ShardsStep.AwaitDecision(pick);
                if (ctx.Answer.ChosenOptionIds.Count > 0)
                    toHand = champions.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            }
            foreach (var card in revealed)
            {
                if (card == toHand)
                {
                    card.Zone = ShardsZone.Hand;
                    player.Hand.Add(card);
                }
                else
                {
                    card.Zone = ShardsZone.Discard;
                    player.Discard.Add(card);
                }
            }
        }

        private static IEnumerable<ShardsStep> FabricatorFlow(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var state = engine.State;
            var revealed = new List<ShardsCard>();
            var defIds = new List<string>();
            for (int step = 0; step < state.Players.Count; step++)
            {
                var player = state.Players[(state.TurnPlayerIndex + step) % state.Players.Count];
                if (player.Eliminated) continue;
                var top = engine.PeekTopOfDeck(player); // stays on top
                if (top == null) continue;
                revealed.Add(top);
                defIds.Add(top.DefId);
            }
            if (defIds.Count > 0)
                engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = ctx.ControllerIndex, DefIds = defIds });

            var copyable = revealed.FindAll(c => !c.Def.IsChampion && c.Def.PlayEffect != null);
            if (copyable.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = "Copy the effect of a revealed ally",
                Context = "soi.copy",
                Min = 1,
                Max = 1
            };
            foreach (var card in copyable)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            var chosen = copyable.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen == null) yield break;
            foreach (var step in chosen.Def.PlayEffect.Resolve(ctx))
                yield return step;
        }

        private static IEnumerable<ShardsStep> ShardSeerFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var shards = player.Hand.FindAll(c => c.DefId == "infinity_shard");
            if (shards.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Reveal an Infinity Shard to gain 2 mastery?",
                Context = "soi.reveal",
                Min = 0,
                Max = 1
            };
            foreach (var card in shards)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            ctx.Engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = new List<string> { "infinity_shard" } });
            ctx.Engine.GainMastery(player.Index, 2);
        }

        private static IEnumerable<ShardsStep> GatekeeperFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var engine = ctx.Engine;
            var top = engine.PeekTopOfDeck(player);
            if (top == null) yield break;
            engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = new List<string> { top.DefId } });
            int cost = top.Def.Cost;
            player.Deck.Remove(top);
            top.Zone = ShardsZone.Hand;
            player.Hand.Add(top);
            if (cost <= 0) yield break;
            if (player.Mastery >= 20)
            {
                foreach (var opponent in new List<ShardsPlayer>(engine.State.LivingOpponentsOf(player.Index)))
                    engine.LoseHealth(opponent.Index, cost);
            }
            else
            {
                engine.LoseHealth(player.Index, cost);
            }
        }

        // ------------------------------------------------------------- Ingeminex (5)

        private static void RegisterIngeminex()
        {
            SoiCard.New("ingeminex_brutality", "Ingeminex: Brutality").InSet(SET)
                .Faction(ShardsFaction.Monster).Type(ShardsCardType.Monster).Qty(1).Defense(10)
                .MonsterAttack(new AllPlayersLoseHealth(5))
                .Reward(E.Health(20))
                .Text("Attack — at the end of the turn it appeared (cancelled if defeated first): every player loses 5 health (shields can't prevent it). Defeat (10 power): gain 20 health.")
                .Art("a mountainous horned brute cratering the ground with a fist").Register();

            SoiCard.New("ingeminex_corruption", "Ingeminex: Corruption").InSet(SET)
                .Faction(ShardsFaction.Monster).Type(ShardsCardType.Monster).Qty(1).Defense(10)
                .MonsterAttack(E.Seq(new AllPlayersLoseHealth(3), new AllPlayersLoseMastery(1)))
                .Reward(new Custom(CorruptionReward))
                .Text("Attack — at the end of the turn it appeared (cancelled if defeated first): every player loses 3 health and 1 mastery. Defeat (10 power): recruit an additional relic directly to your hand. (Removed when Relics of the Future is off.)")
                .Art("a weeping mass of blackened crystal infecting the ground").Register();

            SoiCard.New("ingeminex_torment", "Ingeminex: Torment").InSet(SET)
                .Faction(ShardsFaction.Monster).Type(ShardsCardType.Monster).Qty(1).Defense(10)
                .MonsterAttack(new AllPlayersLoseMastery(2))
                .Reward(E.Mastery(4))
                .Text("Attack — at the end of the turn it appeared (cancelled if defeated first): every player loses 2 mastery. Defeat (10 power): gain 4 mastery.")
                .Art("a many-armed jailer dragging chains of frozen screams").Register();

            SoiCard.New("ingeminex_agony", "Ingeminex: Agony").InSet(SET)
                .Faction(ShardsFaction.Monster).Type(ShardsCardType.Monster).Qty(1).Defense(10)
                .MonsterAttack(new AllPlayersDiscard(2))
                .Reward(E.Seq(E.Draw(2), new Custom(BonusDestiny)))
                .Text("Attack — at the end of the turn it appeared (cancelled if defeated first): every player discards 2 cards. Defeat (10 power): draw 2 cards and take an additional destiny.")
                .Art("a flayed colossus whose cries bend the air into waves").Register();

            SoiCard.New("ingeminex_malice", "Ingeminex: Malice").InSet(SET)
                .Faction(ShardsFaction.Monster).Type(ShardsCardType.Monster).Qty(1).Defense(10)
                .MonsterAttack(new AllPlayersDestroyBiggestChampion())
                .Reward(E.Seq(new ReturnFromDiscard(d => d.IsChampion, "champion", optional: true), new Custom(BonusDestiny)))
                .Text("Attack — at the end of the turn it appeared (cancelled if defeated first): every player destroys their highest-cost champion. Defeat (10 power): return a champion from your discard pile to your hand and take an additional destiny.")
                .Art("a grinning shadow puppeteer snipping marionette strings").Register();
        }

        private static IEnumerable<ShardsStep> CorruptionReward(ShardsContext ctx)
        {
            // An ADDITIONAL relic: bypasses the once-per-game limit and goes to hand.
            var player = ctx.Controller;
            var relics = player.SetAside.FindAll(c => c.Def.Type == ShardsCardType.Relic);
            if (relics.Count == 0) yield break;
            ShardsCard chosen;
            if (relics.Count == 1)
            {
                chosen = relics[0];
            }
            else
            {
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Recruit an additional relic to your hand",
                    Context = "soi.relic",
                    Min = 1,
                    Max = 1
                };
                foreach (var relic in relics)
                    request.Options.Add(new DecisionOption(relic.InstanceId, relic.Def.Name) { CardInstanceId = relic.InstanceId, DefId = relic.DefId });
                yield return ShardsStep.AwaitDecision(request);
                chosen = relics.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                if (chosen == null) yield break;
            }
            player.SetAside.Remove(chosen);
            chosen.Zone = ShardsZone.Hand;
            player.Hand.Add(chosen);
            ctx.Engine.Emit(new ShardsRelicRecruitedEvent { PlayerIndex = player.Index, DefId = chosen.DefId });
        }

        private static IEnumerable<ShardsStep> BonusDestiny(ShardsContext ctx)
        {
            // Reward destinies ignore Mastery 5 AND the once-per-game limit (FAQ).
            var engine = ctx.Engine;
            var player = ctx.Controller;
            if (engine.State.DestinyRow.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Take an additional destiny from the row",
                Context = "soi.destiny",
                Min = 1,
                Max = 1
            };
            foreach (var destiny in engine.State.DestinyRow)
                request.Options.Add(new DecisionOption(destiny.InstanceId, destiny.Def.Name) { CardInstanceId = destiny.InstanceId, DefId = destiny.DefId });
            yield return ShardsStep.AwaitDecision(request);
            var chosen = engine.State.DestinyRow.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen != null)
                engine.GrantDestiny(player, chosen);
        }

        // ------------------------------------------------------------- Destinies (30)

        private static void RegisterDestinies()
        {
            void Destiny(string id, string name, string text, string art,
                IShardsEffect exhaust = null, System.Action<ShardsCardDef> tweak = null)
            {
                var builder = SoiCard.New(id, name).InSet(SET).Type(ShardsCardType.Destiny).Qty(1)
                    .Text(text).Art(art);
                if (exhaust != null) builder.Exhausts(exhaust);
                tweak?.Invoke(builder.Def);
                builder.Register();
            }

            int DistinctFactions(ShardsContext ctx)
            {
                int n = 0;
                foreach (var f in new[] { H, O, U, W, A })
                    if (ctx.Controller.FactionPlays(f) > 0)
                        n++;
                return n;
            }

            Destiny("datic_secrets", "Datic Secrets",
                "Exhaust: if you played 2+ Order allies this turn, gain 2 gems.",
                "an unlocked archive spilling ribbons of glowing script",
                new If(ctx => ctx.Controller.FactionAllyPlays(O) >= 2, E.Gems(2)));

            Destiny("price_of_power", "The Price of Power",
                "Exhaust: gain 1 power per Wraethe card in your discard pile.",
                "a hand signing a contract written in living shadow",
                new PerCount(ctx => ShardsBaseSet.CountDiscard(ctx, W), power: 1));

            Destiny("one_mind_one_army", "One Mind One Army",
                "Your champions get +2 defense.",
                "ranks of soldiers moving as a single silhouette",
                tweak: def => def.DefenseAura = (owner, source, champion) => 2);

            Destiny("project_yggdrasil", "Project Yggdrasil",
                "Your Wraethe cards also count as Undergrowth and vice versa.",
                "a world-tree whose roots are shadow and crown is starlight");
                // Behavior lives in the engine's CountsAs/CountPlay checks.

            Destiny("phasic_technology", "Phasic Technology",
                "Your Homodeus and Order cards get +2 shield.",
                "a lattice of phased energy panels snapping into place");
                // Behavior lives in the engine's ShieldValue.

            Destiny("blood_for_blood", "Blood for Blood",
                "When you deal 5+ unblocked damage to an opponent, you may banish a card you played this turn.",
                "two crossed blades dripping into the same chalice",
                tweak: def => def.OnDamageDealt = dealt =>
                    dealt >= 5 ? new Custom(BloodForBloodFlow) : null);

            Destiny("bound_for_life", "Bound for Life",
                "Exhaust: ALL players (including you) lose 4 health. Not damage — shields can't prevent it.",
                "chains of light binding every combatant's wrist together",
                new AllPlayersLoseHealth(4));

            Destiny("nature_dominance", "Nature Dominance",
                "Exhaust: gain 1 health and 1 power per Undergrowth card you played this turn.",
                "vines overrunning a plaza, statues wearing crowns of moss",
                new PerCount(ctx => ctx.Controller.FactionPlays(U), health: 1, power: 1));

            Destiny("crystal_gate", "The Crystal Gate",
                "Exhaust: if you played an Order card and an Undergrowth card this turn, recruit a card costing 3 or less for free.",
                "a gate of crystal grown through with flowering vines",
                new If(ctx => ctx.Controller.FactionPlays(O) > 0 && ctx.Controller.FactionPlays(U) > 0,
                    new RecruitFromRow(3)));

            Destiny("forged_in_flame", "Forged in Flame",
                "Exhaust: if you played a Wraethe card and a Homodeus card this turn, banish a card from your hand or discard pile.",
                "a shadowed forge where machine parts are quenched in darkness",
                new If(ctx => ctx.Controller.FactionPlays(W) > 0 && ctx.Controller.FactionPlays(H) > 0,
                    new BanishUpTo(1, optional: false)));

            Destiny("paradigm_shift", "Paradigm Shift",
                "Exhaust: if you played an Order card and a Wraethe card this turn, gain 1 mastery.",
                "a chessboard mid-game where the pieces have swapped colors",
                new If(ctx => ctx.Controller.FactionPlays(O) > 0 && ctx.Controller.FactionPlays(W) > 0,
                    E.Mastery(1)));

            Destiny("deadly_recruits", "Deadly Recruits",
                "Exhaust: fast-play a cost-1 ally from the row for free (you keep it). M20: cost 2 or less.",
                "a recruiter's table where every pen is a knife",
                new BestByMastery((0, new Custom(ctx => DeadlyRecruitsFlow(ctx, 1))),
                                  (20, new Custom(ctx => DeadlyRecruitsFlow(ctx, 2)))));

            Destiny("biotech_enhancements", "Biotech Enhancements",
                "Exhaust: if you played a Homodeus card and an Undergrowth card this turn, draw a card.",
                "chrome limbs grafted with living green circuitry",
                new If(ctx => ctx.Controller.FactionPlays(H) > 0 && ctx.Controller.FactionPlays(U) > 0,
                    E.Draw(1)));

            Destiny("absorption_grid", "Absorption Grid",
                "Exhaust: gain 2 power per shield ally you played this turn.",
                "a grid of humming panels drinking incoming fire",
                new PerCount(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    c.Def.Shield > 0 && !c.Def.IsChampion).Count, power: 2));

            Destiny("maglev_tunnels", "Maglev Tunnels",
                "When you recruit a Homodeus champion, you may put it on top of your deck.",
                "a bullet-tunnel of magnetic rings vanishing to a point",
                tweak: def => def.RedirectChampionRecruitsToDeckTop = true);

            Destiny("soul_syphon", "Soul Syphon",
                "Exhaust: if you played cards of 3+ different factions this turn, gain 5 health.",
                "streams of many-colored light drawn into one vessel",
                new If(ctx => DistinctFactions(ctx) >= 3, E.Health(5)));

            Destiny("agony_of_choice", "The Agony of Choice",
                "Exhaust: if you played cards of 3+ different factions this turn, gain 4 power.",
                "a figure at a crossroads of burning signposts",
                new If(ctx => DistinctFactions(ctx) >= 3, E.Power(4)));

            Destiny("shard_defiant", "The Shard Defiant",
                "Pay 2 gems, Exhaust: reveal the center deck's top card; recruit or banish it. If you played an Aion card this turn, you may repeat this once.",
                "a lone shard hovering against a tide of grasping hands",
                new Custom(ShardDefiantFlow),
                def => def.ExhaustGemCost = 2);

            Destiny("whatever_it_takes", "Whatever it Takes",
                "Pay 6 gems, Exhaust: gain 9 power.",
                "a gauntlet crushing a fortune in gems into raw force",
                E.Power(9),
                def => def.ExhaustGemCost = 6);

            Destiny("the_last_city", "The Last City",
                "Exhaust: if you played 2+ mercenaries this turn, gain 2 gems.",
                "a walled city glowing alone on a darkened plain",
                new If(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    c.Def.Type == ShardsCardType.Mercenary).Count >= 2, E.Gems(2)));

            Destiny("power_struggle", "Power Struggle",
                "Exhaust: destroy a champion you control to gain 5 power.",
                "two hands wrestling over a crown that cuts them both",
                // If wrapper = same gate the flow checks, exposed for the condition glow.
                new If(ctx => ctx.Controller.Champions.Count > 0, new Custom(PowerStruggleFlow)));

            Destiny("unconditional_conscription", "Unconditional Conscription",
                "Exhaust: if you played 2+ non-starter allies costing 2 or less this turn, gain 4 power.",
                "a draft notice nailed to every door of a narrow street",
                new If(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    !c.Def.IsChampion && c.Def.Type != ShardsCardType.Starter && c.Def.Cost <= 2).Count >= 2,
                    E.Power(4)));

            Destiny("stolen_futures", "Stolen Futures",
                "M10 — Exhaust: banish this destiny; add 2 destinies from the deck to the row, then take 2 destinies.",
                "an hourglass being pocketed by a gloved thief",
                E.At(10, new Custom(StolenFuturesFlow)));

            Destiny("strategic_mastermind", "Strategic Mastermind",
                "Exhaust: if you have 40 or more health, draw a card.",
                "a general rearranging a battle map with surgical calm",
                new If(ctx => ctx.Controller.Health >= 40, E.Draw(1)));

            Destiny("advanced_weapons", "Advanced Weapons",
                "Exhaust: if you played 2+ odd-cost cards this turn, gain 3 power. (Cost-less cards are neither even nor odd.)",
                "a rack of impossible weapons still warm from the forge",
                new If(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    c.Def.Cost > 0 && c.Def.Cost % 2 == 1).Count >= 2, E.Power(3)));

            Destiny("advanced_medicine", "Advanced Medicine",
                "Exhaust: if you played 2+ even-cost cards this turn, gain 4 health. (Cost-less cards are neither even nor odd.)",
                "a suture of light closing a wound in mid-air",
                new If(ctx => ctx.Controller.PlayedThisTurn.FindAll(c =>
                    c.Def.Cost > 0 && c.Def.Cost % 2 == 0).Count >= 2, E.Health(4)));

            Destiny("healing_hands", "Healing Hands",
                "Exhaust: if you played a champion this turn, gain 4 health.",
                "radiant palms hovering over a kneeling warrior",
                new If(ctx => ctx.Controller.PlayedThisTurn.Exists(c => c.Def.IsChampion), E.Health(4)));

            Destiny("war_bound", "War Bound",
                "Exhaust: if you control 2+ champions, gain 4 power.",
                "banners of many houses tied to a single war-horn",
                new If(ctx => ctx.Controller.Champions.Count >= 2, E.Power(4)));

            Destiny("true_leader", "True Leader",
                "Exhaust: if you played 3 cards of the same faction this turn, gain 2 mastery.",
                "a commander walking first into the breach, army at their back",
                new If(ctx =>
                {
                    foreach (var f in new[] { H, O, U, W, A })
                        if (ctx.Controller.FactionPlays(f) >= 3)
                            return true;
                    return false;
                }, E.Mastery(2)));

            Destiny("synthesis", "Synthesis",
                "M15 — Exhaust: draw a card.",
                "two opposing energies braided into a single strand",
                E.At(15, E.Draw(1)));
        }

        private static IEnumerable<ShardsStep> BloodForBloodFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var candidates = player.PlayedThisTurn.FindAll(c => c.Zone == ShardsZone.PlayZone);
            if (candidates.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Blood for Blood: banish a card you played this turn?",
                Context = "soi.banish",
                Min = 0,
                Max = 1
            };
            foreach (var card in candidates)
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId, DefId = card.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = candidates.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen != null)
                ctx.Engine.Banish(chosen, player.PlayZone);
        }

        private static IEnumerable<ShardsStep> DeadlyRecruitsFlow(ShardsContext ctx, int maxCost)
        {
            var engine = ctx.Engine;
            var slots = new List<int>();
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
            {
                var card = engine.State.CenterRow[s];
                if (card != null && !card.Def.IsChampion && card.Def.Cost <= maxCost)
                    slots.Add(s);
            }
            if (slots.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Fast-play an ally costing {maxCost} or less for free (you keep it)?",
                Context = "soi.warp",
                Min = 0,
                Max = 1
            };
            foreach (int s in slots)
            {
                var card = engine.State.CenterRow[s];
                request.Options.Add(new DecisionOption(s, card.Def.Name + " (cost " + card.Def.Cost + ")") { CardInstanceId = card.InstanceId, DefId = card.DefId });
            }
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            engine.WarpFromRow(ctx.ControllerIndex, ctx.Answer.ChosenOptionIds[0], keep: true);
        }

        private static IEnumerable<ShardsStep> PowerStruggleFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            if (player.Champions.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = "Destroy a champion you control to gain 5 power?",
                Context = "soi.destroy",
                Min = 0,
                Max = 1
            };
            foreach (var champion in player.Champions)
                request.Options.Add(new DecisionOption(champion.InstanceId, champion.Def.Name) { CardInstanceId = champion.InstanceId, DefId = champion.DefId });
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            var chosen = player.Champions.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
            if (chosen == null) yield break;
            ctx.Engine.DestroyChampion(player, chosen, player.Index);
            ctx.Engine.GainPower(player.Index, 5);
        }

        private static IEnumerable<ShardsStep> ShardDefiantFlow(ShardsContext ctx)
        {
            // The 2-gem payment is the activation COST (ExhaustGemCost) — already paid.
            var player = ctx.Controller;
            var engine = ctx.Engine;

            for (int round = 0; round < 2; round++)
            {
                var card = engine.DrawFromCenterDeck();
                if (card == null) yield break;
                engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = new List<string> { card.DefId } });

                // Mandatory keep-or-banish (user decision 2026-07-19). Both options
                // carry the revealed card so the UI renders the CARD, never just a name.
                var choice = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseMode,
                    Title = $"{card.Def.Name}: recruit it or banish it?",
                    Context = "soi.defiant",
                    Min = 1,
                    Max = 1
                };
                choice.Options.Add(new DecisionOption(1, "Keep") { CardInstanceId = card.InstanceId, DefId = card.DefId });
                choice.Options.Add(new DecisionOption(2, "Banish") { CardInstanceId = card.InstanceId, DefId = card.DefId });
                yield return ShardsStep.AwaitDecision(choice);
                if (ctx.Answer.ChosenOptionIds[0] == 1)
                {
                    engine.RecruitLoose(player, card);
                }
                else
                {
                    card.Zone = ShardsZone.Banished;
                    engine.State.Banished.Add(card);
                    engine.Emit(new ShardsCardBanishedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
                }

                if (round == 0 && ctx.Controller.FactionPlays(A) > 0)
                {
                    var again = new DecisionRequest
                    {
                        PlayerIndex = player.Index,
                        Kind = DecisionKind.ChooseMode,
                        Title = "Repeat the effect once? (you played an Aion card)",
                        Context = "soi.confirm",
                        Min = 0,
                        Max = 1
                    };
                    again.Options.Add(new DecisionOption(1, "Repeat"));
                    yield return ShardsStep.AwaitDecision(again);
                    if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
                }
                else if (round == 0)
                {
                    yield break;
                }
            }
        }

        private static IEnumerable<ShardsStep> StolenFuturesFlow(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var engine = ctx.Engine;
            var self = ctx.Source;
            if (self == null || !player.Destinies.Contains(self)) yield break;

            engine.Banish(self, player.Destinies);
            for (int i = 0; i < 2 && engine.State.DestinyDeck.Count > 0; i++)
            {
                var next = engine.State.DestinyDeck[engine.State.DestinyDeck.Count - 1];
                engine.State.DestinyDeck.RemoveAt(engine.State.DestinyDeck.Count - 1);
                next.Zone = ShardsZone.DestinyRow;
                engine.State.DestinyRow.Add(next);
            }

            for (int pick = 0; pick < 2 && engine.State.DestinyRow.Count > 0; pick++)
            {
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Take a destiny ({pick + 1} of 2)",
                    Context = "soi.destiny",
                    Min = 1,
                    Max = 1
                };
                foreach (var destiny in engine.State.DestinyRow)
                    request.Options.Add(new DecisionOption(destiny.InstanceId, destiny.Def.Name) { CardInstanceId = destiny.InstanceId, DefId = destiny.DefId });
                yield return ShardsStep.AwaitDecision(request);
                var chosen = engine.State.DestinyRow.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                if (chosen != null)
                    engine.GrantDestiny(player, chosen);
            }
        }
    }
}
