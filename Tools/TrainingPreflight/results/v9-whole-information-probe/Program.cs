using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using Shards.Content;
using Shards.Engine;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
const BindingFlags SI=BindingFlags.NonPublic|BindingFlags.Static, II=BindingFlags.NonPublic|BindingFlags.Instance;
var asm=typeof(ShardsEngine).Assembly;var at=asm.GetType("Shards.Preflight.Adapter");var st=asm.GetType("Shards.Preflight.SelfTest");
ShardsContentRegistry.EnsureRegistered();asm.GetType("Shards.Preflight.Encoder").GetMethod("Initialize",SI).Invoke(null,null);
ShardsEngine Engine(object g)=>(ShardsEngine)at.GetField("Engine",II).GetValue(g);
int Actor(object g)=>(int)at.GetProperty("Actor",II).GetValue(g);
DecisionRequest Decision(object g)=>(DecisionRequest)at.GetProperty("Decision",II).GetValue(g);
float[] Encode(object g)=>(float[])st.GetMethod("Encode",SI).Invoke(null,new[]{g});
void Refresh(object g){var e=Engine(g);e.State.InvalidateCardIndex();if(e.PendingInput.Decision==null)e.PendingInput.LegalActions=e.LegalActions(Actor(g));at.GetMethod("Rebuild",II).Invoke(g,null);}
void Submit(object g,PlayerAction action)=>at.GetMethod("Submit",II).Invoke(g,new object[]{action});
void Answer(object g,int option)=>Submit(g,new SubmitDecisionAction{PlayerIndex=Actor(g),Answer=new DecisionAnswer{DecisionId=Decision(g).Id,ChosenOptionIds=new List<int>{option}}});
object Fixture(ulong seed=927993)=>(object)st.GetMethod("AfterDraft",SI).Invoke(null,new object[]{seed});
ShardsCard Add(object g,ShardsPlayer p,string id,ShardsZone zone,List<ShardsCard> target){var c=new ShardsCard{InstanceId=Engine(g).State.NextInstanceId++,DefId=id,Owner=p?.Index??-1,Zone=zone};target.Add(c);return c;}
void Clear(ShardsPlayer p){p.Hand.Clear();p.Deck.Clear();p.Discard.Clear();p.PlayZone.Clear();p.Champions.Clear();p.Destinies.Clear();p.ResetTurn();p.Health=50;p.Mastery=10;}
var fixtures=new List<object>();var flats=new List<object>();int checks=0;bool requireDistinct=args.Contains("--require-distinct");
void Check(bool okay,string label){checks++;if(!okay)throw new Exception(label);}
void Alias(string name,object a,object b,object consequences,string scope){var xa=Encode(a);var xb=Encode(b);var differences=Enumerable.Range(0,xa.Length).Where(i=>xa[i]!=xb[i]).ToArray();fixtures.Add(new{name,alias=differences.Length==0,differences,consequences,scope});flats.Add(new{name,caseA=xa,caseB=xb});Check(requireDistinct?differences.Length>0:differences.Length==0,requireDistinct?"Regression: authorized public information is aliased: "+name:"Expected frozen alias not present: "+name);}

// Real accepted Shard Defiant actions, with only a constructed initial top order.
object Banished(bool reverse){var g=Fixture();var e=Engine(g);var p=e.State.Players[Actor(g)];p.Gems=2;p.ResetTurn();p.Gems=2;
var ids=new[]{"fungal_hermit","shard_abstractor"};var cards=ids.Select(id=>e.State.CenterDeck.First(c=>c.DefId==id)).ToArray();foreach(var c in cards)e.State.CenterDeck.Remove(c);
foreach(var c in reverse?cards.Reverse():cards)e.State.CenterDeck.Add(c);
var destiny=Add(g,p,"shard_defiant",ShardsZone.SetAside,p.Destinies);Refresh(g);
Submit(g,new ShardsExhaustAction{PlayerIndex=p.Index,CardInstanceId=destiny.InstanceId});Check(Decision(g)?.Context=="soi.defiant","Defiant actual decision");Answer(g,2);return g;}
var ba=Banished(false);var bb=Banished(true);Alias("public_banished_identity",ba,bb,new{a=Engine(ba).State.Banished.Select(c=>c.DefId),b=Engine(bb).State.Banished.Select(c=>c.DefId)},"Constructed initial center-top order followed by accepted exhaust and banish decisions; banished identities public, order of remaining center deck hidden");

