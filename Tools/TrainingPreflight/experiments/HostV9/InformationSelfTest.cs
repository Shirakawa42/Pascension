using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class InformationSelfTest
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int _checks;
        private static void Check(bool good, string label)
        { _checks++; if (!good) throw new InvalidOperationException("Information: " + label); }
        private static Adapter Fixture(ulong seed = 925726)
        {
            var g = new Adapter(seed);
            while (g.Decision?.Context == "soi.herodraft") g.Step(0);
            return g;
        }
        private static ShardsCard Add(Adapter g, ShardsPlayer p, string id, ShardsZone zone, List<ShardsCard> target)
        {
            var card = new ShardsCard { InstanceId = g.Engine.State.NextInstanceId++, Owner = p.Index,
                DefId = id, Zone = zone };
            target.Add(card); return card;
        }
        private static void Refresh(Adapter g)
        {
            g.Engine.State.InvalidateCardIndex();
            if (g.Decision == null) g.Engine.PendingInput.LegalActions = g.Engine.LegalActions(g.Actor);
            g.Rebuild();
        }
        private static float[] Encode(Adapter g)
        {
            var flat = new float[Encoder.ObsDim + 2048 + 64];
            Encoder.Encode(g, flat.AsSpan(0, Encoder.ObsDim), flat.AsSpan(Encoder.ObsDim, 2048),
                flat.AsSpan(Encoder.ObsDim + 2048, 64)); return flat;
        }
        private static void Submit(Adapter g, PlayerAction action) =>
            typeof(Adapter).GetMethod("Submit", Private).Invoke(g, new object[] { action });
        private static Adapter OwnTop(bool reverse)
        {
            var g = Fixture(); var p = g.Engine.State.TurnPlayer; var enemy = g.Engine.State.Players[1 - p.Index];
            p.Hand.Clear(); p.Deck.Clear(); p.Discard.Clear(); p.PlayZone.Clear(); p.Champions.Clear();
            p.ResetTurn(); p.Mastery = 5; enemy.Deck.Clear();
            Add(g, enemy, "crystal", ShardsZone.Deck, enemy.Deck);
            Add(g, p, "crystal", ShardsZone.Deck, p.Deck); Add(g, p, "blaster", ShardsZone.Deck, p.Deck);
            if (reverse) p.Deck.Reverse();
            var fab = Add(g, p, "duplication_fabricator_duel", ShardsZone.Hand, p.Hand);
            Add(g, p, "cinder_scars_duel", ShardsZone.Hand, p.Hand); Refresh(g);
            Submit(g, new ShardsPlayCardAction { PlayerIndex = p.Index, CardInstanceId = fab.InstanceId });
            Check(g.Decision?.Context == "soi.copy", "Accepted Fabricator action reaches revealed copy");
            var option = g.Decision.Options.Single(o => o.CardInstanceId == enemy.Deck[0].InstanceId);
            Submit(g, new SubmitDecisionAction { PlayerIndex = p.Index,
                Answer = new DecisionAnswer { DecisionId = g.Decision.Id, ChosenOptionIds = new List<int> { option.Id } } });
            return g;
        }
        private static Adapter Reactor(bool copied)
        {
            var g = Fixture(926726); var p = g.Engine.State.TurnPlayer;
            p.Hand.Clear(); p.PlayZone.Clear(); p.ResetTurn();
            var drone = Add(g, p, "reactor_drone_duel", ShardsZone.PlayZone, p.PlayZone);
            var ojas = Add(g, p, "ojas_genesis_druid", ShardsZone.PlayZone, p.PlayZone);
            p.PlayedThisTurn.Add(drone); p.PlayedThisTurn.Add(ojas);
            p.CountFactionPlay(drone.Def.Faction, true); p.CountFactionPlay(ojas.Def.Faction, true); Refresh(g);
            typeof(ShardsEngine).GetMethod("QueueEffect", Private).Invoke(g.Engine,
                new object[] { drone.Def.PlayEffect, p.Index, copied ? ojas : drone });
            // Inspect the parked source before Rebuild's copied-Reactor automatic
            // choice. This is an encoder fixture, not a submitted adapter menu.
            typeof(ShardsEngine).GetMethod("Pump", Private).Invoke(g.Engine, null);
            Check(g.Decision?.Context == "soi.mode", "Parked actual Reactor effect has mode choice");
            return g;
        }
        internal static void Run()
        {
            _checks = 0;
            var a = OwnTop(false); var b = OwnTop(true); var xa = Encode(a); var xb = Encode(b);
            Check(xa.Take(2048).SequenceEqual(xb.Take(2048)), "Known-top fixture preserves old alias");
            Check(!xa.SequenceEqual(xb), "Remembered public own tops remove old alias");
            Check(a.Supplement.Top(a.Actor).Single() == "blaster", "Public Fabricator remembers own top");
            Check(a.Supplement.Top(1 - a.Actor).Single() == "crystal", "Public Fabricator remembers enemy top");
            a.Engine.State.Players[a.Actor].Deck.Reverse();
            Check(xa.SequenceEqual(Encode(a)), "No hidden deck repair or order leak after public reveal");
            a = Reactor(false); b = Reactor(true); xa = Encode(a); xb = Encode(b);
            Check(xa.Take(2048).SequenceEqual(xb.Take(2048)), "Parked source fixture preserves legacy observation prefix");
            Check(xa[2050] == 1 && xb[2050] == 0, "Actual source distinguishes paid versus free Reactor banish mode");
            Check(!xa.SequenceEqual(xb), "Effect source removes old Reactor alias");
            var enemy = a.Engine.State.Players[1 - a.Actor];
            // Full composition is public; only its hand/draw allocation and
            // order are private. Preserve that aggregate while perturbing them.
            if (enemy.Hand.Count > 0 && enemy.Deck.Count > 0)
            {
                var swap = enemy.Hand[0]; enemy.Hand[0] = enemy.Deck[0]; enemy.Deck[0] = swap;
                enemy.Hand[0].Zone = ShardsZone.Hand; enemy.Deck[0].Zone = ShardsZone.Deck;
            }
            enemy.Hand.Reverse(); enemy.Deck.Reverse();
            a.Engine.State.CenterDeck.Reverse();
            Check(xa.SequenceEqual(Encode(a)), "Entire source decision encoding invariant to hidden enemy allocation/order");
            var context = (ShardsContext)typeof(ShardsEngine).GetField("_activeContext", Private).GetValue(a.Engine);
            context.Source = enemy.Hand[0];
            Check(Encode(a)[2048] == 0, "Hidden opponent source is never encoded");
            FabricatorNoCopy(); MemoryEvents(); Gates(); EntityOverflow();
            Program.Print(new { passed = true, checks = _checks, observationSchema = Encoder.SchemaVersion,
                fixtures = "accepted-Fabricator-known-top/parked-Reactor-source/hidden-permutation/public-memory-events/readiness/public-entities-24-to-64",
                limitations = "Constructed positions; personal top capacity4; public entity capacity64" });
        }
        private static void FabricatorNoCopy()
        {
            var g = Fixture(925735); var p = g.Engine.State.Players[g.Actor]; var enemy = g.Engine.State.Players[1-g.Actor];
            p.Hand.Clear(); p.Deck.Clear(); enemy.Deck.Clear(); p.Discard.Clear(); enemy.Discard.Clear();
            Add(g,p,"primus_pilus_duel",ShardsZone.Deck,p.Deck);
            Add(g,enemy,"ferrata_guard_duel",ShardsZone.Deck,enemy.Deck);
            var f=Add(g,p,"duplication_fabricator_duel",ShardsZone.Hand,p.Hand); Refresh(g);
            Submit(g,new ShardsPlayCardAction { PlayerIndex=p.Index, CardInstanceId=f.InstanceId });
            Check(g.Decision == null, "All-champion Fabricator has no copy decision");
            Check(g.Supplement.Top(p.Index).Single()=="primus_pilus_duel" &&
                g.Supplement.Top(enemy.Index).Single()=="ferrata_guard_duel", "All-champion revealed tops remembered without decision");
            int start=g.Engine.Log.Count;
            g.Supplement.BeforeSubmit(g.Engine,new ShardsFocusAction { PlayerIndex=p.Index });
            g.Engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex=p.Index,DefIds=new List<string>{"crystal"} });
            g.Supplement.AfterSubmit(g.Engine,start);
            Check(g.Supplement.Top(p.Index).Single()=="primus_pilus_duel", "Unrelated hand reveal cannot replace known top");
            start=g.Engine.Log.Count;
            g.Supplement.BeforeSubmit(g.Engine,new ShardsFocusAction { PlayerIndex=p.Index });
            p.Deck.RemoveAt(p.Deck.Count-1);
            g.Supplement.AfterSubmit(g.Engine,start);
            Check(g.Supplement.Top(p.Index).Count==0, "Unmodeled public deck removal invalidates stale top");
        }
        private static void MemoryEvents()
        {
            var g = Fixture(925730); int seat = g.Actor; var k = g.Supplement;
            int start = g.Engine.Log.Count;
            k.BeforeSubmit(g.Engine, new ShardsFocusAction { PlayerIndex=seat });
            Add(g,g.Engine.State.Players[seat],"blaster",ShardsZone.Deck,g.Engine.State.Players[seat].Deck);
            Add(g,g.Engine.State.Players[seat],"crystal",ShardsZone.Deck,g.Engine.State.Players[seat].Deck);
            g.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = seat, DefId = "blaster", ToDeckTop = true });
            g.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = seat, DefId = "crystal", ToDeckTop = true });
            k.AfterSubmit(g.Engine, start);
            Check(k.Top(seat).SequenceEqual(new[] { "crystal", "blaster" }), "Public top returns preserve order");
            start = g.Engine.Log.Count;
            k.BeforeSubmit(g.Engine, new ShardsFocusAction { PlayerIndex=seat });
            g.Engine.State.Players[seat].Deck.RemoveAt(g.Engine.State.Players[seat].Deck.Count-1);
            g.Engine.Emit(new ShardsCardDrawnEvent { PlayerIndex = seat, DefId = "NOT_AUTHORIZED_OR_USED" });
            k.AfterSubmit(g.Engine, start);
            Check(k.Top(seat).SequenceEqual(new[] { "blaster" }), "Draw consumes memory without inspecting hidden identity");
            start = g.Engine.Log.Count; g.Engine.Emit(new ShardsDeckShuffledEvent { PlayerIndex = seat }); k.AfterSubmit(g.Engine, start);
            Check(k.Top(seat).Count == 0, "Shuffle invalidates known order");
            start = g.Engine.Log.Count;
            g.Engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = 1 - seat, DefId = "fungal_hermit", FastPlay = false });
            g.Engine.Emit(new ShardsCardBoughtEvent { PlayerIndex = 1 - seat, DefId = "fungal_hermit", FastPlay = true });
            k.AfterSubmit(g.Engine, start);
            Check(k.Acquired(1 - seat)[Encoder.CardIndex("fungal_hermit")] == 1, "Acquisition history separates permanent buys from fast-play");
        }
        private static void Gates()
        {
            string[] ids = { "datic_secrets_duel", "paradigm_shift_duel", "forged_in_flame", "biotech_enhancements",
                "crystal_gate", "true_leader", "synthesis", "stolen_futures", "primus_pilus_duel", "war_bound",
                "strategic_mastermind", null, "agony_of_choice_duel", "soul_syphon_duel", "the_last_city_duel",
                "unconditional_conscription", "advanced_weapons", "advanced_medicine", "healing_hands_duel", "power_struggle" };
            var g = Fixture(925731); var p = g.Engine.State.Players[g.Actor];
            for (int step = 0; step < 40; step++)
            {
                p.ResetTurn(); p.Mastery = step; p.Health = step + 20; p.Champions.Clear(); p.PlayZone.Clear();
                for (int n = 0; n < step % 5; n++)
                    Add(g, p, "primus_pilus_duel", ShardsZone.Champions, p.Champions);
                string[] played = { "reactor_drone_duel", "fungal_hermit", "ojas_genesis_druid", "cinder_scars_duel", "crystal" };
                for (int n = 0; n < step % 11; n++)
                {
                    var card = Add(g, p, played[n % played.Length], ShardsZone.PlayZone, p.PlayZone);
                    p.PlayedThisTurn.Add(card); p.CountFactionPlay(card.Def.Faction, !card.Def.IsChampion);
                }
                var obs = Encode(g);
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == null) continue;
                    var def = ShardsCardDatabase.Get(ids[i]);
                    Check(def.ExhaustEffect is IShardsConditionalEffect || def.ExhaustEffect is AtMastery,
                        "Gate fixture is conditional: " + ids[i]);
                    bool expected = def.ExhaustEffect is AtMastery at ? p.Mastery >= at.Threshold :
                        ((IShardsConditionalEffect)def.ExhaustEffect).ConditionMet(new ShardsContext {
                        Engine = g.Engine, ControllerIndex = p.Index,
                        Source = new ShardsCard { DefId = ids[i], Owner = p.Index } });
                    Check(obs[2064 + i] == (expected ? 1 : 0), "Readiness equals registered own effect: " + ids[i]);
                }
            }
        }
        private static void EntityOverflow()
        {
            var g = Fixture(925732); var p = g.Engine.State.Players[g.Actor]; p.Champions.Clear();
            for (int i = 0; i < 65; i++) Add(g, p, "primus_pilus_duel", ShardsZone.Champions, p.Champions);
            var before = Encode(g); p.Champions[50].DamageThisTurn = 1; var after = Encode(g);
            Check(before[2048 + 256 + (50 - 24) * 6 + 3] == 0 && after[2048 + 256 + (50 - 24) * 6 + 3] > 0,
                "Entity50 damage remains individually represented");
            Check(after[2048 + 497] > 0, "Beyond64 overflow is explicitly reported");
        }
    }
}
