using System.Reflection;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;
const BindingFlags SI=BindingFlags.Static|BindingFlags.NonPublic, II=BindingFlags.Instance|BindingFlags.NonPublic;
var asm=typeof(ShardsEngine).Assembly;var at=asm.GetType("Shards.Preflight.Adapter");var st=asm.GetType("Shards.Preflight.SelfTest");
ShardsContentRegistry.EnsureRegistered();asm.GetType("Shards.Preflight.Encoder").GetMethod("Initialize",SI).Invoke(null,null);
ShardsEngine Engine(object g)=>(ShardsEngine)at.GetField("Engine",II).GetValue(g);
float[] Encode(object g)=>(float[])st.GetMethod("Encode",SI).Invoke(null,new[]{g});
void Add(object g,ShardsPlayer p,string id)=>st.GetMethod("AddCard",SI).Invoke(null,new object[]{g,p,id,ShardsZone.Deck,p.Deck});
object Make(bool undergrowth,int mastery=5) {
 var g=st.GetMethod("AfterDraft",SI).Invoke(null,new object[]{(ulong)926927});var e=Engine(g);var p=e.State.TurnPlayer;var o=e.State.Players[1-p.Index];
 p.ResetTurn();p.Gems=3;o.Mastery=mastery;o.Hand.Clear();o.Deck.Clear();o.Discard.Clear();o.PlayZone.Clear();o.Champions.Clear();o.Destinies.Clear();
 foreach(var id in undergrowth?new[]{"arach_devotees","nectar_alchemist","thorn_zealot"}:new[]{"cloud_oracles","index_of_futures","portal_monk"})Add(g,o,id);
 st.GetMethod("RefreshFixture",SI).Invoke(null,new[]{g});return g;
}
var a=Make(true);var b=Make(false);var c=Make(true,20);var x=Encode(a);var y=Encode(b);var z=Encode(c);
if(x.Length!=2816+2048+64)throw new Exception("Unexpected V9 layout");
var changes=Enumerable.Range(0,x.Length).Where(i=>x[i]!=y[i]).ToArray();
var original=JsonDocument.Parse(File.ReadAllText("Tools/TrainingPreflight/results/reroll-composition-alias-v8.json"));
var old=original.RootElement.GetProperty("cases")[0].GetProperty("flat").EnumerateArray().Select(e=>e.GetSingle()).ToArray();
bool legacyPrefixUnchanged=x.Take(2560).SequenceEqual(old.Take(2560));
bool candidateMaskUnchanged=x.Skip(2816).SequenceEqual(old.Skip(2560));
var privacy=Make(true);var pe=Engine(privacy);var enemy=pe.State.Players[1-pe.State.TurnPlayerIndex];
var h=enemy.Deck[0];enemy.Deck.RemoveAt(0);h.Zone=ShardsZone.Hand;enemy.Hand.Add(h);
st.GetMethod("RefreshFixture",SI).Invoke(null,new[]{privacy});var before=Encode(privacy);
var swap=enemy.Deck[0];enemy.Deck[0]=enemy.Hand[0];enemy.Deck[0].Zone=ShardsZone.Deck;enemy.Hand[0]=swap;swap.Zone=ShardsZone.Hand;
st.GetMethod("RefreshFixture",SI).Invoke(null,new[]{privacy});var afterSwap=Encode(privacy);
enemy.Deck.Reverse();st.GetMethod("RefreshFixture",SI).Invoke(null,new[]{privacy});var afterOrder=Encode(privacy);
bool hiddenMembershipInvariant=before.SequenceEqual(afterSwap),hiddenDrawOrderInvariant=afterSwap.SequenceEqual(afterOrder);
if(changes.Length==0 || changes.Any(i=>i<2560||i>=2816)||!legacyPrefixUnchanged||!candidateMaskUnchanged||!hiddenMembershipInvariant||!hiddenDrawOrderInvariant)throw new Exception("V9 information contract failed");
var output=new {legacyPrefixUnchanged,candidateMaskUnchanged,hiddenMembershipInvariant,hiddenDrawOrderInvariant,schema="shards-reroll-public-collection-alias-v1",scope="Constructed engine fixtures via isolated reflection, not a complete seeded legal replay. Full enemy composition is public under clarified user rules; hand identities and draw order remain hidden.",
 layout=new{obs=2816,candidates=2048,mask=64},exactCompositionAlias=changes.Length==0,compositionDifferenceIndices=changes,
 masteryDifferenceIndices=Enumerable.Range(0,x.Length).Where(i=>x[i]!=z[i]).ToArray(),
 cases=new[]{new{name="enemy_undergrowth_mastery5",enemyDeck=Engine(a).State.Players[1-Engine(a).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=x},new{name="enemy_order_mastery5",enemyDeck=Engine(b).State.Players[1-Engine(b).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=y},new{name="enemy_undergrowth_mastery20",enemyDeck=Engine(c).State.Players[1-Engine(c).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=z}}};
File.WriteAllText("Tools/TrainingPreflight/results/reroll-composition-alias-v9.json",JsonSerializer.Serialize(output));Console.WriteLine(JsonSerializer.Serialize(new {output.exactCompositionAlias,output.compositionDifferenceIndices,output.masteryDifferenceIndices,legacyPrefixUnchanged,candidateMaskUnchanged,hiddenMembershipInvariant,hiddenDrawOrderInvariant}));
