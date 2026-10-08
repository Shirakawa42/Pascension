using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Pascension.Core;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Independent test oracles may inspect hidden lists. Nothing from these
    // oracles repairs Knowledge, legal actions, or the policy observation.
    internal static class StressSelfTest
    {
        private sealed class Case
        {
            public ulong Seed { get; set; }
            public int Dlc { get; set; }
            public string Hero0 { get; set; }
            public string Hero1 { get; set; }
            public int Profile { get; set; }
            public bool AutomaticSingletons { get; set; }
        }
        private sealed class Capture
        {
            public Case Game { get; set; }
            public List<int> Actions { get; set; }
            public string Error { get; set; }
            public string HostSha256 { get; set; }
            public int Boundary { get; set; }
            public string Context { get; set; }
            public string[] RecentPublicEventTypes { get; set; }
        }
        private sealed class Sampler
        {
            private ulong _state;
            internal Sampler(ulong seed) { _state=seed==0?1:seed; }
            internal int Next(int max)
            {
                _state^=_state<<13;_state^=_state>>7;_state^=_state<<17;
                return (int)(_state%(uint)max);
            }
        }
        private sealed class Totals
        {
            internal int Games,Completed,Censored,States,FreshChecks,PrivacyChecks,MaxRound,MaxActions;
            internal long Submissions,Automatic;
            internal readonly HashSet<int> Dlcs=new();
            internal readonly HashSet<string> Heroes=new(),Contexts=new(),Played=new();
            internal readonly int[] Actions=new int[16];
            internal object Report(double seconds)=>new {passed=true,games=Games,naturally_completed=Completed,censored=Censored,
                decision_boundaries=States,fresh_buffer_checks=FreshChecks,hidden_permutation_checks=PrivacyChecks,
                max_round=MaxRound,max_policy_choices=MaxActions,submissions=Submissions,automatic_singletons=Automatic,
                dlc_masks=Dlcs.OrderBy(x=>x).ToArray(),heroes=Heroes.OrderBy(x=>x).ToArray(),
                contexts=Contexts.OrderBy(x=>x).ToArray(),played_definitions=Played.OrderBy(x=>x).ToArray(),
                action_kinds=Actions,seconds};
        }
        internal static object Run(string[] args)
        {
            int games=args.Length>1?int.Parse(args[1]):96;
            ulong seed=args.Length>2?ulong.Parse(args[2]):120000;
            string directory=args.Length>3?args[3]:"Tools/ZeroDepthTraining/results";
            int maxActions=args.Length>4?int.Parse(args[4]):8000;
            if(games<1||games>10000||maxActions<1||maxActions>100000)
                throw new ArgumentOutOfRangeException("Bounded stress expects 1..10000 games and 1..100000 policy actions per game");
            var total=new Totals();var watch=Stopwatch.StartNew();
            for(int game=0;game<games;game++)
            {
                int mask=game%9;var heroes=ShardsContentRegistry.CharactersFor((ShardsDlc)mask);
                var spec=new Case {Seed=seed+(ulong)game,Dlc=mask,Hero0=heroes[(game/9)%heroes.Count],
                    Hero1=heroes[(game/9+1+game%3)%heroes.Count],Profile=(game/9)%4,AutomaticSingletons=game%2==0};
                Execute(spec,maxActions,directory,total,null);
                if((game+1)%32==0)Console.Error.WriteLine($"Stress {game+1}/{games}: {total.States} boundaries, {total.Completed} completed, {total.Censored} censored");
            }
            return total.Report(watch.Elapsed.TotalSeconds);
        }
        internal static object Replay(string path)
        {
            var capture=JsonSerializer.Deserialize<Capture>(File.ReadAllText(path));
            var total=new Totals();var watch=Stopwatch.StartNew();
            Execute(capture.Game,capture.Actions.Count,Path.GetDirectoryName(Path.GetFullPath(path)),total,capture.Actions);
            return new {passed=true,replayed_actions=capture.Actions.Count,checks=total.Report(watch.Elapsed.TotalSeconds)};
        }
        private static Adapter Create(Case spec)=>new(new ShardsEngine(ShardsContentRegistry.StandardConfig(spec.Seed,
            new List<PlayerSpec>{new(){Name="P0",CharacterId=spec.Hero0},new(){Name="P1",CharacterId=spec.Hero1}},
            (ShardsDlc)spec.Dlc)),automaticSingletons:spec.AutomaticSingletons);
        private static void Execute(Case spec,int maxActions,string directory,Totals total,List<int> replay)
        {
            Adapter g=null;var trace=new List<int>();int boundary=0;
            var random=new Sampler(spec.Seed^0x91e10da5c79e7b1dUL^(ulong)spec.Profile);
            var obs=GC.AllocateArray<float>(Encoder.ObsDim,pinned:true);
            var candidates=new float[Encoder.MaxActions*Encoder.ActionDim];var mask=new float[Encoder.MaxActions];
            try
            {
                g=Create(spec);
                for(;boundary<=maxActions;boundary++)
                {
                    Encoder.Encode(g,obs,candidates,mask);
                    Audit(g,obs);total.States++;
                    total.MaxRound=Math.Max(total.MaxRound,g.Engine.State.Round);
                    total.Dlcs.Add((int)g.Engine.State.Dlc);
                    foreach(var p in g.Engine.State.Players)if(p.CharacterId!=null)total.Heroes.Add(p.CharacterId);
                    total.Contexts.Add(g.Decision?.Context??"priority");
                    foreach(var p in g.Engine.State.Players)foreach(var c in p.PlayedThisTurn)total.Played.Add(c.DefId);
                    if(boundary%17==0){FreshParity(g,obs,candidates,mask);total.FreshChecks++;}
                    if(boundary%67==0){Privacy(g,obs,candidates,mask);total.PrivacyChecks++;}
                    if(g.Engine.State.GameOver||g.Truncated||boundary==maxActions)break;
                    int choice=replay==null?Choose(g,random,spec.Profile):replay[boundary];
                    trace.Add(choice);total.Actions[g.Visible(choice).Kind]++;g.Step(choice);
                }
                total.Games++;if(g.Engine.State.GameOver)total.Completed++;else total.Censored++;
                total.MaxActions=Math.Max(total.MaxActions,trace.Count);total.Submissions+=g.Submissions;total.Automatic+=g.AutomaticallyApplied;
            }
            catch(Exception error)
            {
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,$"visibility-stress-failure-s{spec.Seed}-p{spec.Profile}-a{trace.Count}.json");
                var capture=new Capture {Game=spec,Actions=trace,Error=error.ToString(),Boundary=boundary,
                    Context=g?.Decision?.Context??"priority",RecentPublicEventTypes=g==null?Array.Empty<string>():Enumerable.Range(Math.Max(0,g.Engine.Log.Count-24),Math.Min(24,g.Engine.Log.Count)).Select(i=>g.Engine.Log[i].GetType().Name).ToArray(),
                    HostSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(StressSelfTest).Assembly.Location))).ToLowerInvariant()};
                File.WriteAllText(path,JsonSerializer.Serialize(capture,new JsonSerializerOptions{WriteIndented=true}));
                throw new InvalidOperationException($"Stress seed={spec.Seed} dlc={spec.Dlc} profile={spec.Profile} boundary={boundary}; exact trace: {Path.GetFullPath(path)}; {error.Message}",error);
            }
        }
        private static int Choose(Adapter g,Sampler random,int profile)
        {
            Span<int> weights=stackalloc int[Encoder.MaxActions];int sum=0;
            for(int i=0;i<g.VisibleCount;i++)
            {
                var c=g.Visible(i);
                int weight=c.Kind switch {0=>70,1=>25,2=>profile==1?60:20,3=>profile==2?90:40,
                    4=>75,5=>35,6 or 7=>100,8=>profile==3?18:3,9=>65,10=>profile==3?10:1,
                    11=>0,12=>30,13=>g.Selected.Count==0?10:35,14=>2,15=>30,_=>0};
                if(c.Kind is 1 or 2)
                {
                    var buy=(ShardsBuyCardAction)c.Action;var def=g.Engine.State.CenterRow[buy.SlotIndex]?.Def;
                    if(def?.Type==ShardsCardType.Champion)weight*=2;
                    if(def!=null&&(def.Id.Contains("copy")||def.Id.Contains("ojas")||def.Id.Contains("decurion")||def.Id.Contains("legion")||def.Id.Contains("fabricator")||def.Id.Contains("longshot")||def.Id.Contains("oracles")))weight*=4;
                }
                weights[i]=weight;sum+=weight;
            }
            Check(sum>0,"No non-concede legal policy action");int draw=random.Next(sum);
            for(int i=0;i<g.VisibleCount;i++){draw-=weights[i];if(draw<0)return i;}
            throw new InvalidOperationException("Sampler sum mismatch");
        }
        private static IEnumerable<ShardsCard> Physical(Adapter g)
        {
            var s=g.Engine.State;
            foreach(var c in s.CenterDeck.Concat(s.CenterRow.Where(c=>c!=null)).Concat(s.DestinyDeck).Concat(s.DestinyRow).Concat(s.ActiveMonsters).Concat(s.Banished))yield return c;
            foreach(var p in s.Players)
                foreach(var c in p.Deck.Concat(p.Hand).Concat(p.Discard).Concat(p.PlayZone).Concat(p.Champions).Concat(p.Destinies).Concat(p.SetAside))yield return c;
        }
        private static IEnumerable<ShardsCard> Collection(ShardsPlayer p)=>p.Deck.Concat(p.Hand).Concat(p.Discard).Concat(p.PlayZone.Where(c=>!c.FastPlayed)).Concat(p.Champions);
        private static int[] Counts(IEnumerable<ShardsCard> cards)
        {
            var result=new int[Encoder.CardCapacity];foreach(var c in cards)result[Encoder.CardIndex(c.DefId)]++;return result;
        }
        private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        private static void Audit(Adapter g,float[] obs)
        {
            var s=g.Engine.State;var actual=Physical(g).ToArray();var identities=new HashSet<int>();
            foreach(var card in actual)Check(identities.Add(card.InstanceId),$"Physical zone duplicate {card.InstanceId} {card.DefId} {card.Zone}");
            foreach(var card in g.Knowledge.Detached)
                Check(identities.Add(card.InstanceId),$"Stale detached identity {card.InstanceId} {card.DefId} origin={card.Zone}; physical zone={actual.FirstOrDefault(c=>c.InstanceId==card.InstanceId)?.Zone}");
            var whole=Counts(actual.Concat(g.Knowledge.Detached));var initial=g.InitialCounts();
            int floods=s.Players.Count(p=>p.DoomGateFloodUsed);
            for(int code=0;code<Encoder.CardIds.Length;code++)
            {
                string id=Encoder.CardIds[code];var def=ShardsCardDatabase.Get(id);
                int expected=initial[code]+(def.IsMonster?floods*7:0);
                Check(whole[code]==expected,$"Physical pool {id}: {whole[code]} != public setup+flood {expected}");
            }
            var center=Counts(s.CenterDeck);var destiny=Counts(s.DestinyDeck);
            for(int code=0;code<Encoder.CardIds.Length;code++)
            {
                var def=ShardsCardDatabase.Get(Encoder.CardIds[code]);
                if(def.Type==ShardsCardType.Destiny)Count(obs,22,code,destiny[code]);
                else if(def.Type!=ShardsCardType.Starter&&def.Type!=ShardsCardType.Relic)Count(obs,21,code,center[code]);
            }
            for(int seat=0;seat<2;seat++)
            {
                var p=s.Players[seat];var collection=Counts(Collection(p).Concat(g.Knowledge.Detached.Where(c=>c.Owner==seat)));
                for(int code=0;code<Encoder.CardIds.Length;code++)Count(obs,seat==g.Actor?23:7,code,collection[code]);
                if(seat!=g.Actor)
                {var setAside=Counts(p.SetAside);for(int code=0;code<Encoder.CardIds.Length;code++)Count(obs,12,code,setAside[code]);}
                foreach(var fact in g.Knowledge.PublicHandIds[seat])
                    Check(p.Hand.Any(c=>c.InstanceId==fact.Key&&c.DefId==fact.Value),$"Identified hand fact absent: seat={seat} {fact.Key} {fact.Value}");
                foreach(var facts in g.Knowledge.Hand[seat].GroupBy(id=>id))
                    Check(facts.Count()<=p.Hand.Count(c=>c.DefId==facts.Key),$"Hand lower bound false: seat={seat} {facts.Key}");
                Positions(g.Knowledge.Personal[seat],p.Deck,$"personal seat={seat}");
                Positions(g.Knowledge.Center[seat],s.CenterDeck,$"center viewer={seat}");
            }
            Check((int)Math.Round(obs[166]*384)==g.EntityBuffer.Count,"Entity count header stale");
            KnownTensor(g,obs);
        }
        private static void KnownTensor(Adapter g,float[] obs)
        {
            int expected=g.Knowledge.Center[g.Actor].Count+g.Knowledge.Personal[0].Count+g.Knowledge.Personal[1].Count+
                g.Knowledge.PublicHandIds[1-g.Actor].Count;
            int rows=(int)Math.Round(obs[167]*384);
            Check(rows==expected,$"Remembered fact rows missing: {rows} != {expected}");
            var seen=new HashSet<(int kind,int identity)>();
            for(int i=0;i<rows;i++)
            {
                int at=Encoder.KnowledgeOffset+i*8;
                int kind=(int)Math.Round(obs[at+1]*3),identity=(int)Math.Round(obs[at+7]*65536)-1;
                Check(seen.Add((kind,identity)),$"Repeated remembered tensor identity kind={kind} id={identity}");
                if(kind==4)
                {
                    Check(g.Knowledge.PublicHandIds[1-g.Actor].TryGetValue(identity,out string id)&&obs[at]==Encoder.CardCode(id),"Encoded known-hand identity/definition mismatch");
                    Check(obs[at+2]==-1/384f&&obs[at+3]==-1/384f&&obs[at+4]==0&&obs[at+5]==0&&obs[at+6]==1,"Known hand falsely exposes private position");
                }
                else
                {
                    var facts=kind==1?g.Knowledge.Center[g.Actor]:kind==2?g.Knowledge.Personal[g.Actor]:kind==3?g.Knowledge.Personal[1-g.Actor]:null;
                    var fact=facts?.Find(f=>f.InstanceId==identity);
                    Check(fact!=null&&obs[at]==Encoder.CardCode(fact.DefId),"Encoded remembered pile identity/definition mismatch");
                    Check(obs[at+2]==fact.Min/384f&&obs[at+3]==fact.Max/384f&&obs[at+4]==(fact.Min==fact.Max&&!fact.UncertainPresence?1:0)&&
                        obs[at+5]==(fact.UncertainPresence?1:0)&&obs[at+6]==1,"Encoded remembered certainty/position mismatch");
                }
            }
        }
        private static void Count(float[] obs,int channel,int code,int expected)
        {
            float actual=obs[Encoder.HistogramOffset+channel*Encoder.CardCapacity+code]*10;
            Check(Math.Abs(actual-expected)<0.0001f,$"Tensor {Encoder.Histograms[channel]} {Encoder.CardIds[code]}: {actual} != {expected}");
        }
        private static void Positions(List<KnownPosition> facts,List<ShardsCard> pile,string label)
        {
            var seen=new HashSet<int>();
            foreach(var fact in facts)
            {
                Check(seen.Add(fact.InstanceId),$"Duplicate known identity {label} {fact.InstanceId}");
                int index=pile.FindIndex(c=>c.InstanceId==fact.InstanceId);
                if(index<0){Check(fact.UncertainPresence,$"False certain membership {label} {fact.InstanceId} {fact.DefId} [{fact.Min},{fact.Max}]");continue;}
                int top=pile.Count-1-index;
                Check(pile[index].DefId==fact.DefId&&top>=fact.Min&&top<=fact.Max,
                    $"False known position {label} {fact.InstanceId} {fact.DefId}: actual={top}, remembered=[{fact.Min},{fact.Max}]");
            }
        }
        private static void Same(Adapter g,float[] obs,float[] candidates,float[] mask,string label)
        {
            var fresh=new float[Encoder.ObsDim];var actions=new float[Encoder.MaxActions*Encoder.ActionDim];var legal=new float[Encoder.MaxActions];
            IntPtr reused=g.ObservationAddress;
            Encoder.Encode(g,fresh,actions,legal);g.ObservationAddress=reused;
            int index=-1;
            for(int i=0;i<obs.Length;i++)if(obs[i]!=fresh[i]){index=i;break;}
            Check(index<0,$"{label}: observation column {index} changed ({(index>=0?obs[index]:0)} -> {(index>=0?fresh[index]:0)})");
            Check(candidates.SequenceEqual(actions)&&mask.SequenceEqual(legal),label+": legal candidates or mask changed");
        }
        private static void FreshParity(Adapter g,float[] obs,float[] candidates,float[] mask)=>Same(g,obs,candidates,mask,"Reused/fresh buffer parity");
        private static void Privacy(Adapter g,float[] obs,float[] candidates,float[] mask)
        {
            var s=g.Engine.State;var backups=new List<(List<ShardsCard> pile,ShardsCard[] cards)>();
            void Permute(List<ShardsCard> pile,IEnumerable<KnownPosition> facts)
            {
                var known=new HashSet<int>(facts.Select(f=>f.InstanceId));var slots=Enumerable.Range(0,pile.Count).Where(i=>!known.Contains(pile[i].InstanceId)).ToArray();
                backups.Add((pile,pile.ToArray()));
                for(int i=0;i<slots.Length/2;i++){int a=slots[i],b=slots[slots.Length-1-i];(pile[a],pile[b])=(pile[b],pile[a]);}
            }
            var enemy=s.Players[1-g.Actor];ShardsCard h=null,d=null;int hi=-1,di=-1;
            try
            {
                Permute(s.CenterDeck,g.Knowledge.Center.SelectMany(x=>x));Permute(s.DestinyDeck,Array.Empty<KnownPosition>());
                foreach(var p in s.Players)Permute(p.Deck,g.Knowledge.Personal[p.Index]);
                backups.Add((enemy.Hand,enemy.Hand.ToArray()));enemy.Hand.Reverse();
                var knownDeck=new HashSet<int>(g.Knowledge.Personal[enemy.Index].Select(f=>f.InstanceId));
                hi=enemy.Hand.FindIndex(c=>!g.Knowledge.PublicHandIds[enemy.Index].ContainsKey(c.InstanceId)&&
                    enemy.Hand.Count(x=>x.DefId==c.DefId)>g.Knowledge.Hand[enemy.Index].Count(id=>id==c.DefId)&&
                    enemy.Deck.Any(x=>!knownDeck.Contains(x.InstanceId)&&x.DefId!=c.DefId));
                if(hi>=0)
                {
                    di=enemy.Deck.FindIndex(c=>!knownDeck.Contains(c.InstanceId)&&c.DefId!=enemy.Hand[hi].DefId);
                    h=enemy.Hand[hi];d=enemy.Deck[di];enemy.Hand[hi]=d;d.Zone=ShardsZone.Hand;enemy.Deck[di]=h;h.Zone=ShardsZone.Deck;
                }
                Same(g,obs,candidates,mask,"Unrevealed order/hand allocation privacy");
            }
            finally
            {
                if(h!=null){h.Zone=ShardsZone.Hand;d.Zone=ShardsZone.Deck;}
                foreach(var backup in backups){backup.pile.Clear();backup.pile.AddRange(backup.cards);}
                s.InvalidateCardIndex();
            }
        }
    }
}