// Same public monster board/count, different publicly pending attack identities.
object Monster(bool torment){var g=Fixture(927994);var e=Engine(g);foreach(var p in e.State.Players)Clear(p);e.State.ActiveMonsters.Clear();e.State.PendingMonsterAttacks.Clear();
var brute=Add(g,null,"ingeminex_brutality",ShardsZone.MonsterSpace,e.State.ActiveMonsters);var tor=Add(g,null,"ingeminex_torment",ShardsZone.MonsterSpace,e.State.ActiveMonsters);
e.State.PendingMonsterAttacks.Add(torment?tor.InstanceId:brute.InstanceId);Refresh(g);return g;}
var ma=Monster(false);var mb=Monster(true);Alias("pending_monster_attack_identity",ma,mb,new{a="Brutality: both players lose5health",b="Torment: both players lose2mastery"},"Constructed identical board with different pending attack list; public reveal history identifies pending monsters");
Submit(ma,new ShardsEndTurnAction{PlayerIndex=Actor(ma)});Submit(mb,new ShardsEndTurnAction{PlayerIndex=Actor(mb)});
Check(Engine(ma).State.Players[0].Health==45&&Engine(mb).State.Players[0].Mastery==8,"Distinct accepted end-turn monster consequences");

// Both cases actually play Reactor with Decurion duplication already armed.
object Queue(bool second){var g=Fixture(927995);var e=Engine(g);var p=e.State.Players[Actor(g)];Clear(p);p.CopyHomodeusAlliesThisTurn=true;p.Gems=second?0:2;
var drone=Add(g,p,"reactor_drone_duel",ShardsZone.Hand,p.Hand);Refresh(g);Submit(g,new ShardsPlayCardAction{PlayerIndex=p.Index,CardInstanceId=drone.InstanceId});
if(second)Answer(g,1);Check(Decision(g)?.Context=="soi.mode"&&p.Gems==2,"Matching first/second Reactor contexts");return g;}
var qa=Queue(false);var qb=Queue(true);Alias("queued_effect_progress",qa,qb,new{a="First Reactor resolution; another is queued",b="Second Reactor resolution; no duplicate remains"},"Constructed public duplicate-effect flag and different starting gems, then accepted play/choice actions reach identical visible inputs with different public continuation progress");
Answer(qa,1);Answer(qb,1);Check(Decision(qa)?.Context=="soi.mode"&&Decision(qb)==null,"Same accepted option leaves different continuations");

object Flags(bool fastBanish){var g=Fixture(927996);var e=Engine(g);var p=e.State.Players[Actor(g)];Clear(p);var permanent=Add(g,p,"reactor_drone_duel",ShardsZone.PlayZone,p.PlayZone);var temporary=Add(g,p,"reactor_drone_duel",ShardsZone.PlayZone,p.PlayZone);
temporary.FastPlayed=true;permanent.BanishAtCleanup=!fastBanish;temporary.BanishAtCleanup=fastBanish;Refresh(g);return g;}
var fa=Flags(false);var fb=Flags(true);Alias("per_card_temporary_banish_association",fa,fb,new{a="Permanent Reactor marked; fast Reactor unmarked",b="Fast Reactor marked; permanent Reactor unmarked"},"Constructed public per-card statuses; both choices are producible by physical Reactor modes, but this fixture does not replay their setup");
int fseat=Actor(fa);Submit(fa,new ShardsEndTurnAction{PlayerIndex=fseat});Submit(fb,new ShardsEndTurnAction{PlayerIndex=fseat});
int OwnedReactors(object g)=>Engine(g).State.Players[fseat].Deck.Concat(Engine(g).State.Players[fseat].Hand).Concat(Engine(g).State.Players[fseat].Discard).Count(c=>c.DefId=="reactor_drone_duel");
Check(OwnedReactors(fa)==0&&OwnedReactors(fb)==1,"Accepted cleanup loses or keeps permanent Reactor despite identical inputs");
var sticky=Engine(fb).State.CenterDeck.Where(c=>c.DefId=="reactor_drone_duel"&&c.BanishAtCleanup).ToArray();
Check(sticky.Length==1,"Fast-play return leaves Reactor banish flag on center card");

