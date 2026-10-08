#if SHARDS_NATIVE_STATS
using Adapter = Shards.AI.Adapter;
using Encoder = Shards.AI.Encoder;
#elif SHARDS_ZERO_DEPTH_STATS
using Adapter = Shards.ZeroDepth.Adapter;
using Encoder = Shards.ZeroDepth.Encoder;
#endif
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Shards.Engine;
using Pascension.Engine.Events;

namespace Shards.Preflight
{
    // Passive evaluation-only evidence. No data from here is consumed by inference.
    internal sealed class StrategyTrace : IDisposable
    {
        internal sealed class Player
        {
            public string hero, relic, destiny;
            public int seat, mastery, health, starter_banishes, fastplays, champions, champion_activations,
                monsters, rerolls, healed, peak_power, mastery10_round, mastery20_round, mastery30_round;
            public int turns, gems_gained, gems_paid_for_cards, gems_left_at_cleanup, cleanups,
                damage_dealt, health_lost, shields_prevented, hero_abilities, focuses, cards_drawn,
                champions_destroyed, mastery5_round, infinity_activations, relic_round, destiny_round;
            public Dictionary<string,int> acquired = new(), played = new(), activated = new(),
                fast_acquired = new(), deployed = new(), banished = new(), rerolled = new(), modes = new();
            public Dictionary<string,int> first_acquired_round = new(), first_played_round = new();
            public string[] extra_destinies;
            public Dictionary<string,int> collection;
            internal int Gems;
            internal readonly Dictionary<int,string> ExhaustibleIds = new();
            internal readonly HashSet<int> StarterBanishIds = new();
            internal readonly Dictionary<int, string> ChampionIds = new();
        }
        private sealed class Lane
        {
            internal Player[] Players = { new(), new() };
            internal int NormalSeat = -1, ModeSeat = -1;
            internal string VolosMode;
            internal bool NormalDestiny, NormalRelic, Initialized, CometSeen;
            internal readonly VictoryEvidence Victory = new();
        }
        private Lane[] _lanes;
        private readonly StreamWriter _output;
        private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
        internal StrategyTrace(string directory)
        {
            Directory.CreateDirectory(directory);
            _output = new StreamWriter(new FileStream(Path.Combine(directory,"strategy-games.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),System.Text.Encoding.UTF8,1024*1024);
        }
        internal void Reset(int count) => _lanes = Enumerable.Range(0,count).Select(_=>new Lane()).ToArray();
        internal void Begin(int lane, Adapter game, int choice)
        {
            var l=_lanes[lane];
            if(!l.Initialized){l.Initialized=true;Observe(lane,game.Engine,0,1);}
            var candidate=game.Visible(choice);var action=candidate.Action;
            l.VolosMode=game.Decision?.Context=="soi.volos" && candidate.Kind==12 ? "volos|"+candidate.Option.Label : null;
            l.ModeSeat=game.Actor;
            l.NormalSeat=action?.PlayerIndex ?? -1;
            l.NormalDestiny=action is ShardsTakeDestinyAction;
            l.NormalRelic=action is ShardsRecruitRelicAction;
        }
        private static void Increment(Dictionary<string,int> values,string key,int amount=1)
        { if(key!=null)values[key]=values.TryGetValue(key,out var n)?n+amount:amount; }
        private static void Acquired(Player p,string id,int round)
        { Increment(p.acquired,id);if(!p.first_acquired_round.ContainsKey(id))p.first_acquired_round[id]=round; }
        internal void Observe(int lane, ShardsEngine engine, int logStart, int round)
        {
            var l=_lanes[lane];
            for(int i=logStart;i<engine.Log.Count;i++)
            {
                l.Victory.Observe(engine.Log[i]);
                switch(engine.Log[i])
                {
                    case DecisionMadeEvent e:
                        if(l.VolosMode!=null && e.PlayerIndex==l.ModeSeat){Increment(l.Players[e.PlayerIndex].modes,l.VolosMode);l.VolosMode=null;}break;
                    case ShardsTurnStartedEvent e:round=e.Round;l.Players[e.PlayerIndex].turns++;break;
                    case ShardsRowRefilledEvent e:if(e.DefId=="comet")l.CometSeen=true;break;
                    case ShardsCardPlayedEvent e:
                        var played=l.Players[e.PlayerIndex];Increment(played.played,e.DefId);
                        if(!played.first_played_round.ContainsKey(e.DefId))played.first_played_round[e.DefId]=round;break;
                    case ShardsCardDrawnEvent e:l.Players[e.PlayerIndex].cards_drawn++;break;
                    case ShardsHeroAbilityUsedEvent e:l.Players[e.PlayerIndex].hero_abilities++;break;
                    case ShardsFocusedEvent e:l.Players[e.PlayerIndex].focuses++;break;
                    case ShardsModeChosenEvent e:Increment(l.Players[e.PlayerIndex].modes,e.DefId+"|"+e.Label);break;
                    case ShardsGemsChangedEvent e:
                        var gems=l.Players[e.PlayerIndex];gems.Gems=e.NewValue;if(e.Delta>0)gems.gems_gained+=e.Delta;break;
                    case ShardsCleanupEvent e:
                        var cleaned=l.Players[e.PlayerIndex];cleaned.cleanups++;cleaned.gems_left_at_cleanup+=cleaned.Gems;cleaned.Gems=0;break;
                    case ShardsShieldsRevealedEvent e:l.Players[e.PlayerIndex].shields_prevented+=e.Prevented;break;
                    case ShardsDamageAssignedEvent e:
                        // Ordinary post-shield damage only; exclude Infinity's artificial 9999 burst.
                        if(e.FromPlayerIndex>=0 && !l.Victory.InfinityPower(e.FromPlayerIndex))
                            l.Players[e.FromPlayerIndex].damage_dealt+=e.Amounts.Sum();break;
                    case ShardsChampionDestroyedEvent e:
                        if(e.ByPlayerIndex>=0)l.Players[e.ByPlayerIndex].champions_destroyed++;break;
                    case ShardsRelicRecruitedEvent e:
                        Acquired(l.Players[e.PlayerIndex],e.DefId,round);
                        if(l.NormalRelic && e.PlayerIndex==l.NormalSeat){l.Players[e.PlayerIndex].relic=e.DefId;l.Players[e.PlayerIndex].relic_round=round;l.NormalRelic=false;}break;
                    case ShardsDestinyTakenEvent e:
                        Acquired(l.Players[e.PlayerIndex],e.DefId,round);l.Players[e.PlayerIndex].ExhaustibleIds[e.InstanceId]=e.DefId;
                        if(l.NormalDestiny && e.PlayerIndex==l.NormalSeat){l.Players[e.PlayerIndex].destiny=e.DefId;l.Players[e.PlayerIndex].destiny_round=round;l.NormalDestiny=false;}break;
                    case ShardsCardBoughtEvent e:
                        var bought=l.Players[e.PlayerIndex];Acquired(bought,e.DefId,round);bought.gems_paid_for_cards+=e.CostPaid;
                        if(e.FastPlay){bought.fastplays++;Increment(bought.fast_acquired,e.DefId);}break;
                    case ShardsChampionDeployedEvent e:Increment(l.Players[e.PlayerIndex].deployed,e.DefId);l.Players[e.PlayerIndex].champions++;l.Players[e.PlayerIndex].ChampionIds[e.InstanceId]=e.DefId;l.Players[e.PlayerIndex].ExhaustibleIds[e.InstanceId]=e.DefId;break;
                    case ShardsCharacterExhaustedEvent e:
                        if(l.Players[e.PlayerIndex].ChampionIds.ContainsKey(e.CardInstanceId))l.Players[e.PlayerIndex].champion_activations++;
                        if(l.Players[e.PlayerIndex].ExhaustibleIds.TryGetValue(e.CardInstanceId,out var activatedId))Increment(l.Players[e.PlayerIndex].activated,activatedId);break;
                    case ShardsMonsterDefeatedEvent e:l.Players[e.PlayerIndex].monsters++;break;
                    case ShardsRowRerolledEvent e:l.Players[e.PlayerIndex].rerolls++;Increment(l.Players[e.PlayerIndex].rerolled,e.DefId);break;
                    case ShardsCardBanishedEvent e:
                        if(e.PlayerIndex>=0 && e.PlayerIndex<2)Increment(l.Players[e.PlayerIndex].banished,e.DefId);
                        if(e.PlayerIndex>=0 && e.PlayerIndex<2 && ShardsCardDatabase.Get(e.DefId).Type==ShardsCardType.Starter && l.Players[e.PlayerIndex].StarterBanishIds.Add(e.InstanceId))
                            l.Players[e.PlayerIndex].starter_banishes++;break;
                    case ShardsHealthChangedEvent e:
                        if(e.Delta>0)l.Players[e.PlayerIndex].healed+=e.Delta;
                        else l.Players[e.PlayerIndex].health_lost+=Math.Min(-e.Delta,Math.Max(0,e.NewValue-e.Delta));break;
                    case ShardsPowerChangedEvent e:if(e.Delta==9994)l.Players[e.PlayerIndex].infinity_activations++;l.Players[e.PlayerIndex].peak_power=Math.Max(l.Players[e.PlayerIndex].peak_power,e.NewValue);break;
                    case ShardsMasteryChangedEvent e:
                        var p=l.Players[e.PlayerIndex];
                        if(e.NewValue>=5 && p.mastery5_round==0)p.mastery5_round=round;
                        if(e.NewValue>=10 && p.mastery10_round==0)p.mastery10_round=round;
                        if(e.NewValue>=20 && p.mastery20_round==0)p.mastery20_round=round;
                        if(e.NewValue>=30 && p.mastery30_round==0)p.mastery30_round=round;break;
                }
            }
        }
        internal void Finish(int lane, ShardsState state, bool terminal, ulong seed)
        {
            if(terminal)
            {
                var players=_lanes[lane].Players;
                for(int seat=0;seat<2;seat++)
                {
                    var s=state.Players[seat];var p=players[seat];p.seat=seat;p.hero=s.CharacterId;p.mastery=s.Mastery;p.health=s.Health;
                    p.extra_destinies=s.Destinies.Select(c=>c.DefId).Where(id=>id!=p.destiny).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
                    // Zone-blind permanent collection, excluding temporary loans and transient cards due to be banished.
                    p.collection=s.Deck.Concat(s.Hand).Concat(s.Discard).Concat(s.Champions).Concat(s.PlayZone.Where(c=>!c.FastPlayed && !c.BanishAtCleanup))
                        .GroupBy(c=>c.DefId).OrderBy(g=>g.Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count());
                }
                _output.WriteLine(JsonSerializer.Serialize(new {schema=2,seed=seed.ToString("x16"),winner=state.WinnerIndex,victory=_lanes[lane].Victory.Result(state.WinnerIndex),comet_market_seen=_lanes[lane].CometSeen,round=state.Round,players},Json));
                // Evaluation telemetry must survive an early user stop. This is
                // one buffered-file flush per completed game, never per action.
                _output.Flush();
            }
            _lanes[lane]=new Lane();
        }
        public void Dispose()=>_output.Dispose();
    }
}
