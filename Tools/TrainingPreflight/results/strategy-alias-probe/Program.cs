using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using Shards.Content;
using Shards.Engine;
using Pascension.Engine.Actions;
const BindingFlags SI=BindingFlags.Static|BindingFlags.NonPublic, II=BindingFlags.Instance|BindingFlags.NonPublic;
var asm=typeof(ShardsEngine).Assembly;
var at=asm.GetType("Shards.Preflight.Adapter"); var st=asm.GetType("Shards.Preflight.SelfTest");
ShardsContentRegistry.EnsureRegistered(); asm.GetType("Shards.Preflight.Encoder").GetMethod("Initialize",SI).Invoke(null,null);
ShardsEngine Engine(object g)=>(ShardsEngine)at.GetField("Engine",II).GetValue(g);
float[] Encode(object g)=>(float[])st.GetMethod("Encode",SI).Invoke(null,new[]{g});
void Refresh(object g)=>st.GetMethod("RefreshFixture",SI).Invoke(null,new[]{g});
void Submit(object g,PlayerAction a)=>at.GetMethod("Submit",II).Invoke(g,new object[]{a});
ShardsCard Add(object g,ShardsPlayer p,string id,ShardsZone z,List<ShardsCard> l)=>(ShardsCard)st.GetMethod("AddCard",SI).Invoke(null,new object[]{g,p,id,z,l});
object Reactor(bool copied) {
 var g=st.GetMethod("AfterDraft",SI).Invoke(null,new object[]{(ulong)926726});var e=Engine(g);var p=e.State.TurnPlayer;
 p.Hand.Clear();p.PlayZone.Clear();p.ResetTurn();
 var drone=Add(g,p,"reactor_drone_duel",ShardsZone.PlayZone,p.PlayZone);
 var ojas=Add(g,p,"ojas_genesis_druid",ShardsZone.PlayZone,p.PlayZone);
 p.PlayedThisTurn.Add(drone);p.PlayedThisTurn.Add(ojas);p.CountFactionPlay(drone.Def.Faction,true);p.CountFactionPlay(ojas.Def.Faction,true);
 Refresh(g);
 typeof(ShardsEngine).GetMethod("QueueEffect",II).Invoke(e,new object[]{drone.Def.PlayEffect,p.Index,copied?ojas:drone});
 typeof(ShardsEngine).GetMethod("Pump",II).Invoke(e,null);at.GetMethod("Rebuild",II).Invoke(g,null);
 if(e.PendingInput.Decision?.Context!="soi.mode")throw new Exception("Expected reactor mode");
 return g;
}
if(args.Contains("--reactor")) {
 var ra=Reactor(false);var rb=Reactor(true);var xa=Encode(ra);var xb=Encode(rb);
 var different=Enumerable.Range(0,xa.Length).Where(i=>xa[i]!=xb[i]).ToArray();
 File.WriteAllText("Tools/TrainingPreflight/results/strategy-reactor-source-inputs.json",JsonSerializer.Serialize(new {schema="shards-flat-policy-fixtures-v1",layout=new{obs=2048,candidates=2048,mask=64},scope="Constructed parked effect contexts via reflection; not complete legal replay",cases=new[]{new{name="physical_reactor",flat=xa},new{name="copied_reactor_ojas",flat=xb}}}));
 object Resolve(object g){var e=Engine(g);var p=e.State.TurnPlayer;Submit(g,new SubmitDecisionAction{PlayerIndex=p.Index,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=e.PendingInput.Decision.Id,ChosenOptionIds=new List<int>{2}}});return new{gems=p.Gems,droneBanishAtCleanup=p.PlayZone.Single(c=>c.DefId=="reactor_drone_duel").BanishAtCleanup};}
 Console.WriteLine(JsonSerializer.Serialize(new{schema="shards-v7-reactor-source-alias-v1",scope="Constructed identical public zones; queue actual registered Reactor effect with actual versus copier source via isolated reflection; not a full legal replay",floatsCompared=xa.Length,differences=different,exactAlias=different.Length==0,physical=Resolve(ra),copied=Resolve(rb)},new JsonSerializerOptions{WriteIndented=true}));
 if(different.Length!=0)Environment.Exit(1);return;
}
object Make(bool reverse) {
 var g=st.GetMethod("AfterDraft",SI).Invoke(null,new object[]{(ulong)925726}); var e=Engine(g); var p=e.State.TurnPlayer; var o=e.State.Players[1-p.Index];
 p.Hand.Clear();p.Deck.Clear();p.Discard.Clear();p.PlayZone.Clear();p.Champions.Clear(); p.Mastery=5;p.ResetTurn();
 o.Deck.Clear(); Add(g,o,"crystal",ShardsZone.Deck,o.Deck);
 Add(g,p,"crystal",ShardsZone.Deck,p.Deck);Add(g,p,"blaster",ShardsZone.Deck,p.Deck);
 if(reverse)p.Deck.Reverse();
 var f=Add(g,p,"duplication_fabricator_duel",ShardsZone.Hand,p.Hand);Add(g,p,"cinder_scars_duel",ShardsZone.Hand,p.Hand);Refresh(g);
 Submit(g,new ShardsPlayCardAction{PlayerIndex=p.Index,CardInstanceId=f.InstanceId});
 if(e.PendingInput.Decision?.Context!="soi.copy")throw new Exception("no copy");
 var target=e.PendingInput.Decision.Options.Single(x=>x.CardInstanceId==o.Deck[0].InstanceId);
 Submit(g,new SubmitDecisionAction{PlayerIndex=p.Index,Answer=new Pascension.Engine.Decisions.DecisionAnswer{DecisionId=e.PendingInput.Decision.Id,ChosenOptionIds=new List<int>{target.Id}}});
 return g;
}
var a=Make(false);var b=Make(true);var ea=Encode(a);var eb=Encode(b);
var diffs=Enumerable.Range(0,ea.Length).Where(i=>ea[i]!=eb[i]).ToArray();
File.WriteAllText("Tools/TrainingPreflight/results/strategy-own-top-inputs.json",JsonSerializer.Serialize(new {schema="shards-flat-policy-fixtures-v1",layout=new{obs=2048,candidates=2048,mask=64},scope="Constructed initial fixture, then accepted legal Fabricator play and opponent-Crystal copy; not full initial-seed replay",cases=new[]{new{name="known_own_top_blaster",flat=ea},new{name="known_own_top_crystal",flat=eb}}}));
object Details(object g){var e=Engine(g);var p=e.State.TurnPlayer;var top=p.Deck[^1].DefId;var c=p.Hand.Single(x=>x.DefId=="cinder_scars_duel");Submit(g,new ShardsPlayCardAction{PlayerIndex=p.Index,CardInstanceId=c.InstanceId});return new{revealedTopBefore=top,handAfterDraw=p.Hand.Select(x=>x.DefId).ToArray()};}
Console.WriteLine(JsonSerializer.Serialize(new {schema="shards-v7-known-own-top-alias-v1",scope="Constructed fixture then legal Fabricator play and opponent-Crystal copy; no full initial-seed reachability assertion",floatsCompared=ea.Length,differences=diffs,exactAlias=diffs.Length==0,caseA=Details(a),caseB=Details(b),hostHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asm.Location))).ToLowerInvariant()},new JsonSerializerOptions{WriteIndented=true}));
if(diffs.Length!=0)Environment.Exit(1);