// Accepted real card-effect path to the sticky flag: Deadly Recruits -> Reactor
// -> decline keep -> choose3 gems -> end turn. No flags are set by this fixture.
var warped=Fixture(928006);var we=Engine(warped);var wp=we.State.Players[Actor(warped)];Clear(wp);wp.Mastery=20;
var oldRow=we.State.CenterRow[0];we.State.CenterDeck.Add(oldRow);oldRow.Zone=ShardsZone.CenterDeck;
var reactor=new ShardsCard{InstanceId=we.State.NextInstanceId++,DefId="reactor_drone_duel",Owner=-1,Zone=ShardsZone.CenterRow};we.State.CenterRow[0]=reactor;
var deadly=Add(warped,wp,"deadly_recruits_duel",ShardsZone.SetAside,wp.Destinies);Refresh(warped);
Submit(warped,new ShardsExhaustAction{PlayerIndex=wp.Index,CardInstanceId=deadly.InstanceId});Check(Decision(warped)?.Context=="soi.warp","Deadly Recruits opens actual warp choice");Answer(warped,0);
Check(Decision(warped)?.Context=="soi.keepfast","Deadly Recruits opens keep decision");
Submit(warped,new SubmitDecisionAction{PlayerIndex=Actor(warped),Answer=new DecisionAnswer{DecisionId=Decision(warped).Id,ChosenOptionIds=new List<int>()}});
Check(Decision(warped)?.Context=="soi.mode","Actual unkept physical Reactor opens mode");Answer(warped,2);
Check(reactor.FastPlayed&&reactor.BanishAtCleanup&&wp.Gems==3,"Actual Reactor3gem path marks temporary physical card");
wp.Power=0;Submit(warped,new ShardsEndTurnAction{PlayerIndex=wp.Index});
Check(we.State.CenterDeck.Contains(reactor)&&reactor.Owner==-1&&reactor.BanishAtCleanup,"Accepted full effect path returns sticky Reactor to center");
var warpedEvidence=new{reachedBy="accepted DeadlyRecruits/chooseReactor/declineKeep/choose3gems/endTurn",centerOwned=reactor.Owner,inCenter=we.State.CenterDeck.Contains(reactor),banishAtCleanup=reactor.BanishAtCleanup};

// Relic identity is a static hero mapping, not a pre-eligibility observation.
var ra=Fixture(927997);var rb=Fixture(927997);var rpa=Engine(ra).State.Players[Actor(ra)];var rpb=Engine(rb).State.Players[Actor(rb)];rpa.Mastery=rpb.Mastery=9;
var removed=rpb.SetAside[0];rpb.SetAside.RemoveAt(0);Refresh(ra);Refresh(rb);Alias("remaining_set_aside_before_mastery10",ra,rb,new{a=rpa.SetAside.Select(c=>c.DefId),b=rpb.SetAside.Select(c=>c.DefId)},"Constructed different own-known remaining relic pool; same hero below eligibility. Not a claim initial same-hero pool is randomized.");
int RelicOptions(object g)=>Engine(g).LegalActions(Actor(g)).OfType<ShardsRecruitRelicAction>().Count();
Check(RelicOptions(ra)==0&&RelicOptions(rb)==0,"No normal relic actions at mastery9");rpa.Mastery=rpb.Mastery=10;Refresh(ra);Refresh(rb);
Check(RelicOptions(ra)==3&&RelicOptions(rb)==2,"Mastery10 exposes exactly remaining obtainable relics");Check(!Encode(ra).SequenceEqual(Encode(rb)),"Relic identities become encoded once eligible");
var relics=ShardsEngine.DraftableCharacters.ToDictionary(hero=>hero,hero=>ShardsEngine.RelicIdsFor(hero,ShardsEngine.NormalizeDlc(ShardsDlc.Duel)));

