using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;
using Pascension.Engine.Actions;

const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
var assembly = typeof(ShardsEngine).Assembly;
var adapterType = assembly.GetType("Shards.Preflight.Adapter");
var testsType = assembly.GetType("Shards.Preflight.SelfTest");
ShardsContentRegistry.EnsureRegistered();
assembly.GetType("Shards.Preflight.Encoder").GetMethod("Initialize", StaticPrivate).Invoke(null, null);
object Make(bool fastOrder)
{
    var game = testsType.GetMethod("AfterDraft", StaticPrivate).Invoke(null, new object[] { (ulong)78213 });
    var engine = (ShardsEngine)adapterType.GetField("Engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(game);
    var state = engine.State;
    var player = state.Players[state.TurnPlayerIndex];
    var starter = player.Hand.Concat(player.Deck).Concat(player.Discard).ToList();
    player.Hand.Clear(); player.Deck.Clear(); player.Discard.Clear();
    foreach (var card in starter) { card.Zone = ShardsZone.Banished; state.Banished.Add(card); }
    foreach (var card in starter.Where(c => c.DefId == "crystal").Take(3))
    { state.Banished.Remove(card); card.Zone = ShardsZone.Hand; player.Hand.Add(card); }
    ShardsCard Take(string definition, ShardsZone zone, List<ShardsCard> destination)
    {
        var card = state.CenterDeck.First(c => c.DefId == definition);
        state.CenterDeck.Remove(card); card.Owner = player.Index; card.Zone = zone;
        destination?.Add(card); return card;
    }
    var x = Take("shard_abstractor", fastOrder ? ShardsZone.Deck : ShardsZone.Hand, fastOrder ? player.Deck : player.Hand);
    var y = Take("fungal_hermit", fastOrder ? ShardsZone.Hand : ShardsZone.Deck, fastOrder ? player.Hand : player.Deck);
    Take("mainframe_abbot_duel", ShardsZone.Hand, player.Hand);
    Take("data_heretic_duel", ShardsZone.Discard, player.Discard);
    var temporary = Take(fastOrder ? x.DefId : y.DefId, ShardsZone.CenterRow, null);
    temporary.Owner = -1;
    var restoreRow = state.CenterRow[0]; restoreRow.Zone = ShardsZone.CenterDeck;
    state.CenterDeck.Add(restoreRow); state.CenterRow[0] = temporary;
    player.Mastery = 5; player.Gems = 0; player.Power = 0;
    player.ResetTurn();
    testsType.GetMethod("RefreshFixture", StaticPrivate).Invoke(null, new[] { game });
    void Submit(PlayerAction action)
    {
        if (!engine.LegalActions(player.Index).Any(a => a.Describe() == action.Describe()))
            throw new Exception("Not advertised legal: " + action.Describe());
        var result = engine.Submit(action);
        if (!result.Accepted || engine.PendingInput.Decision != null)
            throw new Exception("Rejected or unexpectedly staged: " + result.Error);
    }
    foreach (var crystal in player.Hand.Where(c => c.DefId == "crystal").ToList())
        Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = crystal.InstanceId });
    if (fastOrder)
    {
        Submit(new ShardsBuyCardAction { PlayerIndex = player.Index, SlotIndex = 0, FastPlay = true });
        Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = y.InstanceId });
    }
    else
    {
        Submit(new ShardsPlayCardAction { PlayerIndex = player.Index, CardInstanceId = x.InstanceId });
        Submit(new ShardsBuyCardAction { PlayerIndex = player.Index, SlotIndex = 0, FastPlay = true });
    }
    testsType.GetMethod("RefreshFixture", StaticPrivate).Invoke(null, new[] { game });
    return game;
}
var ga = Make(true); var gb = Make(false);
float[] Encode(object game) => (float[])testsType.GetMethod("Encode", StaticPrivate).Invoke(null, new[] { game });
ShardsEngine Engine(object game) => (ShardsEngine)adapterType.GetField("Engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(game);
var a = Encode(ga); var b = Encode(gb);
var differences = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
object Describe(object game)
{
    var e = Engine(game); var p = e.State.TurnPlayer;
    var count = AllegianceEffect.OwnedCount(p, ShardsFaction.Order);
    var record = new { hero = p.CharacterId, masteryBefore = p.Mastery, gems = p.Gems,
      orderAllegianceCount = count,
      deck = p.Deck.Select(c => c.DefId).ToArray(), hand = p.Hand.Select(c => c.DefId).ToArray(),
      played = p.PlayZone.Select(c => new { card = c.DefId, temporary = c.FastPlayed }).ToArray(),
      ownFactionPlays = Enumerable.Range(0,7).Select(i=>p.FactionPlays((ShardsFaction)i)).ToArray(),
      ownAllyPlays = Enumerable.Range(0,7).Select(i=>p.FactionAllyPlays((ShardsFaction)i)).ToArray(),
      visibleLegal = e.LegalActions(p.Index).Select(x=>x.GetType().Name).ToArray() };
    var abbot = p.Hand.Single(c => c.DefId == "mainframe_abbot_duel");
    var result=e.Submit(new ShardsPlayCardAction { PlayerIndex=p.Index, CardInstanceId=abbot.InstanceId });
    if (!result.Accepted) throw new Exception(result.Error);
    return new { before = record, mainframeAbbotAccepted = result.Accepted, masteryAfter = p.Mastery };
}
var report = new { schema="shards-representation-alias-fixture-v1", liveAssemblyReadOnly=true,
    scope="Constructed card-conserving starting fixtures, then five advertised legal Submit actions per case; not a complete seed-to-position reachability proof.",
    prefixA=new[]{"play Crystal x3", "fast-buy Shard Abstractor", "play Fungal Hermit"},
    prefixB=new[]{"play Crystal x3", "play Shard Abstractor", "fast-buy Fungal Hermit"},
    floatsCompared=a.Length, observationAndCandidatesAndMaskExactlyEqual=differences.Length==0,
    differences, caseA=Describe(ga), caseB=Describe(gb),
    hostSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() };
Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
if (differences.Length != 0) Environment.Exit(1);
