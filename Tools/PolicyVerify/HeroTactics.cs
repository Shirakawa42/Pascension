using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pascension.Core;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Evaluation-only, deliberately constructed positions. Each winning line is
// executed through the engine before testing the unmodified shipped network.
internal static class HeroTactics
{
    internal sealed class Case
    {
        internal string Hero, Id, Description;
        internal Func<int,Adapter> Factory;
        internal Action<Adapter> Demonstrate;
        internal Func<Adapter,bool> Goal;
        internal Func<Adapter,bool> Stop;
    }
    private static FrozenPolicy AuditPolicy;
    private static List<object> DemonstrationTrace;
    private static ShardsPlayer Own(Adapter g)=>g.Engine.State.Players[g.Actor];
    private static void Step(Adapter g,Func<Candidate,bool> predicate)
    {
        if(DemonstrationTrace!=null)
        {
            var en=Encode(g);var p=AuditPolicy.Probabilities(en.o,en.c,en.m,out float value);
            int best=Array.IndexOf(p,p.Max());
            var allowed=Enumerable.Range(0,g.VisibleCount).Where(i=>predicate(g.Visible(i))).ToArray();
            if(allowed.Length==0)throw new Exception("No demonstration target: "+g.Decision?.Context);
            DemonstrationTrace.Add(new{context=g.Decision?.Context,selected=Name(g,g.Visible(allowed[0])),selected_probability=allowed.Sum(i=>p[i]),best=Name(g,g.Visible(best)),best_probability=p[best],value,mastery=Own(g).Mastery,power=Own(g).Power,gems=Own(g).Gems});
        }
        RezAudit.Step(g,predicate);
    }
    private static bool IsPlay(Adapter g,Candidate c,string id)=>c.Action is ShardsPlayCardAction a&&Own(g).Hand.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==id);
    private static void Play(Adapter g,string id)=>Step(g,c=>IsPlay(g,c,id));
    private static void Tap(Adapter g,string id)=>Step(g,c=>c.Action is ShardsExhaustAction a&&Own(g).Champions.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==id));
    private static void Hero(Adapter g)=>Step(g,c=>c.Action is ShardsHeroAbilityAction);
    private static void Focus(Adapter g)=>Step(g,c=>c.Action is ShardsFocusAction);
    private static void Finish(Adapter g)=>Step(g,c=>c.Kind==13);
    private static void Pick(Adapter g,string id)=>Step(g,c=>c.Option?.DefId==id);
    private static void Buy(Adapter g,string id)=>Step(g,c=>c.Action is ShardsBuyCardAction a&&a.FastPlay&&g.Engine.State.CenterRow[a.SlotIndex]?.DefId==id);
    private static ShardsCard Give(Adapter g,string id,ShardsZone zone=ShardsZone.Hand,int seat=-1)
    {
        if(seat<0)seat=g.Actor;
        var p=g.Engine.State.Players[seat];
        var c=p.SetAside.Concat(p.Discard).FirstOrDefault(x=>x.DefId==id&&x.Def.Type==ShardsCardType.Relic);
        if(c!=null){p.SetAside.Remove(c);p.Discard.Remove(c);}
        else c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id};
        c.Owner=zone==ShardsZone.MonsterSpace?-1:seat;c.Zone=zone;
        if(zone==ShardsZone.Hand)p.Hand.Add(c);else if(zone==ShardsZone.Champions)p.Champions.Add(c);
        else if(zone==ShardsZone.Deck)p.Deck.Add(c);else if(zone==ShardsZone.Discard)p.Discard.Add(c);
        else if(zone==ShardsZone.MonsterSpace)g.Engine.State.ActiveMonsters.Add(c);
        else throw new Exception("Unsupported fixture zone "+zone);
        RezAudit.Refresh(g);return c;
    }
    private static Adapter Game(string hero,int variant,int mastery,int gems=0,int enemyHealth=50,string relic=null,string opponentHero=null)
    {
        int seat=variant%2;string other=opponentHero??(hero=="decima"?"tetra":"decima");
        var cfg=ShardsContentRegistry.StandardConfig((ulong)(0x79000000+variant),new List<PlayerSpec>{new(){Name="P0",CharacterId=seat==0?hero:other},new(){Name="P1",CharacterId=seat==1?hero:other}},ShardsDlc.Duel);
        var g=new Adapter(new ShardsEngine(cfg));g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
        foreach(string pick in seat==0?new[]{other,hero}:new[]{hero,other})Pick(g,pick);
        g.Engine.State.TurnPlayerIndex=seat;g.Engine.State.Round=5+variant%12;
        var p=g.Engine.State.Players[seat];p.Hand.Clear();p.Deck.Clear();p.Discard.Clear();p.Mastery=mastery;p.Gems=gems;p.FocusedThisTurn=true;p.Health=35+variant%16;
        g.Engine.State.DestinyRow.Clear();
        Array.Clear(g.Engine.State.CenterRow,0,g.Engine.State.CenterRow.Length);
        var e=g.Engine.State.Players[1-seat];e.Health=enemyHealth;
        // Public complete opponent collection rules out every hidden shield.
        foreach(var c in e.Hand.Concat(e.Deck).Concat(e.Discard))c.DefId="crystal";
        e.Hand[0].DefId="blaster";
        string[] market={"axia_duel","general_decurion","zetta_encryptor","orm_madu_duel","drakonarius","additri_gaiamancer"};
        for(int i=0;i<6;i++)Row(g,i,market[(i+variant)%6]);
        // Reroll refills cannot accidentally supply a different immediate win.
        g.Engine.State.CenterDeck.Clear();
        foreach(string id in new[]{"zetta_encryptor","testudo_vanguard","giga_source_adept","raidian"})
            g.Engine.State.CenterDeck.Add(new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=-1,Zone=ShardsZone.CenterDeck});
        RezAudit.Refresh(g);
        if(mastery>=10)
        {
            relic??=p.SetAside[0].DefId;
            Step(g,c=>c.Action is ShardsRecruitRelicAction a&&p.SetAside.Any(x=>x.InstanceId==a.CardInstanceId&&x.DefId==relic));
        }
        // Keep recruited relic in discard unless the scenario explicitly moves it.
        RezAudit.Refresh(g);return g;
    }
    private static void Row(Adapter g,int slot,string id)
    {
        g.Engine.State.CenterRow[slot]=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,DefId=id,Owner=-1,Zone=ShardsZone.CenterRow};RezAudit.Refresh(g);
    }
    private static void End(Adapter g)
    {
        int seat=g.Actor;Step(g,c=>c.Action is ShardsEndTurnAction);
        for(int i=0;i<40&&!g.Engine.State.GameOver&&g.Engine.State.TurnPlayerIndex==seat;i++)
        {
            if(g.Candidates.Any(c=>c.Kind==13))Finish(g);else g.Step(0);
        }
    }
    private static string Name(Adapter g,Candidate c)
    {
        if(c.Action is ShardsPlayCardAction a)return "play:"+Own(g).Hand.First(x=>x.InstanceId==a.CardInstanceId).DefId;
        if(c.Action is ShardsExhaustAction b)return "exhaust:"+Own(g).Champions.Concat(Own(g).Destinies).First(x=>x.InstanceId==b.CardInstanceId).DefId;
        if(c.Action is ShardsBuyCardAction buy)return (buy.FastPlay?"fast:":"buy:")+g.Engine.State.CenterRow[buy.SlotIndex].DefId;
        if(c.Action is ShardsRerollRowAction r)return "reroll:"+r.SlotIndex;
        return c.Option?.DefId??c.Option?.Label??c.Action?.GetType().Name??("kind:"+c.Kind+":"+c.Low+"-"+c.High);
    }
    private static (float[] o,float[] c,float[] m) Encode(Adapter g)
    {var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);return(o,c,m);}
    internal static List<Case> Cases()
    {
        var list=new List<Case>();
        void Win(string hero,string id,string description,Func<int,Adapter> f,Action<Adapter> line)
            =>list.Add(new Case{Hero=hero,Id=id,Description=description,Factory=f,Demonstrate=g=>{line(g);if(!g.Engine.State.GameOver)End(g);},Goal=g=>g.Engine.State.GameOver&&g.Engine.State.Players[g.Engine.State.WinnerIndex<0?0:g.Engine.State.WinnerIndex].CharacterId==hero&&g.Engine.State.WinnerIndex>=0});
        Win("decima","discount_lethal","Use the first-buy discount to fast-play Nil Assassin with one gem.",v=>{var g=Game("decima",v,5,1,5);Row(g,v%6,"nil_assassin");return g;},g=>Buy(g,"nil_assassin"));
        Win("decima","discount_competing_purchase","Do not spend the first-buy discount on a free Skirmisher when discounted Nil Assassin wins.",v=>{var g=Game("decima",v,5,1,5);Row(g,v%6,"nil_assassin");Row(g,(v+1)%6,"wraethe_skirmisher_duel");return g;},g=>Buy(g,"nil_assassin"));
        Win("decima","praetorian01_threshold","Focus from 19 to 20 before Praetorian-01: 12 power wins; 8 does not.",v=>{var g=Game("decima",v,19,1,10,"praetorian_01");Own(g).FocusedThisTurn=false;Give(g,"praetorian_01");return g;},g=>{Focus(g);Play(g,"praetorian_01");});
        Win("decima","praetorian01_recall","Deploy a champion to recover Praetorian-01 from discard and win.",v=>{var g=Game("decima",v,10,0,8,"praetorian_01");Give(g,"li_hin_duel");return g;},g=>{Play(g,"li_hin_duel");Play(g,"praetorian_01");});
        Win("decima","praetorian03_mastery","Play Praetorian-03 at mastery 27 before Infinity Shard.",v=>{var g=Game("decima",v,27,0,50,"praetorian_03");Give(g,"praetorian_03");Give(g,"infinity_shard");return g;},g=>{Play(g,"praetorian_03");Play(g,"infinity_shard");});

        Win("tetra","ability_draw_lethal","Spend three gems to draw the two remaining Blasters and win.",v=>{var g=Game("tetra",v,5,3,2);Give(g,"blaster",ShardsZone.Deck);Give(g,"blaster",ShardsZone.Deck);return g;},g=>{Hero(g);Play(g,"blaster");Play(g,"blaster");});
        Win("tetra","buy_instead_of_draw","Fast-play Nil Assassin for lethal; drawing two healers consumes the needed gems.",v=>{var g=Game("tetra",v,5,3,5);Give(g,"spore_cleric",ShardsZone.Deck);Give(g,"spore_cleric",ShardsZone.Deck);Row(g,v%6,"nil_assassin");return g;},g=>Buy(g,"nil_assassin"));
        Win("tetra","focus_unlock_draw","Focus from mastery 4 to 5, then use the hero draw to find lethal.",v=>{var g=Game("tetra",v,4,4,2);Own(g).FocusedThisTurn=false;Give(g,"blaster",ShardsZone.Deck);Give(g,"blaster",ShardsZone.Deck);return g;},g=>{Focus(g);Hero(g);Play(g,"blaster");Play(g,"blaster");});
        Win("tetra","crescents_threshold","Focus 17 to 18 before Terminal Crescents; its own +2 then unlocks 20 power.",v=>{var g=Game("tetra",v,17,1,15,"terminal_crescents_duel");Own(g).FocusedThisTurn=false;Give(g,"terminal_crescents_duel");return g;},g=>{Focus(g);Play(g,"terminal_crescents_duel");});
        Win("tetra","multitask_faction_order","Deploy Homodeus and Wraethe champions before Multitask Brain for lethal faction scaling.",v=>{var g=Game("tetra",v,10,0,6,"multitask_brain");Own(g).HeroAbilityUsedThisTurn=true;Give(g,"multitask_brain");Give(g,"li_hin_duel");Give(g,"testudo_vanguard");return g;},g=>{Play(g,"li_hin_duel");Play(g,"testudo_vanguard");Play(g,"multitask_brain");});

        Win("volos","power_mode","Choose the one-gem power mode for immediate lethal.",v=>Game("volos",v,5,1,2),g=>{Hero(g);Pick(g,"soivolos:1");});
        Win("volos","draw_mode","Choose the two-gem draw mode to draw the only remaining card, Nil Assassin, for lethal.",v=>{var g=Game("volos",v,5,2,5);Give(g,"nil_assassin",ShardsZone.Deck);return g;},g=>{Hero(g);Pick(g,"soivolos:2");Play(g,"nil_assassin");});
        Win("volos","mastery_mode","Choose mastery at 29 before Infinity Shard; Focus is already spent.",v=>{var g=Game("volos",v,29,3,50,"unknown_god");Give(g,"infinity_shard");return g;},g=>{Hero(g);Pick(g,"soivolos:3");Play(g,"infinity_shard");});
        Win("volos","talons_before_free_heal","Play Entropic Talons before the free heal mode to convert three health into lethal power.",v=>{var g=Game("volos",v,10,0,3,"entropic_talons");Give(g,"entropic_talons");return g;},g=>{Play(g,"entropic_talons");Hero(g);Pick(g,"soivolos:0");});
        Win("volos","unknown_god_before_exhaust","Deploy Unknown God at mastery 20 before exhausting Axia, doubling seven power to lethal fourteen.",v=>{var g=Game("volos",v,20,0,14,"unknown_god");Own(g).HeroAbilityUsedThisTurn=true;Give(g,"unknown_god");Give(g,"axia_duel",ShardsZone.Champions);return g;},g=>{Play(g,"unknown_god");Tap(g,"axia_duel");});
        Win("volos","crown_before_infinity","Play Panconscious Crown at mastery 28 before Infinity Shard.",v=>{var g=Game("volos",v,28,0,50,"panconscious_crown_duel");Give(g,"panconscious_crown_duel");Give(g,"infinity_shard");return g;},g=>{Play(g,"panconscious_crown_duel");Play(g,"infinity_shard");});

        Win("kosynwu","preserve_only_winning_card","Do not sacrifice the only Nil Assassin in hand: playing it wins now.",v=>{var g=Game("kosynwu",v,5,0,5);Give(g,"nil_assassin");return g;},g=>Play(g,"nil_assassin"));
        Win("kosynwu","decline_bad_sacrifice","In the sacrifice preview, decline when the only target is a mastery-30 Infinity Shard.",v=>{var g=Game("kosynwu",v,30,0,50,"doom_gate");Own(g).Discard.Clear();Give(g,"infinity_shard");Hero(g);return g;},g=>{Finish(g);Play(g,"infinity_shard");});
        Win("kosynwu","world_piercer_targets","World Piercer should return two Nil Assassins, not Spore Cleric, to assemble ten lethal power.",v=>{var g=Game("kosynwu",v,10,0,10,"world_piercer_duel");Give(g,"world_piercer_duel");Give(g,"nil_assassin",ShardsZone.Deck);Give(g,"nil_assassin",ShardsZone.Discard);Give(g,"spore_cleric",ShardsZone.Deck);return g;},g=>{Play(g,"world_piercer_duel");Pick(g,"nil_assassin");Pick(g,"nil_assassin");Play(g,"nil_assassin");Play(g,"nil_assassin");});
        Win("kosynwu","world_piercer_mastery","Play World Piercer at mastery 28 before Infinity Shard.",v=>{var g=Game("kosynwu",v,28,0,50,"world_piercer_duel");Give(g,"world_piercer_duel");Give(g,"infinity_shard");return g;},g=>{Play(g,"world_piercer_duel");Play(g,"infinity_shard");});
        Win("kosynwu","doom_gate_mastery_reward","Doom Gate destroys Torment for four mastery, unlocking Infinity Shard at 30.",v=>{var g=Game("kosynwu",v,26,0,50,"doom_gate");Give(g,"doom_gate",ShardsZone.Champions);Give(g,"ingeminex_torment",ShardsZone.MonsterSpace,1-v%2);Give(g,"infinity_shard");return g;},g=>{Tap(g,"doom_gate");if(g.Decision?.Context=="soi.destroy")Pick(g,"ingeminex_torment");Play(g,"infinity_shard");});

        Win("rez","scry_longshot_lethal","Scry away two incompatible champions, then Longshot the Nil Assassin for lethal.",v=>{var g=Game("rez",v,5,0,5);Give(g,"longshot");RezAudit.Top(g,"additri_gaiamancer","testudo_vanguard","nil_assassin");return g;},g=>{Hero(g);Pick(g,"additri_gaiamancer");Pick(g,"testudo_vanguard");Finish(g);Play(g,"longshot");Pick(g,"nil_assassin");if(g.Decision?.Context=="soi.warp")Finish(g);});
        Win("rez","star_seeker_threshold","Focus 19 to 20 before Star Seeker for two Nil Assassin warps instead of one.",v=>{var g=Game("rez",v,19,1,10,"star_seeker");Own(g).FocusedThisTurn=false;Own(g).HeroAbilityUsedThisTurn=true;Give(g,"star_seeker",ShardsZone.Champions);Row(g,v%6,"nil_assassin");Row(g,(v+1)%6,"nil_assassin");return g;},g=>{Focus(g);Tap(g,"star_seeker");Pick(g,"nil_assassin");Pick(g,"nil_assassin");});
        Win("rez","warpquartz_banish_lethal","Warpquartz banishes a starter to produce immediate lethal power.",v=>{var g=Game("rez",v,10,0,3,"warpquartz_duel");Give(g,"warpquartz_duel");Give(g,"crystal");Own(g).Discard.Clear();Play(g,"warpquartz_duel");return g;},g=>{Pick(g,"crystal");if(g.Decision?.Context=="soi.banish")Finish(g);});
        Win("rez","immediate_infinity","Take the already available mastery-30 win while Scry is also available.",v=>{var g=Game("rez",v,30);Give(g,"infinity_shard");return g;},g=>Play(g,"infinity_shard"));
        Win("rez","free_reroll_known_lethal","After Scry keeps a known Nil Assassin on top, reroll a champion for free and fast-play the Assassin.",v=>{var g=Game("rez",v,5,2,5);Row(g,v%6,"zetta_encryptor");RezAudit.Top(g,"nil_assassin","testudo_vanguard","giga_source_adept");Hero(g);Finish(g);return g;},g=>{Step(g,c=>c.Action is ShardsRerollRowAction);Buy(g,"nil_assassin");});
        list.Add(new Case{Hero="rez",Id="bury_monster_against_doom_gate",Description="Complete Scry by burying Ingeminex when the enemy has Doom Gate and neither target can be killed.",
            Factory=v=>{var g=Game("rez",v,5,opponentHero:"kosynwu");var enemy=g.Engine.State.Players[1-g.Actor];enemy.Mastery=10;enemy.RelicRecruited=true;Give(g,"doom_gate",ShardsZone.Champions,1-g.Actor);string[] top={"ingeminex_agony","nil_assassin","testudo_vanguard"};RezAudit.Top(g,top.Skip(v%3).Concat(top.Take(v%3)).ToArray());Hero(g);return g;},
            Demonstrate=g=>{Pick(g,"ingeminex_agony");Finish(g);},Stop=g=>g.Decision?.Context!="soi.scry",Goal=g=>g.Decision?.Context!="soi.scry"&&!g.Knowledge.For(g.Actor).Contains("ingeminex_agony")});
        foreach(string hero in new[]{"decima","tetra","volos","kosynwu","rez"})
            Win(hero,"focus_before_infinity_29","Focus from mastery 29 to 30 before playing Infinity Shard.",v=>{var g=Game(hero,v,29,1);Own(g).FocusedThisTurn=false;Give(g,"infinity_shard");return g;},g=>{Focus(g);Play(g,"infinity_shard");});
        return list;
    }
    internal static void Run(FrozenPolicy policy,string output,int variants,string filter=null,bool requirePass=false,bool search=false,bool perturb=false,bool sampledSearch=false,bool fresh=false)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();AuditPolicy=policy;
        var cases=Cases();if(filter!=null)cases=cases.Where(c=>c.Id==filter).ToList();
        if(cases.Count==0)throw new Exception("Unknown tactical case "+filter);
        if(perturb)
            foreach(var test in cases)
            {
                var original=test.Factory;
                test.Factory=v=>
                {
                    var g=original(v+(fresh?90000:1000));
                    if(g.Decision==null)
                    {
                        // Held-out distractions: no new action advice or expected sequence.
                        string[] extras={"crystal","blaster","spore_cleric","li_hin_duel","testudo_vanguard"};
                        if(fresh)
                        {
                            extras=new[]{"crystal","blaster","spore_cleric_duel","li_hin_duel","testudo_vanguard","order_initiate_duel","reactor_drone_duel","wraethe_skirmisher_duel"};
                            var random=new Random(812317+v*101);
                            for(int n=0,count=1+random.Next(3);n<count;n++)Give(g,extras[random.Next(extras.Length)]);
                            var hand=Own(g).Hand;
                            for(int n=hand.Count-1;n>0;n--){int j=random.Next(n+1);(hand[n],hand[j])=(hand[j],hand[n]);}
                            RezAudit.Refresh(g);
                        }
                        else {Give(g,extras[v%extras.Length]);Give(g,extras[(v*3+1)%extras.Length]);}
                    }
                    return g;
                };
            }
        var rows=new List<object>();bool allPassed=true;
        foreach(var test in cases)
        {
            int greedyWins=0,sampledWins=0;var failures=new List<object>();var roots=new List<object>();
            for(int v=0;v<variants;v++)
            {
                var teacher=test.Factory(v);DemonstrationTrace=new List<object>();test.Demonstrate(teacher);var demonstration=DemonstrationTrace;DemonstrationTrace=null;
                if(!test.Goal(teacher))throw new Exception("INVALID DEMONSTRATION "+test.Id+" variant "+v);
                var root=test.Factory(v);if(search)SearchDiagnostics.AssertPrivacy(root);var en=Encode(root);var probabilities=policy.Probabilities(en.o,en.c,en.m,out float value);
                var own=Own(root);var enemy=root.Engine.State.Players[1-root.Actor];
                if(Math.Abs(en.o[16]-own.Health/50f)>1e-7||Math.Abs(en.o[17]-own.Mastery/30f)>1e-7||Math.Abs(en.o[18]-own.Gems/20f)>1e-7||Math.Abs(en.o[19]-own.Power/100f)>1e-7||Math.Abs(en.o[64]-enemy.Health/50f)>1e-7||en.o[HeroFeatures.Slots[12+Encoder.HeroIndex(test.Hero)]]!=1)
                    throw new Exception("Missing resource/hero information "+test.Id);
                var probes=new List<object>();
                if(v==0)
                {
                    foreach(bool ownLow in new[]{true,false})
                    {
                        int health=ownLow?own.Health:enemy.Health;if(ownLow)own.Health=2;else enemy.Health=50;
                        var z=Encode(root);var probability=policy.Probabilities(z.o,z.c,z.m,out float probeValue);
                        probes.Add(new{change=ownLow?"own_health_2":"enemy_health_50",changed_observation_slots=Enumerable.Range(0,z.o.Length).Where(i=>z.o[i]!=en.o[i]).ToArray(),value=probeValue,actions=Enumerable.Range(0,root.VisibleCount).Select(i=>new{name=Name(root,root.Visible(i)),p=probability[i]}).OrderByDescending(x=>x.p).ToArray()});
                        if(ownLow)own.Health=health;else enemy.Health=health;
                    }
                }
                // Same public collection, different private opponent hand/draw partition.
                var opponent=root.Engine.State.Players[1-root.Actor];var swap=opponent.Hand[0];opponent.Hand[0]=opponent.Deck[0];opponent.Deck[0]=swap;opponent.Hand[0].Zone=ShardsZone.Hand;swap.Zone=ShardsZone.Deck;
                var hidden=Encode(root);
                bool privateInvariant=en.o.SequenceEqual(hidden.o)&&en.c.SequenceEqual(hidden.c)&&en.m.SequenceEqual(hidden.m);
                if(!privateInvariant)throw new Exception("Hidden partition leak "+test.Id);
                var before=Enumerable.Range(0,root.VisibleCount).Select(i=>probabilities[i]).ToArray();root.Candidates.Reverse();var perm=Encode(root);var pp=policy.Probabilities(perm.o,perm.c,perm.m,out _);
                double permutationError=Enumerable.Range(0,root.VisibleCount).Max(i=>Math.Abs(pp[i]-before[root.VisibleCount-1-i]));root.Candidates.Reverse();
                if(permutationError>0.0001)throw new Exception("Action position dependence "+test.Id);
                roots.Add(new{variant=v,seat=root.Actor,value,probes,private_invariant=privateInvariant,permutation_error=permutationError,demonstration,observation=v==0?en.o:null,actions=Enumerable.Range(0,root.VisibleCount).Select(i=>new{name=Name(root,root.Visible(i)),p=probabilities[i]}).OrderByDescending(x=>x.p).ToArray()});
                for(int sample=-1;sample<(search&&!sampledSearch?0:8);sample++)
                {
                    var g=test.Factory(v);int seat=g.Actor;var rng=sample<0?null:new Random(271100+v*101+sample);var trace=new List<object>();
                    for(int n=0;n<100&&!g.Engine.State.GameOver&&g.Engine.State.TurnPlayerIndex==seat&&!(test.Stop?.Invoke(g)??false);n++)
                    {
                        en=Encode(g);var p=policy.Probabilities(en.o,en.c,en.m,out value);int chosen=Array.IndexOf(p,p.Max());
                        if(rng!=null){double d=rng.NextDouble();for(int a=0;a<g.VisibleCount;a++){chosen=a;d-=p[a];if(d<0)break;}}
                        if(search){int improved=TacticalSearch.Find(g);if(improved>=0)chosen=improved;}
                        if(sample<0||failures.Count<2)trace.Add(new{step=n,actor=g.Actor,context=g.Decision?.Context,action=Name(g,g.Visible(chosen)),probability=p[chosen],mastery=Own(g).Mastery,power=Own(g).Power,gems=Own(g).Gems,value});
                        if(search)SearchDiagnostics.AssertAdvance(g,chosen);else g.Step(chosen);
                    }
                    bool success=test.Goal(g);if(sample<0){if(success)greedyWins++;}else if(success)sampledWins++;
                    if(!success&&(sample<0||failures.Count<2))failures.Add(new{variant=v,sample,winner=g.Engine.State.WinnerIndex,trace});
                }
            }
            rows.Add(new{hero=test.Hero,id=test.Id,description=test.Description,variants,verified_goal_lines=variants,greedy_wins=greedyWins,sampled_wins=sampledWins,sampled_games=search&&!sampledSearch?0:variants*8,roots,failures});
            allPassed &= greedyWins/(double)variants>=.95 && sampledWins/(double)(variants*8)>=.95;
            Console.WriteLine($"{test.Hero}/{test.Id}: greedy {greedyWins}/{variants}; sampled {sampledWins}/{(search&&!sampledSearch?0:variants*8)}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output,JsonConvert.SerializeObject(new{schema="hero-tactics-v2",evaluation_only=true,search,perturb,fresh,sampling=search?(sampledSearch?"public-world lookahead, greedy plus eight sampled fallbacks":"public-world lookahead, greedy fallback"):"greedy plus 8 independently seeded samples per position",cases=rows},Formatting.Indented));
        }
        if(filter==null&&rows.Count!=32)throw new Exception("Unexpected scenario coverage "+rows.Count);
        if(requirePass&&!allPassed)Environment.ExitCode=2;
    }
}
