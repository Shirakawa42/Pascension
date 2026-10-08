using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;

const BindingFlags SP = BindingFlags.Static | BindingFlags.NonPublic;
const BindingFlags IP = BindingFlags.Instance | BindingFlags.NonPublic;
var assembly = typeof(ShardsEngine).Assembly;
var adapterType = assembly.GetType("Shards.Preflight.Adapter");
var testsType = assembly.GetType("Shards.Preflight.SelfTest");
ShardsContentRegistry.EnsureRegistered();
assembly.GetType("Shards.Preflight.Encoder").GetMethod("Initialize", SP).Invoke(null, null);
ShardsEngine Engine(object game) => (ShardsEngine)adapterType.GetField("Engine", IP).GetValue(game);
float[] Encode(object game) => (float[])testsType.GetMethod("Encode", SP).Invoke(null, new[] { game });
object Candidate(object game, int index) => adapterType.GetMethod("Visible", IP).Invoke(game, new object[] { index });
int Kind(object candidate) => (int)candidate.GetType().GetField("Kind", IP).GetValue(candidate);
int FirstKind(object game, int kind)
{
    int count = (int)adapterType.GetProperty("VisibleCount", IP).GetValue(game);
    return Enumerable.Range(0, count).First(i => Kind(Candidate(game, i)) == kind);
}
void Step(object game, int index) => adapterType.GetMethod("Step", IP).Invoke(game, new object[] { index });
object Make(bool reverse)
{
    var game = testsType.GetMethod("AfterDraft", SP).Invoke(null, new object[] { (ulong)78213 });
    var engine = Engine(game); var state = engine.State; var player = state.TurnPlayer;
    player.CharacterId = "rez"; player.Mastery = 5; player.Gems = 0;
    player.HeroAbilityUsedThisTurn = false; player.RerollsThisTurn = 0; player.NextRerollDiscount = 0;
    var a = state.CenterDeck.First(c => c.DefId == "shard_abstractor");
    var b = state.CenterDeck.First(c => c.DefId == "fungal_hermit");
    state.CenterDeck.Remove(a); state.CenterDeck.Remove(b);
    state.CenterDeck.Add(reverse ? a : b); state.CenterDeck.Add(reverse ? b : a);
    testsType.GetMethod("RefreshFixture", SP).Invoke(null, new[] { game });
    return game;
}
var ga = Make(false); var gb = Make(true);
var initialA = Encode(ga); var initialB = Encode(gb);
Step(ga, FirstKind(ga, 9)); Step(gb, FirstKind(gb, 9));
if (Engine(ga).PendingInput.Decision.Context != "soi.scry" || Engine(gb).PendingInput.Decision.Context != "soi.scry")
    throw new Exception("Expected real Scry requests");
var revealA = Engine(ga).PendingInput.Decision.Options.Select(o => o.DefId).ToArray();
var revealB = Engine(gb).PendingInput.Decision.Options.Select(o => o.DefId).ToArray();
var duringA = Encode(ga); var duringB = Encode(gb);
Step(ga, FirstKind(ga, 13)); Step(gb, FirstKind(gb, 13));
var afterA = Encode(ga); var afterB = Encode(gb);
int[] Diff(float[] a, float[] b) => Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
var differences = Diff(afterA, afterB);
var rerollIndexA = FirstKind(ga, 8); var rerollIndexB = FirstKind(gb, 8);
var action = Candidate(ga, rerollIndexA).GetType().GetField("Action", IP).GetValue(Candidate(ga, rerollIndexA));
var slot = (int)action.GetType().GetField("SlotIndex").GetValue(action);
var beforeCard = Engine(ga).State.CenterRow[slot].DefId;
var priceA = ShardsEngine.RerollCost(Engine(ga).State.TurnPlayer);
Step(ga, rerollIndexA); Step(gb, rerollIndexB);
var resultA = Engine(ga).State.CenterRow[slot].DefId;
var resultB = Engine(gb).State.CenterRow[slot].DefId;
var report = new {
    schema = "shards-rez-scry-memory-alias-fixture-v1",
    scope = "Constructed same-seed card-conserving fixture with Rez at Mastery 5; different center-deck top-two order, then actual legal ability, keep-all Scry, and identical free reroll through the frozen V5 Adapter. Not a full seed-to-position reachability proof.",
    liveAssemblyReadOnly = true, hostSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant(),
    initialEncodedInputsExactlyEqual = Diff(initialA, initialB).Length == 0,
    scryRevealsA = revealA, scryRevealsB = revealB,
    duringScryDifferenceCount = Diff(duringA, duringB).Length,
    floatsCompared = afterA.Length,
    postScryObservationCandidatesAndMaskExactlyEqual = differences.Length == 0,
    postScryDifferenceIndices = differences,
    sameRerollCandidateIndex = rerollIndexA == rerollIndexB,
    rerollSlot = slot, rerollPrice = priceA, replacedCard = beforeCard,
    rerollRevealedA = resultA, rerollRevealedB = resultB,
    retainedKnownTopChangesRerollResult = resultA != resultB
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (differences.Length != 0 || resultA == resultB || Diff(duringA, duringB).Length == 0 || rerollIndexA != rerollIndexB)
    Environment.Exit(1);
