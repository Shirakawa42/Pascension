using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.AI
{
// Primitive choices remain available; a resource
// macro is chosen by search, never forced just because cards remain in hand.
internal sealed partial class HybridLookahead
{
    readonly PolicySearchSettings config;
    readonly Func<Adapter[],Prediction[]> infer;
    readonly Func<Adapter,Adapter> copy;
    readonly ParallelOptions parallel;
    readonly ConditionalWeakTable<Adapter,Saved> plans=new();
    sealed class Saved {internal string[] Keys;internal int Next,Seat,Round;internal long Steps,Submissions;internal bool MenuPlan,ScryPlan;internal int MenuId;internal long[] StepSubmissions;}
    internal sealed class Option {internal int First;internal string[] Keys;internal double Probability,ChoiceProbability,ChoiceUncertainty=1;internal bool MenuPlan,SetupPlan,OptionalMenu,ScryPlan;internal int MenuId;internal long[] StepSubmissions;internal long FirstSubmissions=1;}
    sealed class Branch
    {
        internal Adapter Game;internal int Root,Option,Style,Seat,Turn,Round,Used,Depth;
        internal int TurnChanges,TurnStartDepth,FirstTurnPathLength,HealthSpent;
        internal double Value;internal bool Done,Complete,TurnChanged;
        internal string MenuHistory="",MenuInformation;
        internal string[] SetupKeys;
        internal int SetupNext;
        internal List<string> Path=new();
        internal void Advance(int action,bool continuation=false)
        {
            int log=Game.Engine.Log.Count;Path.Add(TacticalSearch.Key(Game,action));Game.Step(action);Used++;
            if(continuation)Depth++;
            for(int i=log;i<Game.Engine.Log.Count;i++)
            {
                if(Game.Engine.Log[i] is ShardsHealthChangedEvent health&&health.PlayerIndex==Seat&&health.Delta<0)
                    HealthSpent-=health.Delta;
                if(Game.Engine.Log[i] is ShardsTurnStartedEvent)
                {
                    if(!TurnChanged)FirstTurnPathLength=Path.Count;
                    TurnChanged=true;TurnChanges++;TurnStartDepth=Depth;SetupKeys=null;
                }
            }
        }
    }
    internal long Branches,Steps,Overrides,Decisions,OpeningDecisions;
    internal double CloneSeconds,RolloutSeconds,TerminalSeconds;
    long cachedSteps,forcedSteps,macroOptions,unfinishedLeaves,allLeaves;
    internal bool CaptureLeaves {get;set;}
    internal object DebugLeaves,DebugVerification;
    internal long VerifiedRoots,VerificationChanges,SequenceRepairsApplied;
    internal object Diagnostics=>new{cachedSteps,forcedSteps,macroOptions,unfinishedLeaves,allLeaves,menuBranches,menuInformationSets,VerifiedRoots,VerificationChanges,SequenceRepairsApplied};
    // Keep a small target-policy floor even in confidently losing positions;
    // saturated critic estimates still contain approximation error.
    internal static double ChoiceUncertainty(double value)=>Math.Max(.05,1-value*value);
    internal HybridLookahead(PolicySearchSettings settings,Func<Adapter[],Prediction[]> inference,Func<Adapter,Adapter> copier)
    {config=settings;infer=inference;copy=copier;parallel=new ParallelOptions{MaxDegreeOfParallelism=settings.Workers};}
    internal void TransferPlan(Adapter from,Adapter to)
    {
        if(from==null||to==null||!plans.TryGetValue(from,out var saved))return;
        plans.Remove(from);plans.Remove(to);
        plans.Add(to,new Saved{Keys=saved.Keys,Next=saved.Next,Seat=saved.Seat,Round=saved.Round,Steps=saved.Steps,Submissions=saved.Submissions,MenuPlan=saved.MenuPlan,ScryPlan=saved.ScryPlan,MenuId=saved.MenuId,StepSubmissions=saved.StepSubmissions});
    }

    // Deliberately narrow symmetry domain: neutral starter cards, no costs,
    // draws, health, mastery, faction triggers, champion deployment or custom code.
    // Thresholds only read mastery, which cannot change inside a resource macro.
    internal static bool ResourceCard(Adapter g,int action)
    {
        if(g.Decision!=null||g.Candidates.Count>Encoder.MaxActions||!(g.Visible(action).Action is ShardsPlayCardAction play))return false;
        var card=g.Engine.State.FindCard(play.CardInstanceId);var def=card.Def;
        if(def.Type!=ShardsCardType.Starter||def.Faction!=ShardsFaction.None||def.CountsAsEveryFaction||def.IsChampion)return false;
        bool Flat(IShardsEffect effect)
        {
            if(effect is Gain gain)return gain.Draw==0&&gain.Health==0&&gain.Mastery==0&&gain.Gems>=0&&gain.Power>=0;
            if(effect is ShardsComposite sequence)
            {for(int i=0;i<sequence.Parts.Count;i++)if(!Flat(sequence.Parts[i]))return false;return true;}
            if(effect is AtMastery threshold)return Flat(threshold.Inner);
            return effect is ShardsNullEffect;
        }
        return Flat(def.PlayEffect);
    }
    internal static List<int[]> Groups(Adapter g)
    {
        var groups=new List<List<int>>();var lookup=new Dictionary<string,int>();
        for(int a=0;a<g.VisibleCount;a++)
        {
            if(g.Visible(a).Action is ConcedeAction)continue;
            if(ResourceCard(g,a))
            {
                var c=g.Engine.State.FindCard(((ShardsPlayCardAction)g.Visible(a).Action).CardInstanceId);
                string key=$"{c.DefId}:{c.Owner}:{c.Zone}:{c.Exhausted}:{c.FastPlayed}:{c.DamageThisTurn}:{c.BanishAtCleanup}";
                if(lookup.TryGetValue(key,out int group)){groups[group].Add(a);continue;}
                lookup.Add(key,groups.Count);
            }
            groups.Add(new List<int>{a});
        }
        return groups.Select(x=>x.ToArray()).ToList();
    }
    int Greedy(Adapter g,Prediction prediction,int style=0)
    {
        // Offline-tested alternative continuation: resolve the root effect and
        // stop. A gain must not be rejected solely because every longer policy
        // continuation subsequently spends it poorly. This is a simulated plan,
        // never a mandatory end-turn rule for the real actor.
        if(style==3&&g.Decision==null&&g.Actor==g.Engine.State.TurnPlayerIndex)
            for(int a=0;a<g.VisibleCount;a++)if(g.Visible(a).Action is ShardsEndTurnAction)return a;
        if(style==3&&config.MenuPlans&&MenuPlanning.VisibleMenu(g))
            for(int a=0;a<g.VisibleCount;a++)if(g.Visible(a).Kind==13)return a;
        if(style==3)style=0;
        int preferred=SafeTurnGains.Preferred(g,style);
        if(preferred>=0&&(!config.PruneNoEffectPlans||!NoEffectPlans.InactiveDestiny(g,preferred)))return preferred;
        // Rollouts call this hundreds of thousands of times: no grouping lists,
        // strings, dictionaries, sorting, or LINQ allocations on the hot path.
        Span<bool> used=stackalloc bool[64];used.Clear();int best=-1;double maximum=-1;
        bool omitEnd=config.TacticalGuards&&SafeTurnGains.HasAlternative(g);
        for(int a=0;a<g.VisibleCount;a++)
        {
            if(used[a]||g.Visible(a).Action is ConcedeAction||(omitEnd&&g.Visible(a).Action is ShardsEndTurnAction)||
                (config.TacticalGuards&&SafeTurnGains.WastefulFocus(g,a))||
                (config.PruneNoEffectPlans&&NoEffectPlans.InactiveDestiny(g,a)))continue;
            double mass=prediction.P[a];
            if(ResourceCard(g,a))
            {
                var first=g.Engine.State.FindCard(((ShardsPlayCardAction)g.Visible(a).Action).CardInstanceId);
                for(int b=a+1;b<g.VisibleCount;b++)
                {
                    if(!(g.Visible(b).Action is ShardsPlayCardAction play))continue;
                    var next=g.Engine.State.FindCard(play.CardInstanceId);
                    if(first.DefId==next.DefId&&first.Owner==next.Owner&&first.Zone==next.Zone&&first.Exhausted==next.Exhausted&&first.FastPlayed==next.FastPlayed&&first.DamageThisTurn==next.DamageThisTurn&&first.BanishAtCleanup==next.BanishAtCleanup)
                    {mass+=prediction.P[b];used[b]=true;}
                }
            }
            if(mass>maximum){best=a;maximum=mass;}
        }
        if(best<0)throw new InvalidOperationException("No non-concession continuation");
        return best;
    }
    internal static List<Option> Options(Adapter g,Prediction prediction,int fallback,int limit,bool guards=false,bool mixed=false,bool optionalChoices=false,bool pruneNoEffect=false)
    {
        var groups=Groups(g).OrderByDescending(group=>group.Sum(a=>(double)prediction.P[a])).ToList();
        if(pruneNoEffect)groups.RemoveAll(xs=>NoEffectPlans.InactiveDestiny(g,xs[0]));
        if(guards)groups.RemoveAll(xs=>SafeTurnGains.WastefulFocus(g,xs[0]));
        if(guards&&SafeTurnGains.HasAlternative(g))groups.RemoveAll(xs=>g.Visible(xs[0]).Action is ShardsEndTurnAction);
        var selected=groups.Take(limit).ToList();
        var fallbackGroup=groups.FirstOrDefault(group=>group.Contains(fallback));
        if(fallbackGroup!=null&&!selected.Contains(fallbackGroup))selected.Add(fallbackGroup);
        // An optional effect always has a meaningful no-effect alternative.
        // A confident policy or small branch budget must not erase that choice.
        if(optionalChoices&&g.Decision!=null)
            foreach(var group in groups.Where(xs=>g.Visible(xs[0]).Kind==13))
                if(!selected.Contains(group))selected.Add(group);
        var result=new List<Option>();
        foreach(var group in selected)
        {
            // Keep the sampled instance first, so a one-card option preserves it.
            var ordered=group.OrderBy(a=>a==fallback?0:1).ToArray();
            double mass=group.Sum(a=>(double)prediction.P[a]);
            for(int n=1;n<=ordered.Length;n++)result.Add(new Option{First=ordered[0],Keys=ordered.Take(n).Select(a=>TacticalSearch.Key(g,a)).ToArray(),Probability=mass/ordered.Length});
        }
        if(mixed&&g.Decision==null)
        {
            var gemCards=Enumerable.Range(0,g.VisibleCount).Where(a=>ResourceCard(g,a)).Select(a=>
            {
                var card=g.Engine.State.FindCard(((ShardsPlayCardAction)g.Visible(a).Action).CardInstanceId);
                var gain=new SafeTurnGains.Amounts();SafeTurnGains.Read(card.Def.PlayEffect,g.Engine.State.Players[g.Actor].Mastery,ref gain);
                return new{action=a,card.DefId,gain.Gems};
            }).Where(x=>x.Gems>0).OrderByDescending(x=>x.Gems).ThenBy(x=>x.action).ToArray();
            if(gemCards.Select(x=>x.DefId).Distinct().Count()>1)
                result.Add(new Option{First=gemCards[0].action,Keys=gemCards.Select(x=>TacticalSearch.Key(g,x.action)).ToArray(),
                    Probability=gemCards.Sum(x=>(double)prediction.P[x.action])/gemCards.Length});
        }
        return result;
    }
    internal List<Option> BuildOptions(Adapter g,Prediction prediction,int fallback)
    {
        var result=Options(g,prediction,fallback,config.Candidates,config.TacticalGuards,config.MixedResourcePlans,config.OptionalChoices,config.PruneNoEffectPlans);
        if(config.SetupPlans)SetupPlanning.Expand(g,result,prediction,copy);
        if(config.MenuPlans)MenuPlanning.Expand(g,result,copy,infer,config.PruneNoEffectPlans);
        if(config.ScryPlans){var batch=new[]{result};ScryPlanning.Expand(new[]{g},batch,copy,infer);result=batch[0];}
        return result;
    }
    static int Find(Adapter g,string key)
    {for(int a=0;a<g.VisibleCount;a++)if(TacticalSearch.Key(g,a)==key)return a;return -1;}
    static int SoleAction(Adapter g)
    {
        if(g.VisibleCount>2)return -1;
        int sole=-1;
        for(int a=0;a<g.VisibleCount;a++)if(!(g.Visible(a).Action is ConcedeAction))
        {if(sole>=0)return -1;sole=a;}
        return sole;
    }
    static bool PlanMenu(Adapter g,bool scry,int id)=>scry?g.Decision?.Context=="soi.scry"&&g.Decision.Id==id&&g.Actor==g.Engine.State.TurnPlayerIndex:MenuPlanning.VisibleMenu(g);
    bool Cached(Adapter g,out int action)
    {
        action=-1;if(!plans.TryGetValue(g,out var saved))return false;
        if(g.Actor!=saved.Seat||g.Engine.State.Round!=saved.Round||g.WrapperSteps!=saved.Steps||g.Submissions!=saved.Submissions||saved.Next>=saved.Keys.Length)
        {plans.Remove(g);return false;}
        action=Find(g,saved.Keys[saved.Next]);
        if(action<0||(saved.MenuPlan?!PlanMenu(g,saved.ScryPlan,saved.MenuId):!ResourceCard(g,action))){plans.Remove(g);action=-1;return false;}
        saved.Submissions+=saved.StepSubmissions==null?1:saved.StepSubmissions[saved.Next];saved.Next++;saved.Steps++;cachedSteps++;return true;
    }
    internal int[] Choose(Adapter[] roots,Prediction[] predictions,int[] fallback,bool[] enabled)
    {
        DebugVerification=null;
        var verificationRoots=new List<int>();
        var result=(int[])fallback.Clone();var options=new List<Option>[roots.Length];
        var jobs=new List<(int root,int option,int world,int style)>();
        for(int i=0;i<roots.Length;i++)
        {
            if(!enabled[i])continue;
            if(config.SimplifyWins)
            {
                int finish=TacticalSearch.ImmediateWinningEnd(roots[i],copy);
                if(finish>=0){result[i]=finish;plans.Remove(roots[i]);continue;}
            }
            if(Cached(roots[i],out int cached)){result[i]=cached;continue;}
            // A forced answer (or EndTurn versus resignation) has no strategic
            // alternative to evaluate. Preserve one real submission per UI step.
            int sole=SoleAction(roots[i]);
            if(sole>=0){result[i]=sole;forcedSteps++;continue;}
            Decisions++;if(roots[i].Engine.State.Round<=2)OpeningDecisions++;
            options[i]=Options(roots[i],predictions[i],fallback[i],config.Candidates,config.TacticalGuards,config.MixedResourcePlans,config.OptionalChoices,config.PruneNoEffectPlans);
        }
        if(config.SetupPlans)Parallel.For(0,roots.Length,parallel,i=>
        {if(options[i]!=null)SetupPlanning.Expand(roots[i],options[i],predictions[i],copy);});
        if(config.MenuPlans)MenuPlanning.Expand(roots,options,copy,infer,config.Workers,config.PruneNoEffectPlans);
        if(config.ScryPlans)ScryPlanning.Expand(roots,options,copy,infer);
        for(int i=0;i<roots.Length;i++)
        {
            if(options[i]==null)continue;
            macroOptions+=options[i].Count(o=>o.Keys.Length>1);
            foreach(int o in Enumerable.Range(0,options[i].Count))for(int style=0;style<config.RolloutStyles;style++)for(int w=0;w<config.Worlds;w++)jobs.Add((i,o,w,style));
        }
        if(jobs.Count==0)
        {Overrides+=Enumerable.Range(0,result.Length).Count(i=>enabled[i]&&result[i]!=fallback[i]);return result;}
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var templates=new Adapter[roots.Length*config.Worlds];
        Parallel.For(0,templates.Length,parallel,j=>
        {int root=j/config.Worlds;if(options[root]!=null)templates[j]=TacticalSearch.PublicWorld(roots[root],713101+j%config.Worlds*7919,copy);});
        var branches=new Branch[jobs.Count];
        Parallel.For(0,jobs.Count,parallel,j=>
        {
            var job=jobs[j];var g=copy(templates[job.root*config.Worlds+job.world]);
            var b=new Branch{Game=g,Root=job.root,Option=job.option,Style=job.style,Seat=g.Actor,Turn=g.Engine.State.TurnPlayerIndex,Round=g.Engine.State.Round};
            foreach(string key in options[job.root][job.option].Keys)
            {
                int a=Find(g,key);
                if(a<0){if(b.Used==0)throw new InvalidOperationException("Root action became illegal");break;}
                // A large newly affordable menu can cross the paging boundary.
                // End the resource segment there, exactly as the live cache does.
                if(b.Used>0&&(options[job.root][job.option].SetupPlan?g.Actor!=b.Seat||g.Decision!=null:
                    options[job.root][job.option].MenuPlan?g.Actor!=b.Seat||!PlanMenu(g,options[job.root][job.option].ScryPlan,options[job.root][job.option].MenuId):!ResourceCard(g,a)))break;
                b.Advance(a);
            }
            var option=options[job.root][job.option];
            // A visible reveal/choice can interrupt a known preparation prefix.
            // Retain its unexecuted keys for this hypothetical rollout; live
            // SetupPlans still replan after each actual action and new information.
            if(option.SetupPlan&&b.Used<option.Keys.Length)
            {b.SetupKeys=option.Keys;b.SetupNext=b.Used;}
            branches[j]=b;
        });
        Branches+=branches.Length;Steps+=branches.Sum(b=>b.Used);CloneSeconds+=watch.Elapsed.TotalSeconds;watch.Restart();
        int continuationLimit=config.HybridFinishTurn?Math.Max(config.Depth,64):config.Depth;
        FinishBranches(branches,continuationLimit,config.MenuDepth);
        allLeaves+=branches.Length;unfinishedLeaves+=branches.Count(b=>!b.Complete);RolloutSeconds+=watch.Elapsed.TotalSeconds;
        if(CaptureLeaves)DebugLeaves=branches.Select(b=>new{b.Root,b.Option,b.Style,b.Used,b.Depth,b.TurnChanges,b.Value,b.Complete,path=b.Path.ToArray(),hash=TacticalSearch.Fingerprint(b.Game)}).ToArray();
        var proven=new bool[roots.Length];
        foreach(var group in branches.GroupBy(b=>b.Root))
        {
            int i=group.Key;
            var winningLines=new Dictionary<string,bool>();
            bool ConfirmWin(IEnumerable<Branch> xs)
            {
                // A fixed opponent continuation is an estimate, never a proof
                // that the live opponent must cooperate with a winning line.
                if(config.HorizonTurns>1&&xs.Any(b=>b.TurnChanged))return false;
                if(!xs.All(b=>b.Game.Engine.State.GameOver&&b.Game.Engine.State.WinnerIndex==b.Seat))return false;
                var path=xs.First().Path;string key=string.Join("\n",path);
                if(!winningLines.TryGetValue(key,out bool win))winningLines[key]=win=TacticalSearch.IsWinningLine(roots[i],path,copy);
                return win;
            }
            // A win in two sampled hidden hands cannot receive unconditional
            // winning priority until it survives the defensive validation too.
            var choices=group.GroupBy(b=>new{b.Option,b.Style}).Select(xs=>new{xs.Key.Option,xs.Key.Style,Value=xs.Average(b=>b.Value),Win=ConfirmWin(xs),HealthSpent=xs.Max(b=>b.HealthSpent),Length=xs.Max(b=>b.Path.Count)}).ToArray();
            bool scryMenu=config.ScryPlans&&ScryPlanning.Applies(roots[i]);
            bool boundedMenu=(scryMenu||config.OptionalChoices&&MenuPlanning.VisibleMenu(roots[i]))&&
                Enumerable.Range(0,roots[i].VisibleCount).Any(a=>roots[i].Visible(a).Kind==13);
            double PriorScore(double value,double probability,bool applyCap=true)
            {
                double logPrior=Math.Log(Math.Max(1e-12,probability));
                if(boundedMenu)
                {
                    // Float-underflowed policy probabilities must not impose an
                    // effectively absolute veto. Cap at six nats and use one
                    // root uncertainty scale shared by all alternatives. Keep
                    // ordinary regularization for private reveal/Scry menus.
                    double rootValue=predictions[i].V;
                    return config.Prior*(scryMenu?1:config.ChoicePriorScale)*ChoiceUncertainty(rootValue)*Math.Max(-6,logPrior);
                }
                // Experimental confidence bound: tiny policy likelihoods must
                // not categorically veto a better simulated ordinary action.
                if(applyCap&&roots[i].Decision==null&&config.ActionPriorCap>0)logPrior=Math.Max(-config.ActionPriorCap,logPrior);
                return config.Prior*(roots[i].Decision==null?Math.Max(.001,1-value*value):1)*logPrior;
            }
            // Near a saturated win estimate, a fixed log-prior can outweigh every
            // productive continuation. Unplanned effect menus retain ordinary
            // confidence regularization; complete Scry paths use bounded joint
            // likelihood, and normal turn actions taper it near saturation.
            double ChoicePrior(Option option)
            {
                if(!config.OptionalChoices||!option.MenuPlan||option.ScryPlan)return 0;
                double log=Math.Log(Math.Max(1e-12,option.ChoiceProbability));
                // An activation+target plan must retain the target policy's
                // regularization, just as independently solving that menu does.
                // Otherwise a tiny critic difference can commit a zero-probability
                // target and bypass the sensible choice at the live menu.
                return config.Prior*(option.OptionalMenu?config.ChoicePriorScale*option.ChoiceUncertainty*Math.Max(-6,log):log);
            }
            // Only compare costs after the exact winning line has survived
            // validation. Nonwinning estimates keep their original ranking.
            var best=choices.OrderByDescending(x=>x.Win)
                .ThenBy(x=>config.SimplifyWins&&x.Win?x.HealthSpent:0)
                .ThenBy(x=>config.SimplifyWins&&x.Win?x.Length:0)
                .ThenByDescending(x=>x.Value+PriorScore(x.Value,options[i][x.Option].Probability)+ChoicePrior(options[i][x.Option]))
                .ThenByDescending(x=>config.MenuPlans?options[i][x.Option].Probability:0)
                .ThenByDescending(x=>options[i][x.Option].ChoiceProbability).First();
            var selected=options[i][best.Option];result[i]=selected.First;
            if(!best.Win&&roots[i].Decision==null&&config.ActionPriorCap>0&&config.Prior>0&&config.PriorVerificationWorlds>config.Worlds)
            {
                var trusted=choices.OrderByDescending(x=>x.Win).ThenByDescending(x=>x.Value+PriorScore(x.Value,options[i][x.Option].Probability,false)+ChoicePrior(options[i][x.Option]))
                    .ThenByDescending(x=>config.MenuPlans?options[i][x.Option].Probability:0)
                    .ThenByDescending(x=>options[i][x.Option].ChoiceProbability).First();
                if(options[i][trusted.Option].First!=selected.First)verificationRoots.Add(i);
            }
            int reordered=-1;
            if(config.SetupPlans&&!best.Win)
            {
                var chosenBranches=group.Where(b=>b.Option==best.Option&&b.Style==best.Style).ToArray();
                // A repair must agree across every sampled world, rather than
                // taking a prerequisite from a favorable hidden continuation.
                var repairs=chosenBranches.Select(b=>SetupPlanning.ImproveResourceOrder(roots[i],
                    config.HorizonTurns>1&&b.TurnChanged?b.Path.Take(b.FirstTurnPathLength).ToArray():b.Path,copy)).Distinct().ToArray();
                if(repairs.Length==1&&repairs[0]>=0){reordered=repairs[0];result[i]=reordered;plans.Remove(roots[i]);}
            }
            if(best.Win)proven[i]=TacticalSearch.AcceptWinningLine(roots[i],group.First(b=>b.Option==best.Option&&b.Style==best.Style).Path,copy);
            if(config.SimplifyWins&&proven[i])
            {
                int simpler=TacticalSearch.SimplifyPaidWinningPrefix(roots[i],result[i],copy);
                if(simpler!=result[i]){result[i]=simpler;plans.Remove(roots[i]);continue;}
            }
            if(config.SequenceRepairs&&reordered<0)
            {
                var chosen=group.Where(b=>b.Option==best.Option&&b.Style==best.Style).ToArray();
                int improved=-1;List<string> line=null;bool agrees=true;
                foreach(var branch in chosen)
                {
                    int candidate=SequenceRepairs.Improve(roots[i],branch.Path,copy,out var rewritten);
                    if(candidate<0||improved>=0&&candidate!=improved){agrees=false;break;}
                    improved=candidate;line??=rewritten;
                }
                // An accepted win retains priority only after the rewritten
                // complete line passes the same independent win validation.
                if(agrees&&improved>=0&&(!proven[i]||TacticalSearch.AcceptWinningLine(roots[i],line,copy)))
                {result[i]=improved;SequenceRepairsApplied++;plans.Remove(roots[i]);continue;}
            }
            // Setup prefixes are evaluated jointly, but the live actor replans
            // after every step, especially after drawing/revealing new information.
            if(selected.Keys.Length>1&&!selected.SetupPlan&&reordered<0)
            {
                plans.Remove(roots[i]);plans.Add(roots[i],new Saved{Keys=selected.Keys,Next=1,Seat=roots[i].Actor,Round=roots[i].Engine.State.Round,Steps=roots[i].WrapperSteps+1,Submissions=roots[i].Submissions+selected.FirstSubmissions,MenuPlan=selected.MenuPlan,ScryPlan=selected.ScryPlan,MenuId=selected.MenuId,StepSubmissions=selected.StepSubmissions});
            }
        }
        if(verificationRoots.Count>0)
        {
            // Only the confidence-cap disagreement buys a larger world sample.
            // Re-solve all candidates with common public worlds; do not average
            // separately optimized hidden actions or recurse into verification.
            var settings=config.ValidatedCopy();settings.Worlds=config.PriorVerificationWorlds;
            settings.PriorVerificationWorlds=0;settings.TerminalNodes=0;
            var verifier=new HybridLookahead(settings,infer,copy){CaptureLeaves=CaptureLeaves};
            var indices=verificationRoots.ToArray();var games=indices.Select(i=>roots[i]).ToArray();
            var refined=verifier.Choose(games,indices.Select(i=>predictions[i]).ToArray(),indices.Select(i=>fallback[i]).ToArray(),indices.Select(i=>true).ToArray());
            if(CaptureLeaves)DebugVerification=new{roots=indices,worlds=settings.Worlds,selected=refined,leaves=verifier.DebugLeaves};
            for(int n=0;n<indices.Length;n++)
            {
                int i=indices[n];VerifiedRoots++;if(result[i]!=refined[n])VerificationChanges++;
                result[i]=refined[n];plans.Remove(roots[i]);
                // A verified resource/menu prefix must replace, not leave behind,
                // the speculative two-world prefix on this same live adapter.
                if(verifier.plans.TryGetValue(roots[i],out var saved))plans.Add(roots[i],saved);
            }
            Branches+=verifier.Branches;Steps+=verifier.Steps;CloneSeconds+=verifier.CloneSeconds;RolloutSeconds+=verifier.RolloutSeconds;
            macroOptions+=verifier.macroOptions;unfinishedLeaves+=verifier.unfinishedLeaves;allLeaves+=verifier.allLeaves;
            menuBranches+=verifier.menuBranches;menuInformationSets+=verifier.menuInformationSets;
            SequenceRepairsApplied+=verifier.SequenceRepairsApplied;
        }
        if(config.TerminalNodes>0)
        {
            watch.Restart();
            Parallel.For(0,roots.Length,parallel,i=>
            {
                if(options[i]==null||proven[i]||!TacticalSearch.IsRelevant(roots[i]))return;
                int win=TacticalSearch.Find(roots[i],config.TerminalNodes,12,4,copy,true);
                if(win>=0&&config.PruneNoEffectPlans)win=TacticalSearch.SimplifyWinningPrefix(roots[i],win,copy);
                if(win>=0&&config.SimplifyWins)win=TacticalSearch.SimplifyPaidWinningPrefix(roots[i],win,copy);
                // Sampled lethal lines are not proof against every hidden shield
                // allocation. They must respect the same free-gain exclusion as
                // the ordinary candidates, rather than undoing that safeguard.
                bool rejected=win>=0&&config.TacticalGuards&&(SafeTurnGains.WastefulFocus(roots[i],win)||
                    roots[i].Visible(win).Action is ShardsEndTurnAction&&SafeTurnGains.HasAlternative(roots[i]));
                if(win>=0&&!rejected){result[i]=win;plans.Remove(roots[i]);}
            });
            TerminalSeconds+=watch.Elapsed.TotalSeconds;
        }
        Overrides+=Enumerable.Range(0,result.Length).Count(i=>enabled[i]&&result[i]!=fallback[i]);return result;
    }
}
}
