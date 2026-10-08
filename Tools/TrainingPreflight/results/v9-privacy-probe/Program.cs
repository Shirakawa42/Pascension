using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Shards.Content;
using Shards.Engine;
using Pascension.Engine.Decisions;

const BindingFlags SI=BindingFlags.Static|BindingFlags.NonPublic, II=BindingFlags.Instance|BindingFlags.NonPublic;
var asm=typeof(ShardsEngine).Assembly;
var at=asm.GetType("Shards.Preflight.Adapter");
var st=asm.GetType("Shards.Preflight.SelfTest");
var encode=st.GetMethod("Encode",SI);
var engineField=at.GetField("Engine",II);
var actorProperty=at.GetProperty("Actor",II);
var decisionProperty=at.GetProperty("Decision",II);
var truncatedProperty=at.GetProperty("Truncated",II);
var exercise=at.GetMethod("ExerciseChoice",II);
var step=at.GetMethod("Step",II);
var visible=at.GetMethod("Visible",II);
var visibleCount=at.GetProperty("VisibleCount",II);
var candidateType=asm.GetType("Shards.Preflight.Candidate");
var candidateKind=candidateType.GetField("Kind",II);
var candidateOption=candidateType.GetField("Option",II);
var clock=Stopwatch.StartNew();
ShardsContentRegistry.EnsureRegistered();
asm.GetType("Shards.Preflight.Encoder").GetMethod("Initialize",SI).Invoke(null,null);
var counts=new SortedDictionary<string,int>();
var seats=new int[2];
var contextsWithChanges=new SortedDictionary<string,int>();
int tested=0,states=0,completed=0,censored=0,changedMemberships=0,changedOrders=0,changedKnownOwnTop=0,legalMenusCompared=0;
int minRound=int.MaxValue,maxRound=0;
var actionRng=new Random(926838);
int games=args.Length>0?int.Parse(args[0]):64;
int interval=args.Length>1?int.Parse(args[1]):2;
object game=null;
try
{
    for(int episode=0;episode<games;episode++)
    {
        game=Activator.CreateInstance(at,II,null,new object[]{0x7D00000000000000UL+(ulong)episode},null);
        var engine=(ShardsEngine)engineField.GetValue(game);
        int steps=0;
        while(!engine.State.GameOver && !(bool)truncatedProperty.GetValue(game))
        {
            states++; steps++;
            int actor=(int)actorProperty.GetValue(game);
            var request=(DecisionRequest)decisionProperty.GetValue(game);
            string context=request?.Context??"priority";
            if(states%interval==0 || context!="priority")
            {
                var before=(float[])encode.Invoke(null,new[]{game});
                var legalBefore=context=="priority"?engine.LegalActions(actor).Select(a=>a.Describe()).ToArray():null;
                var own=engine.State.Players[actor];
                var enemy=engine.State.Players[1-actor];
                // Public full composition is preserved exactly; only private
                // allocation and order change. Captured options and knowledge
                // are not modified, and no card definition is ever changed.
                var savedHand=enemy.Hand.ToArray();
                var ownOrder=own.Deck.ToArray();var enemyOrder=enemy.Deck.ToArray();var centerOrder=engine.State.CenterDeck.ToArray();
                var pooled=savedHand.Concat(enemyOrder).ToArray();
                // Deterministic rotation exchanges actual hand/draw membership
                // without consuming the exercise policy's RNG stream.
                if(pooled.Length>1)
                {
                    int shift=1+(tested%(pooled.Length-1));
                    pooled=pooled.Skip(shift).Concat(pooled.Take(shift)).ToArray();
                }
                enemy.Hand.Clear();enemy.Hand.AddRange(pooled.Take(savedHand.Length));
                enemy.Deck.Clear();enemy.Deck.AddRange(pooled.Skip(savedHand.Length));
                int changes=enemy.Hand.Count(c=>!savedHand.Contains(c));
                changedMemberships+=changes;
                foreach(var card in enemy.Hand)card.Zone=ShardsZone.Hand;
                foreach(var card in enemy.Deck)card.Zone=ShardsZone.Deck;
                ulong rngState=engine.State.Rng.State,rngInc=engine.State.Rng.Inc;
                own.Deck.Reverse();enemy.Deck.Reverse();engine.State.CenterDeck.Reverse();
                if(own.Deck.Count>1||enemy.Deck.Count>1||engine.State.CenterDeck.Count>1)changedOrders++;
                if(before[2048+49]>0 && own.Deck.Count>1)changedKnownOwnTop++;
                engine.State.Rng.State^=0xD1B54A32D192ED03UL;
                engine.State.Rng.Inc^=0x94D049BB133111EAUL; // remains odd
                var after=(float[])encode.Invoke(null,new[]{game});
                bool legalEqual=legalBefore==null||legalBefore.SequenceEqual(engine.LegalActions(actor).Select(a=>a.Describe()));
                if(legalBefore!=null)legalMenusCompared++;
                // Restore every input before either reporting failure or advancing
                // the actual game; perturbations are never submitted as game state.
                enemy.Hand.Clear();enemy.Hand.AddRange(savedHand);
                own.Deck.Clear();own.Deck.AddRange(ownOrder);
                enemy.Deck.Clear();enemy.Deck.AddRange(enemyOrder);
                foreach(var card in enemy.Hand)card.Zone=ShardsZone.Hand;
                foreach(var card in enemy.Deck)card.Zone=ShardsZone.Deck;
                engine.State.CenterDeck.Clear();engine.State.CenterDeck.AddRange(centerOrder);
                engine.State.Rng.State=rngState;engine.State.Rng.Inc=rngInc;
                var different=Enumerable.Range(0,before.Length).Where(i=>before[i]!=after[i]).Take(50).ToArray();
                if(different.Length>0||!legalEqual)
                {
                    var failure=new{passed=false,episode,steps,context,actor,round=engine.State.Round,different,legalEqual,
                        before=different.Select(i=>before[i]),after=different.Select(i=>after[i]),tested};
                    Console.WriteLine(JsonSerializer.Serialize(failure,new JsonSerializerOptions{WriteIndented=true}));
                    Environment.Exit(1);
                }
                tested++;seats[actor]++;
                counts[context]=counts.GetValueOrDefault(context)+1;
                if(changes>0)contextsWithChanges[context]=contextsWithChanges.GetValueOrDefault(context)+1;
                minRound=Math.Min(minRound,engine.State.Round);maxRound=Math.Max(maxRound,engine.State.Round);
            }
            int choice=(int)exercise.Invoke(game,new object[]{actionRng});
            // Bounded exploration among legal non-concede actions increases
            // decision coverage while retaining the fast completion heuristic.
            if(actionRng.Next(10)==0 && context=="priority")
            {
                var options=new List<int>();
                for(int i=0;i<(int)visibleCount.GetValue(game);i++)
                {
                    var c=visible.Invoke(game,new object[]{i});
                    int kind=(int)candidateKind.GetValue(c);
                    if(kind!=11 && kind!=14)options.Add(i);
                }
                if(options.Count>0)choice=options[actionRng.Next(options.Count)];
            }
            step.Invoke(game,new object[]{choice});
        }
        if(engine.State.GameOver)completed++;else censored++;
    }
}
catch(Exception e)
{
    Console.WriteLine(JsonSerializer.Serialize(new{passed=false,tested,states,exception=e.ToString()},new JsonSerializerOptions{WriteIndented=true}));
    Environment.Exit(1);
}
Console.WriteLine(JsonSerializer.Serialize(new{
    schema="shards-v9-public-composition-private-allocation-probe-v1",passed=true,games,completed,censored,tested,states,seats,
    layout=new{observation=2816,candidates=2048,mask=64,total=4928},contexts=counts,contextsWithPrivateHandDrawMembershipChanges=contextsWithChanges,
    changedHandDrawMemberships=changedMemberships,changedDeckOrders=changedOrders,knownOwnTopMemoryOrderPerturbations=changedKnownOwnTop,legalMenusCompared,
    minRound,maxRound,seedNamespace="0x7D00000000000000",samplingSeed=926838,interval,seconds=clock.Elapsed.TotalSeconds,
    hostSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asm.Location))).ToLowerInvariant(),
    scope="Reached states from accepted exercise-policy games; public full composition fixed; artificial hidden allocation/order/RNG counterfactual perturbations restored before every submitted action; known memory and captured option definitions unchanged; not formal exhaustive privacy proof"
},new JsonSerializerOptions{WriteIndented=true}));
