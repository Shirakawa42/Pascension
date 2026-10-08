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
if(x.Length!=2560+2048+64)throw new Exception("Unexpected V8 layout");
var changes=Enumerable.Range(0,x.Length).Where(i=>x[i]!=y[i]).ToArray();
var output=new {schema="shards-reroll-public-collection-alias-v1",scope="Constructed engine fixtures via isolated reflection, not a complete seeded legal replay. Full enemy composition is public under clarified user rules; hand identities and draw order remain hidden.",
 layout=new{obs=2560,candidates=2048,mask=64},exactCompositionAlias=changes.Length==0,compositionDifferenceIndices=changes,
 masteryDifferenceIndices=Enumerable.Range(0,x.Length).Where(i=>x[i]!=z[i]).ToArray(),
 cases=new[]{new{name="enemy_undergrowth_mastery5",enemyDeck=Engine(a).State.Players[1-Engine(a).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=x},new{name="enemy_order_mastery5",enemyDeck=Engine(b).State.Players[1-Engine(b).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=y},new{name="enemy_undergrowth_mastery20",enemyDeck=Engine(c).State.Players[1-Engine(c).State.TurnPlayerIndex].Deck.Select(q=>q.DefId).ToArray(),flat=z}}};
File.WriteAllText("Tools/TrainingPreflight/results/reroll-composition-alias-v8.json",JsonSerializer.Serialize(output));Console.WriteLine(JsonSerializer.Serialize(new {output.exactCompositionAlias,output.compositionDifferenceIndices,output.masteryDifferenceIndices}));
