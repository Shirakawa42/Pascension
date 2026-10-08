using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Offline evidence only: replay exact recorded action keys, then apply proposed
// public-information alternatives on copies. Never changes the acting AI.
internal static class PositionReview
{
    static object State(Adapter g,int seat)
    {
        var s=g.Engine.State;var p=s.Players[seat];
        return new {seat,hp=p.Health,mastery=p.Mastery,gems=p.Gems,power=p.Power,
            enemyHp=s.Players[1-seat].Health,
            context=g.Decision?.Context,actor=g.Actor,round=s.Round,s.GameOver,s.WinnerIndex,
            hand=p.Hand.Select(c=>c.DefId).ToArray(),champions=p.Champions.Select(c=>c.DefId).ToArray(),
            discard=p.Discard.Select(c=>c.DefId).ToArray(),deckCount=p.Deck.Count,
            deckComposition=p.Deck.GroupBy(c=>c.DefId).OrderBy(xs=>xs.Key).ToDictionary(xs=>xs.Key,xs=>xs.Count()),
            market=s.CenterRow.Select(c=>c?.DefId).ToArray(),monsters=s.ActiveMonsters.Select(c=>c.DefId).ToArray(),
            knownCenterTop=g.Knowledge.For(seat).ToArray(),knownDeckTop=g.Supplement.Top(seat).ToArray(),
            knownOwnHand=g.Supplement.Hand(seat).ToArray(),knownEnemyHand=g.Supplement.Hand(1-seat).ToArray(),
            setAside=p.SetAside.Select(c=>new{c.InstanceId,c.DefId}).ToArray(),
            legal=Enumerable.Range(0,g.VisibleCount).Select(k=>ReviewRecorder.Name(g,g.Visible(k))).ToArray()};
    }
    internal static void Run(string specsPath,string reviews,string positionsPath,string output,Func<Adapter,JObject,object> diagnose=null)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var positions=JArray.Parse(File.ReadAllText(positionsPath));var evidence=new List<object>();int transitions=0,games=0,cardLookups=0;
        foreach(var spec in JArray.Parse(File.ReadAllText(specsPath)))
        {
            int index=(int)spec["index"];string transcript=Path.Combine(reviews,$"game-{index}.jsonl");
            if(!File.Exists(transcript))continue;
            string h0=(string)spec["hero0"],h1=(string)spec["hero1"];
            var cfg=ShardsContentRegistry.StandardConfig((ulong)spec["seed"],new List<PlayerSpec>{new(){Name="P0",CharacterId=h0},new(){Name="P1",CharacterId=h1}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(cfg));g.SubmitThroughHost=act=>{var result=g.ApplyExternal(act);if(!result.Accepted)throw new Exception(result.Error);};
            foreach(string hero in new[]{h1,h0})g.Step(Enumerable.Range(0,g.VisibleCount).Single(k=>g.Visible(k).Option?.DefId==hero));
            int steps=0;
            foreach(string line in File.ReadLines(transcript))
            {
                var r=JObject.Parse(line);if(r["legal"]==null)continue;
                // Offline truth check only. Private identities never enter a
                // policy feature or repair the public-memory constraints.
                for(int player=0;player<2;player++)
                    foreach(var fact in g.Supplement.Hand(player).GroupBy(id=>id))
                        if(fact.Count()>g.Engine.State.Players[player].Hand.Count(c=>c.DefId==fact.Key))
                            throw new Exception($"False public hand memory: game {index}, step {steps}, player {player}, card {fact.Key}");
                if((int)r["step"]!=steps||g.Actor!=(int)r["seat"]||g.Engine.State.Round!=(int)r["round"])throw new Exception("Replay position mismatch");
                for(int candidate=0;candidate<g.VisibleCount;candidate++)
                {
                    int id=g.Visible(candidate).Action switch
                    {
                        ShardsPlayCardAction a=>a.CardInstanceId, ShardsExhaustAction a=>a.CardInstanceId,
                        ShardsAttackMonsterAction a=>a.CardInstanceId, ShardsTakeDestinyAction a=>a.CardInstanceId,
                        ShardsRecruitRelicAction a=>a.CardInstanceId, _=>-1
                    };
                    if(id<0)continue;
                    if(g.Engine.State.FindCard(id)==null)throw new Exception($"Public action target missing from live card index: game {index}, step {steps}, card {id}");
                    cardLookups++;
                }
                int action=Enumerable.Range(0,g.VisibleCount).Single(k=>TacticalSearch.Key(g,k)==(string)r["selectedKey"]);
                foreach(var position in positions.Where(p=>(int)p["game"]==index&&(int)p["step"]==steps))
                {
                    ulong hash=TacticalSearch.Fingerprint(g);int seat=g.Actor;
                    var trials=new List<object>();
                    for(int world=0;world<16;world++)
                    {
                        var alternative=TacticalSearch.PublicWorld(g,713101+world*7919,FastCopy.Copy);
                        var path=new List<string>();string blocked=null;
                        bool exactKeys=position["keys"]!=null;
                        foreach(string name in (position["keys"]??position["path"]).Values<string>())
                        {
                            int chosen=Enumerable.Range(0,alternative.VisibleCount).FirstOrDefault(k=>
                                (exactKeys?TacticalSearch.Key(alternative,k):ReviewRecorder.Name(alternative,alternative.Visible(k)))==name,-1);
                            if(chosen<0){blocked=name;break;}
                            string description=ReviewRecorder.Name(alternative,alternative.Visible(chosen));
                            alternative.Step(chosen);path.Add(description);
                        }
                        trials.Add(new{world,path,blocked,after=State(alternative,seat)});
                    }
                    if(hash!=TacticalSearch.Fingerprint(g))throw new Exception("Counterfactual changed replay source");
                    var analysis=position["analyze"]?.Value<bool>()==true?diagnose?.Invoke(g,r):null;
                    if(hash!=TacticalSearch.Fingerprint(g))throw new Exception("Analysis changed replay source");
                    object repair=null;
                    if(position["repairPath"] is JArray repairPath)
                    {
                        int improved=SequenceRepairs.Improve(g,repairPath.Values<string>().ToArray(),FastCopy.Copy);
                        repair=new{action=improved,name=improved<0?null:ReviewRecorder.Name(g,g.Visible(improved))};
                        if(hash!=TacticalSearch.Fingerprint(g))throw new Exception("Sequence repair changed source");
                    }
                    evidence.Add(new{game=index,step=steps,selected=(string)r["selected"],before=State(g,seat),trials,analysis,repair});
                }
                g.Step(action);steps++;transitions++;
            }
            if(!g.Engine.State.GameOver||steps!=(int)spec["steps"]||g.Engine.State.WinnerIndex!=(int)spec["winner"]||g.Engine.State.Round!=(int)spec["rounds"])throw new Exception("Replay outcome mismatch "+index);
            if(g.Supplement.HandInvalidations!=0)throw new Exception($"Unreported hand movement: game {index}, count {g.Supplement.HandInvalidations}");
            games++;
        }
        if(evidence.Count!=positions.Count)throw new Exception("Missing requested position");
        File.WriteAllText(output,JsonConvert.SerializeObject(new{schema="public-position-review-v1",games,transitions,cardLookups,evidence},Formatting.Indented));
        Console.Error.WriteLine($"Verified {games} full transcripts, {transitions} actions, {evidence.Count} positions x 16 public worlds.");
    }
}
