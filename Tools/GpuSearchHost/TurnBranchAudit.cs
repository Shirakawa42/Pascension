using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pascension.Core;
using Pascension.Engine.Actions;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Offline research instrument, never used to choose a live action.
// Reconstructs real positions from complete action-key transcripts, then measures
// bounded trees on independently sampled public-information worlds. No hash merges.
internal static class TurnBranchAudit
{
    sealed class Counts
    {
        public string mode;
        public int nodes, leaves, maxDepth, maxBranch, duplicateChoicesRemoved;
        public bool capped;
        public double milliseconds;
        public Dictionary<string,int> boundaries=new();
    }
    static Counts Explore(Adapter source,string mode,int cap)
    {
        var root=TacticalSearch.PublicWorld(source,713101,FastCopy.Copy);
        bool whole=mode.StartsWith("whole-turn",StringComparison.Ordinal);
        int seat=root.Actor,round=root.Engine.State.Round;
        var original=root.Engine.State.Players[seat].Hand.Select(c=>c.InstanceId).ToHashSet();
        int deck=root.Engine.State.Players[seat].Deck.Count;
        int[] center=root.Engine.State.CenterRow.Select(c=>c?.InstanceId??-1).ToArray();
        var result=new Counts{mode=mode};var clock=Stopwatch.StartNew();
        void Leaf(string reason){result.leaves++;result.boundaries.TryGetValue(reason,out int n);result.boundaries[reason]=n+1;}
        void Visit(Adapter g,int depth)
        {
            result.nodes++;result.maxDepth=Math.Max(result.maxDepth,depth);
            var s=g.Engine.State;var p=s.Players[seat];
            if(s.GameOver){Leaf("terminal");return;}
            if(g.Actor!=seat||s.TurnPlayerIndex!=seat||s.Round!=round){Leaf("turn-or-opponent-input");return;}
            if(depth>=32){Leaf("depth-cap");return;}
            if(!whole)
            {
                if(g.Decision!=null){Leaf("effect-choice");return;}
                if(p.Hand.Any(c=>!original.Contains(c.InstanceId))||p.Deck.Count!=deck){Leaf("draw-or-new-hand-card");return;}
                if(!s.CenterRow.Select(c=>c?.InstanceId??-1).SequenceEqual(center)){Leaf("market-change");return;}
            }
            var actions=new List<int>();var seen=new HashSet<string>();
            for(int a=0;a<g.VisibleCount;a++)
            {
                var c=g.Visible(a);
                if(c.Action is ConcedeAction||c.Kind==14)continue;
                if(!whole)
                {
                    bool playInitial=c.Action is ShardsPlayCardAction play&&original.Contains(play.CardInstanceId);
                    bool tactical=mode.StartsWith("hand-plus-tactics",StringComparison.Ordinal)&&(c.Action is ShardsFocusAction||c.Action is ShardsHeroAbilityAction||c.Action is ShardsExhaustAction);
                    if(!playInitial&&!tactical)continue;
                }
                if(mode.EndsWith("collapsed",StringComparison.Ordinal)&&c.Action is ShardsPlayCardAction)
                {
                    var card=s.FindCard(((ShardsPlayCardAction)c.Action).CardInstanceId);
                    // Optimistic symmetry experiment ONLY. This is not a production
                    // equivalence proof for paused effects or instance-targeting.
                    string key=$"{card.DefId}:{card.Owner}:{card.Exhausted}:{card.FastPlayed}:{card.DamageThisTurn}:{card.BanishAtCleanup}";
                    if(!seen.Add(key)){result.duplicateChoicesRemoved++;continue;}
                }
                actions.Add(a);
            }
            result.maxBranch=Math.Max(result.maxBranch,actions.Count);
            if(actions.Count==0){Leaf("no-more-initial-hand-plays");return;}
            foreach(int a in actions)
            {
                if(result.nodes>=cap){result.capped=true;return;}
                var child=FastCopy.Copy(g);child.Step(a);Visit(child,depth+1);
            }
        }
        Visit(root,0);result.milliseconds=clock.Elapsed.TotalMilliseconds;return result;
    }
    internal static void Run(string specsPath,string reviews,string output,int cap,int maxPositions,string scope)
    {
        if(cap<2)throw new ArgumentException("Node cap must be at least two");
        string[] scopes={"hand-raw","hand-collapsed","hand-plus-tactics","hand-plus-tactics-collapsed","whole-turn-sampled","whole-turn-collapsed"};
        if(scope!=null&&!scopes.Contains(scope))throw new ArgumentException("Unknown audit scope");
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        var rows=new List<object>();int transitions=0,games=0;var watch=Stopwatch.StartNew();
        var special=new HashSet<string>{"1313:218","1313:334","1338:151","1338:179","1283:436","1402:201","1402:241","1418:177"};
        foreach(var spec in JArray.Parse(File.ReadAllText(specsPath)))
        {
            int index=(int)spec["index"];string h0=(string)spec["hero0"],h1=(string)spec["hero1"];
            var cfg=ShardsContentRegistry.StandardConfig((ulong)spec["seed"],new List<PlayerSpec>{new(){Name="P0",CharacterId=h0},new(){Name="P1",CharacterId=h1}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(cfg));g.SubmitThroughHost=act=>{var r=g.ApplyExternal(act);if(!r.Accepted)throw new Exception(r.Error);};
            foreach(string hero in new[]{h1,h0})g.Step(Enumerable.Range(0,g.VisibleCount).Single(k=>g.Visible(k).Option?.DefId==hero));
            var starts=new HashSet<string>();int steps=0;
            foreach(string line in File.ReadLines(Path.Combine(reviews,$"game-{index}.jsonl")))
            {
                var r=JObject.Parse(line);if(r["legal"]==null)continue;
                if((int)r["step"]!=steps||g.Actor!=(int)r["seat"]||g.Engine.State.Round!=(int)r["round"])throw new Exception("Replay position mismatch");
                // Names and rollout proposals are ambiguous after a terminal override.
                // Require the actual final action key rather than guessing a replay.
                string key=(string)r["selectedKey"]??throw new ArgumentException("Regenerate review transcripts with exact selectedKey fields before auditing");
                int chosen=Enumerable.Range(0,g.VisibleCount).Single(k=>TacticalSearch.Key(g,k)==key);
                bool start=g.Decision==null&&g.Actor==g.Engine.State.TurnPlayerIndex&&starts.Add($"{g.Engine.State.Round}:{g.Actor}");
                bool probe=special.Contains($"{index}:{steps}");
                if((start||probe)&&rows.Count<maxPositions)
                {
                    ulong before=TacticalSearch.Fingerprint(g);
                    var tests=(scope==null?scopes:new[]{scope}).Select(mode=>Explore(g,mode,cap)).ToArray();
                    if(before!=TacticalSearch.Fingerprint(g))throw new Exception("Audit mutated replay source");
                    rows.Add(new{game=index,step=steps,start,probe,round=g.Engine.State.Round,seat=g.Actor,hero=g.Engine.State.Players[g.Actor].CharacterId,
                        hand=g.Engine.State.Players[g.Actor].Hand.Select(c=>c.DefId).ToArray(),legal=g.VisibleCount,tests});
                }
                g.Step(chosen);steps++;transitions++;
            }
            if(!g.Engine.State.GameOver||g.Engine.State.WinnerIndex!=(int)spec["winner"]||g.Engine.State.Round!=(int)spec["rounds"]||steps!=(int)spec["steps"])throw new Exception($"Replay outcome mismatch game {index}: ended={g.Engine.State.GameOver} winner={g.Engine.State.WinnerIndex} round={g.Engine.State.Round} steps={steps}; expected winner={spec["winner"]} round={spec["rounds"]} steps={spec["steps"]}");
            games++;Console.Error.WriteLine($"Turn audit: {games} games, {rows.Count} positions, {watch.Elapsed.TotalSeconds:F1}s");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{schema="turn-branch-research-v1",cap,maxPositions,scope,depthCap=32,games,transitions,seconds=watch.Elapsed.TotalSeconds,
            caveat="Whole-turn counts use one sampled world; collapsed identical cards are an optimistic symmetry estimate, not a proven production reduction. Search times exclude world creation. No acting policy changed.",rows},Formatting.Indented));
    }
}