// The policy wire does not describe card effect amounts. Change only effect code
// IN THIS ISOLATED PROCESS, keep identity/printed attributes constant, and restore.
var ea=Fixture(927998);var ep=Engine(ea).State.Players[Actor(ea)];Clear(ep);var crystal=Add(ea,ep,"crystal",ShardsZone.Hand,ep.Hand);Refresh(ea);var effectBefore=Encode(ea);var def=crystal.Def;var original=def.PlayEffect;
try{def.PlayEffect=new Gain{Gems=4};var effectAfter=Encode(ea);Check(effectBefore.SequenceEqual(effectAfter),"Effect-only balance change leaves every input identical");Submit(ea,new ShardsPlayCardAction{PlayerIndex=ep.Index,CardInstanceId=crystal.InstanceId});Check(ep.Gems==4,"Modified actual registered effect changes gameplay");fixtures.Add(new{name="effect_only_rebalance",alias=true,differences=Array.Empty<int>(),consequences=new{oldCrystalGems=1,newCrystalGems=4},scope="Isolated registry mutation restored finally; frozen DLL and live engine are untouched"});}
finally{def.PlayEffect=original;}

var resource=Fixture(927999);var rp=Engine(resource).State.Players[Actor(resource)];int resourceChecks=0;
foreach(var values in new[]{(hp:1,m:0,g:0,p:0),(hp:23,m:9,g:3,p:17),(hp:50,m:10,g:12,p:99),(hp:49,m:30,g:100,p:500)})
{rp.Health=values.hp;rp.Mastery=values.m;rp.Gems=values.g;rp.Power=values.p;Refresh(resource);var x=Encode(resource);
foreach(var pair in new[]{(index:16,value:values.hp/50f),(index:17,value:values.m/30f),(index:18,value:values.g/20f),(index:19,value:values.p/100f)}){Check(x[pair.index]==pair.value,"Own resources exactly normalized");resourceChecks++;}}
var focus=Fixture(928004);var fp=Engine(focus).State.Players[Actor(focus)];fp.Mastery=9;fp.Gems=1;fp.Power=7;fp.Health=41;Refresh(focus);
Check(RelicOptions(focus)==0,"Actual Focus starts below relic threshold");Submit(focus,new ShardsFocusAction{PlayerIndex=fp.Index});var fobs=Encode(focus);
Check(fp.Mastery==10&&fp.Gems==0&&fobs[17]==10/30f&&fobs[18]==0&&fobs[19]==7/100f&&fobs[16]==41/50f,"Accepted Focus changes exactly gem/mastery observations");
Check(RelicOptions(focus)==3,"Accepted mastery9->10 Focus immediately unlocks three hero relics");
var recruit=Engine(focus).LegalActions(fp.Index).OfType<ShardsRecruitRelicAction>().First();Submit(focus,recruit);
Check(fp.RelicRecruited&&fp.Gems==0&&RelicOptions(focus)==0&&fp.Discard.Any(c=>c.InstanceId==recruit.CardInstanceId),"Accepted relic recruit is free, exactly once, enters discard");

