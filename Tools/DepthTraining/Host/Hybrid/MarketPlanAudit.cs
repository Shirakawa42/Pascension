using System;
using System.Linq;
using System.Reflection;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class MarketPlanAudit
    {
        internal static object Run()
        {
            ulong seed=731960;while(HeroAssignments.ForSeed(seed).Seat0!="rez")seed++;
            var root=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(root,seed);
            var p=root.Engine.State.Players[0];p.Mastery=5;p.Gems=20;
            typeof(ShardsEngine).GetMethod("RoutePriority",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(root.Engine,null);root.Rebuild();
            root.Step(Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Action is ShardsHeroAbilityAction));
            root.Step(Enumerable.Range(0,root.VisibleCount).Single(a=>root.Visible(a).Kind==13));
            int fallback=Enumerable.Range(0,root.VisibleCount).First(a=>root.Visible(a).Action is ShardsPlayCardAction);
            var prediction=new Prediction{P=new float[64],V=0};prediction.P[fallback]=1;
            var settings=new Shards.AI.PolicySearchSettings{Candidates=1,Worlds=1,Depth=2,Workers=1};
            var search=new HybridLookahead(settings,g=>g.Select(_=>prediction).ToArray(),DepthCopy.Copy);
            ulong before=TacticalSearch.Fingerprint(root);
            var options=search.BuildOptions(root,prediction,fallback);
            int eligible=0,covered=0;
            foreach(int a in Enumerable.Range(0,root.VisibleCount).Where(a=>root.Visible(a).Action is ShardsRerollRowAction))
            {
                var probe=DepthCopy.Copy(root);var reroll=(ShardsRerollRowAction)root.Visible(a).Action;probe.Step(a);
                foreach(int b in Enumerable.Range(0,probe.VisibleCount).Where(b=>probe.Visible(b).Action is ShardsBuyCardAction buy&&buy.SlotIndex==reroll.SlotIndex))
                {
                    eligible++;string first=TacticalSearch.Key(root,a),second=TacticalSearch.Key(probe,b);
                    if(options.Any(o=>o.SetupPlan&&o.Keys.SequenceEqual(new[]{first,second})))covered++;
                }
            }
            if(eligible==0)throw new Exception("Fixture has no affordable revealed-card acquisition");
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Market planning mutated live root");
            if(covered!=eligible)throw new Exception($"Search omitted {eligible-covered}/{eligible} known-top free-reroll/acquisition plans under collapsed prior");
            var unknown=DepthCopy.Copy(root);unknown.Knowledge.Center[unknown.Actor].Clear();
            var absent=new System.Collections.Generic.List<HybridLookahead.Option>();
            MarketPlanning.Expand(unknown,absent,prediction,DepthCopy.Copy);
            if(absent.Count!=0)throw new Exception("Unknown center top generated a known-card plan");
            var paid=DepthCopy.Copy(root);paid.Engine.State.Players[paid.Actor].RerollsThisTurn=1;paid.Rebuild();
            MarketPlanning.Expand(paid,absent,prediction,DepthCopy.Copy);
            if(absent.Count!=0)throw new Exception("Paid reroll entered the free-reroll proposal path");
            var hidden=DepthCopy.Copy(root);
            hidden.Engine.State.Players[1].Hand.Reverse();hidden.Engine.State.Players[0].Deck.Reverse();
            hidden.Engine.State.CenterDeck.Reverse(0,hidden.Engine.State.CenterDeck.Count-3);
            var hiddenOptions=search.BuildOptions(hidden,prediction,fallback);
            string[] Keys(System.Collections.Generic.List<HybridLookahead.Option> xs)=>xs.Where(o=>o.SetupPlan).Select(o=>string.Join("/",o.Keys)).ToArray();
            if(!Keys(options).SequenceEqual(Keys(hiddenOptions)))throw new Exception("Market proposals depended on unrevealed card order");
            int knownId=root.Knowledge.Center[0].Single(f=>f.Min==0&&f.Max==0&&!f.UncertainPresence).InstanceId;
            Prediction[] AcquireValue(Adapter[] games)=>games.Select(g=>
            {
                var own=g.Engine.State.Players[0];
                bool acquired=own.Hand.Concat(own.Deck).Concat(own.Discard).Concat(own.PlayZone).Any(c=>c.InstanceId==knownId);
                var probabilities=new float[64];
                for(int a=0;a<g.VisibleCount;a++)if(g.Visible(a).Action is ShardsEndTurnAction)probabilities[a]=1;
                return new Prediction{P=probabilities,V=(g.Actor==0?1:-1)*(acquired?.75f:-.75f)};
            }).ToArray();
            settings.Depth=2;settings.EndTurnExtension=0;settings.RolloutStyles=1;settings.TerminalNodes=0;settings.Prior=0;
            var choosing=new HybridLookahead(settings,AcquireValue,DepthCopy.Copy);
            int choice=choosing.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(!(root.Visible(choice).Action is ShardsRerollRowAction))throw new Exception("Full search did not value the known-card acquisition prefix");
            if(before!=TacticalSearch.Fingerprint(root))throw new Exception("Full market search mutated live root");
            var monsterRoot=new Adapter(new ShardsEngine(Program.Config(seed)));HeroAssignments.Apply(monsterRoot,seed);
            var monsterPlayer=monsterRoot.Engine.State.Players[0];monsterPlayer.Mastery=5;monsterPlayer.Gems=20;
            var deck=monsterRoot.Engine.State.CenterDeck;var monster=deck.First(c=>c.Def.IsMonster);deck.Remove(monster);deck.Add(monster);
            typeof(ShardsEngine).GetMethod("RoutePriority",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(monsterRoot.Engine,null);monsterRoot.Rebuild();
            monsterRoot.Step(Enumerable.Range(0,monsterRoot.VisibleCount).Single(a=>monsterRoot.Visible(a).Action is ShardsHeroAbilityAction));
            monsterRoot.Step(Enumerable.Range(0,monsterRoot.VisibleCount).Single(a=>monsterRoot.Visible(a).Kind==13));
            MarketPlanning.Expand(monsterRoot,absent,prediction,DepthCopy.Copy);
            if(absent.Count!=0)throw new Exception("Known monster treated as an acquirable market card");
            var monsterProbe=DepthCopy.Copy(monsterRoot);
            monsterProbe.Step(Enumerable.Range(0,monsterProbe.VisibleCount).First(a=>monsterProbe.Visible(a).Action is ShardsRerollRowAction));
            if(!monsterProbe.Engine.State.ActiveMonsters.Any(c=>c.InstanceId==monster.InstanceId))throw new Exception("Fixture failed to reveal a monster while refilling the row");
            return new{passed=true,eligible,covered,fullSearchSelectedAcquisition=true,unknownTopExcluded=true,paidRerollExcluded=true,knownMonsterExcluded=true,hiddenArrangementInvariant=true,rootUnchanged=true};
        }
    }
}
