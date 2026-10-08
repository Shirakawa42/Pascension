using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
const BindingFlags II=BindingFlags.Instance|BindingFlags.NonPublic, SI=BindingFlags.Static|BindingFlags.NonPublic;
var root=Path.GetFullPath("Tools/TrainingPreflight/experiments");
var versions=new[]{"V9","V10"};var assemblies=versions.Select(v=>Assembly.LoadFrom(Path.Combine(root,"Host"+v,"bin/Release/net8.0/TrainingHost"+v+".dll"))).ToArray();
foreach(var asm in assemblies){asm.GetType("Shards.Content.ShardsContentRegistry").GetMethod("EnsureRegistered").Invoke(null,null);asm.GetType("Shards.Preflight.Encoder").GetMethod("Initialize",SI).Invoke(null,null);}
if(args.Contains("--information")){assemblies[1].GetType("Shards.Preflight.InformationV10SelfTest").GetMethod("Run",SI).Invoke(null,null);return;}
var adapters=assemblies.Select(a=>a.GetType("Shards.Preflight.Adapter")).ToArray();
var encoders=assemblies.Select(a=>a.GetType("Shards.Preflight.SelfTest").GetMethod("Encode",SI)).ToArray();
object Engine(int v,object g)=>adapters[v].GetField("Engine",II).GetValue(g);
object State(int v,object g){var e=Engine(v,g);return e.GetType().GetField("State").GetValue(e);}
bool Over(int v,object g){var s=State(v,g);return(bool)s.GetType().GetField("GameOver").GetValue(s);}
var rng=new[]{new Random(928210),new Random(928210)};int states=0,completed=0,censored=0;var contexts=new SortedDictionary<string,int>();
for(int episode=0;episode<32;episode++)
{
 var games=adapters.Select(t=>Activator.CreateInstance(t,II,null,new object[]{0x7E00000000000000UL+(ulong)episode},null)).ToArray();
 while(!Over(0,games[0])&&!(bool)adapters[0].GetProperty("Truncated",II).GetValue(games[0]))
 {
  var old=(float[])encoders[0].Invoke(null,new[]{games[0]});var newer=(float[])encoders[1].Invoke(null,new[]{games[1]});
  if(!old.Take(2816).SequenceEqual(newer.Take(2816))||!old.Skip(2816).SequenceEqual(newer.Skip(3328)))throw new Exception("Changed V9 prefix/candidates at episode"+episode+" state"+states);
  var decision=adapters[0].GetProperty("Decision",II).GetValue(games[0]);
  string context=decision==null?"priority":(string)decision.GetType().GetField("Context").GetValue(decision);
  contexts[context]=contexts.GetValueOrDefault(context)+1;states++;
  int a=(int)adapters[0].GetMethod("ExerciseChoice",II).Invoke(games[0],new object[]{rng[0]});int b=(int)adapters[1].GetMethod("ExerciseChoice",II).Invoke(games[1],new object[]{rng[1]});
  if(a!=b)throw new Exception("Changed legal exercise menu");
  for(int v=0;v<2;v++)adapters[v].GetMethod("Step",II).Invoke(games[v],new object[]{a});
 }
 if(Over(0,games[0])!=Over(1,games[1]))throw new Exception("Different termination");
 if(Over(0,games[0]))completed++;else censored++;
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,games=32,completed,censored,states,contexts,
 comparison="Exact prior2816 observation floats plus all64x32candidate floats and64mask floats along shared accepted trajectories",
 hostSha256=assemblies.Select(a=>new{assembly=a.GetName().Name,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant()}),
 scope="Read-only isolated assemblies; no frozen source/binary mutation"},new JsonSerializerOptions{WriteIndented=true}));