var active=new HashSet<string>();var setup=Engine(Fixture(928001)).State;
foreach(var c in setup.CenterDeck.Concat(setup.CenterRow).Concat(setup.DestinyRow).Concat(setup.DestinyDeck).Concat(setup.ActiveMonsters))if(c!=null)active.Add(c.DefId);
foreach(var p in setup.Players)foreach(var c in p.Hand.Concat(p.Deck).Concat(p.SetAside))active.Add(c.DefId);
foreach(var list in relics.Values)foreach(var id in list)active.Add(id);
var definitions=ShardsCardDatabase.All.Where(d=>active.Contains(d.Id)).OrderBy(d=>d.Id).ToArray();
string[] hooks={"ShieldInPlay","ExhaustGemCost","CanBeAttacked","Taunt","DefenseAura","CostModifier","ReturnsFromDiscardOnChampionPlay","OnDamageDealt","KeepFastPlaysCharacter","RedirectChampionRecruitsToDeckTop","RecruitsToHand","DynamicShield","ReturnFromDiscardOnFactionPlay","CountsAsEveryFaction","CannotBeRerolled","CannotBeFastPlayed","ShieldsProtectChampions","DiscardPassiveShield","KeepFastPlaysAtMastery","ImmuneToIngeminex","DoublesExhaustsAtMastery"};
bool NonDefault(object v)=>v switch{null=>false,bool b=>b,int n=>n!=0&&n!=-1,string s=>s.Length>0,ShardsFaction f=>f!=ShardsFaction.None,_=>true};
var hookCounts=hooks.ToDictionary(name=>name,name=>definitions.Count(d=>NonDefault(typeof(ShardsCardDef).GetField(name).GetValue(d))));
var effectCounts=new[]{"PlayEffect","ExhaustEffect","RewardEffect","MonsterAttackEffect"}.ToDictionary(name=>name,name=>definitions.Count(d=>typeof(ShardsCardDef).GetField(name).GetValue(d)!=null));
var hookCards=definitions.Where(d=>hooks.Any(name=>NonDefault(typeof(ShardsCardDef).GetField(name).GetValue(d)))).Select(d=>d.Id).ToArray();
var effectNodeTypes=new SortedDictionary<string,int>();int customCards=0;
void VisitEffect(IShardsEffect effect,HashSet<IShardsEffect> seen)
{
 if(effect==null||!seen.Add(effect))return;
 effectNodeTypes[effect.GetType().Name]=effectNodeTypes.GetValueOrDefault(effect.GetType().Name)+1;
 foreach(var field in effect.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
 {
  var value=field.GetValue(effect);if(value is IShardsEffect child)VisitEffect(child,seen);
  else if(value is System.Collections.IEnumerable list&&value is not string)
   foreach(var item in list){if(item is IShardsEffect nested)VisitEffect(nested,seen);else if(item!=null&&item.GetType().IsValueType)
    foreach(var part in item.GetType().GetFields())if(part.GetValue(item) is IShardsEffect branch)VisitEffect(branch,seen);}
 }
}
foreach(var d in definitions){var seen=new HashSet<IShardsEffect>();VisitEffect(d.PlayEffect,seen);VisitEffect(d.ExhaustEffect,seen);VisitEffect(d.RewardEffect,seen);VisitEffect(d.MonsterAttackEffect,seen);if(seen.Any(e=>e is Custom||e is Do))customCards++;}
string folder="Tools/TrainingPreflight/results";
File.WriteAllText(folder+"/v9-whole-information-fixtures-2026-09-27.json",JsonSerializer.Serialize(new{layout=new{obs=2816,candidates=2048,mask=64},cases=flats}));
Console.WriteLine(JsonSerializer.Serialize(new{schema="shards-v9-whole-information-audit-v1",checks,resourceChecks,fixtures,stickyFastReactor=new{found=sticky.Length,centerOwned=sticky[0].Owner,banishAtCleanup=sticky[0].BanishAtCleanup},warpedEvidence,relicMapping=relics,activeDefinitions=definitions.Length,effectCounts,hookCounts,hookCards,effectNodeTypes,customOrDoCards=customCards,hostSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asm.Location))).ToLowerInvariant(),scope="CPU-only isolated constructed fixtures plus accepted continuation actions; no full initial-seed reachability assertion; --require-distinct exits nonzero while aliases remain"},new JsonSerializerOptions{WriteIndented=true}));
