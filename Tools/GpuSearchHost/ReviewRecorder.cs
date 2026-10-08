using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;

// Passive replay audit. Counterfactual search uses private copies and cannot
// change the acting AI's saved plans, RNG, state, or public-information memory.
internal sealed class ReviewRecorder
{
    internal object SearchDetails,ExtendedSearch;
    internal bool Probe(int step)=>(gameIndex==1313&&(step==218||step==334))||(gameIndex==1338&&(step==151||step==179))||
        (gameIndex==1283&&step==436)||(gameIndex==1418&&step==177)||(gameIndex==1402&&(step==201||step==241));
    internal static string Name(Adapter g,Candidate c)
    {
        if(c.Action is ShardsExhaustAction exhaust)return "exhaust:"+(g.Engine.State.FindCard(exhaust.CardInstanceId)?.DefId??exhaust.CardInstanceId.ToString());
        if(c.Action is ShardsHeroAbilityAction)return "hero:"+g.Engine.State.Players[g.Actor].CharacterId;
        if(c.Action is ShardsBuyCardAction buy)return (buy.FastPlay?"fastplay:":"buy:")+g.Engine.State.CenterRow[buy.SlotIndex]?.DefId;
        return RezAudit.Name(g,c);
    }
    readonly StreamWriter output; readonly int gameIndex;
    internal ReviewRecorder(string directory,int index)
    {Directory.CreateDirectory(directory);gameIndex=index;output=new StreamWriter(Path.Combine(directory,$"game-{index}.jsonl"));}
    static object Card(ShardsCard c)=>c==null?null:new{c.InstanceId,id=c.DefId,c.Exhausted,c.DamageThisTurn,c.FastPlayed,c.BanishAtCleanup};
    internal void Observe(Adapter g,int step,int action,int fallback,Prediction prediction)
    {
        int seat=g.Actor;var s=g.Engine.State;var p=s.Players[seat];var e=s.Players[1-seat];
        var legal=Enumerable.Range(0,g.VisibleCount).Select(k=>new{index=k,name=Name(g,g.Visible(k)),type=g.Visible(k).Action?.GetType().Name,probability=prediction.P[k],key=TacticalSearch.Key(g,k),inactive=NoEffectPlans.InactiveDestiny(g,k)}).ToArray();
        var findings=new List<object>();
        if(legal[action].inactive)
            findings.Add(new{kind="certified_inactive_destiny_activation",action=Name(g,g.Visible(action))});
        if(g.Visible(action).Action is ShardsEndTurnAction)
        {
            bool chosenWins=true;
            for(int w=0;w<4;w++)
            {
                var after=TacticalSearch.PublicWorld(g,713101+w*7919,FastCopy.Copy);
                after.Step(action);
                for(int n=0;n<64&&!after.Engine.State.GameOver&&after.Actor!=seat&&after.Decision?.Context=="soi.shields";n++)
                {
                    int shield=-1,finish=-1;
                    for(int k=0;k<after.VisibleCount;k++){if(after.Visible(k).Kind==12)shield=k;if(after.Visible(k).Kind==13)finish=k;}
                    if(shield>=0)after.Step(shield);else if(finish>=0)after.Step(finish);else break;
                }
                chosenWins &= after.Engine.State.GameOver&&after.Engine.State.WinnerIndex==seat;
            }
            if(!chosenWins)
            {
                var unspent=legal.Where(x=>!x.inactive).Where(x=>x.type==nameof(ShardsFocusAction)||x.type==nameof(ShardsHeroAbilityAction)||x.type==nameof(ShardsExhaustAction)||x.type==nameof(ShardsPlayCardAction)||x.type==nameof(ShardsRecruitRelicAction)||x.type==nameof(ShardsTakeDestinyAction)).Select(x=>x.name).ToArray();
                if(unspent.Length>0)findings.Add(new{kind="unspent_options_at_end_turn",options=unspent});
                var audit=TacticalSearch.Copy(g);int better=TacticalSearch.Find(audit,4096,24,4,FastCopy.Copy,true);
                if(better==action)findings.Clear(); // The selected end-turn sequence itself wins.
                if(better>=0&&better!=action)
                {
                    var path=new List<string>();var world=TacticalSearch.PublicWorld(audit,713101,FastCopy.Copy);
                    TacticalSearch.TransferPlan(audit,world);int next=better;
                    for(int n=0;n<32&&next>=0&&!world.Engine.State.GameOver&&world.Actor==seat;n++)
                    {
                        path.Add(Name(world,world.Visible(next)));world.Step(next);
                        if(!world.Engine.State.GameOver)next=TacticalSearch.Find(world,4096,24,4,FastCopy.Copy,true);
                    }
                    findings.Add(new{kind="alternative_sampled_winning_line",first=Name(g,g.Visible(better)),path,terminal=world.Engine.State.GameOver,winner=world.Engine.State.WinnerIndex});
                }
            }
        }
        string[] line=null;
        if(gameIndex==1313&&step==218)line=new[]{"play:shard_reactor","play:crystal","play:crystal"};
        if(gameIndex==1313&&step==334)line=new[]{"play:crystal","play:crystal","play:crystal","ShardsFocusAction","play:nil_assassin_duel","play:wraethe_skirmisher_duel"};
        if(gameIndex==1338&&step==151)line=new[]{"play:crystal","play:crystal","play:crystal","play:blaster","play:the_dispossessed","exhaust:thornshell_warden"};
        if(gameIndex==1338&&step==179)line=new[]{"play:crystal","play:crystal","play:crystal","play:order_initiate_duel","ShardsFocusAction"};
        if(gameIndex==1418&&step==177)line=new[]{"play:blaster","ShardsFocusAction"};
        if(gameIndex==1283&&step==436)line=new[]{"play:shardwood_guardian_duel","play:shardwood_guardian_duel"};
        if(gameIndex==1402&&(step==201||step==241))line=new[]{"play:j_chord_duel"};
        object counterfactual=null;
        if(line!=null)
        {
            var alternative=TacticalSearch.Copy(g);var applied=new List<string>();string blocked=null;
            foreach(string name in line)
            {
                int choice=Enumerable.Range(0,alternative.VisibleCount).FirstOrDefault(k=>Name(alternative,alternative.Visible(k))==name,-1);
                if(choice<0){blocked=name;break;}alternative.Step(choice);applied.Add(name);
            }
            var ap=alternative.Engine.State.Players[seat];
            counterfactual=new{applied,blocked,hp=ap.Health,mastery=ap.Mastery,gems=ap.Gems,power=ap.Power,hand=ap.Hand.Select(c=>c.DefId),champions=ap.Champions.Select(c=>c.DefId),
                legal=Enumerable.Range(0,alternative.VisibleCount).Select(k=>Name(alternative,alternative.Visible(k))).ToArray()};
        }
        output.WriteLine(JsonConvert.SerializeObject(new{game=gameIndex,step,selectedIndex=action,selectedKey=TacticalSearch.Key(g,action),round=s.Round,seat,turn=s.TurnPlayerIndex,context=g.Decision?.Context,
            hero=p.CharacterId,enemy=e.CharacterId,hp=p.Health,enemyHp=e.Health,mastery=p.Mastery,enemyMastery=e.Mastery,gems=p.Gems,power=p.Power,
            p.FocusedThisTurn,p.HeroAbilityUsedThisTurn,p.RerollsThisTurn,p.NextRerollDiscount,
            hand=p.Hand.Select(Card),played=p.PlayZone.Select(Card),champions=p.Champions.Select(Card),enemyChampions=e.Champions.Select(Card),
            destinies=p.Destinies.Select(Card),discard=p.Discard.Select(c=>c.DefId),ownDeckComposition=p.Deck.GroupBy(c=>c.DefId).ToDictionary(x=>x.Key,x=>x.Count()),
            knownDeckTop=g.Supplement.Top(seat),knownCenterTop=g.Knowledge.For(seat),market=s.CenterRow.Select(Card),monsters=s.ActiveMonsters.Select(Card),
            counterfactual,search=SearchDetails,extendedSearch=ExtendedSearch,selected=legal[action].name,selectedType=legal[action].type,fallback=legal[fallback].name,value=prediction.V,legal,findings}));
    }
    internal void Finish(Adapter g)
    {output.WriteLine(JsonConvert.SerializeObject(new{game=gameIndex,finished=true,winner=g.Engine.State.WinnerIndex,round=g.Engine.State.Round}));output.Dispose();}
}
