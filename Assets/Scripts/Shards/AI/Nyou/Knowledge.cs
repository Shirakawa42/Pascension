using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.Nyou
{
    internal sealed class KnownPosition
    {
        internal string DefId;
        internal int InstanceId, Min, Max;
        internal bool UncertainPresence;
    }
    // Facts are updated solely from accepted public movement/reveal events and
    // the deciding seat's own private Scry options. Never repair from hidden order.
    internal sealed class Knowledge
    {
        internal readonly List<KnownPosition>[] Center = { new(), new() };
        internal readonly List<KnownPosition>[] Personal = { new(), new() };
        internal readonly List<string>[] Hand = { new(), new() };
        internal readonly Dictionary<int,string>[] PublicHandIds = { new(), new() };
        internal readonly HashSet<int>[] RecruitedRelics = { new(), new() };
        internal readonly HashSet<string>[] RecruitedRelicDefs = { new(), new() };
        internal readonly List<ShardsCard> Detached = new();
        private readonly HashSet<int> _removedReveals = new();
        private int _centerCount;
        private readonly int[] _personalCount = new int[2];
        // Adapter accepts exactly two seats; a value snapshot also preserves
        // independent counts for nested accepted submissions without allocating.
        internal struct Before
        {
            internal int CenterCount, PersonalCount0, PersonalCount1;
        }
        internal void Initialize(ShardsEngine e)
        {
            _centerCount = e.State.CenterDeck.Count;
            for (int p = 0; p < 2; p++) _personalCount[p] = e.State.Players[p].Deck.Count;
        }
        internal Before BeforeSubmit(ShardsEngine e, DecisionRequest d, PlayerAction action) => new Before
        { CenterCount = e.State.CenterDeck.Count, PersonalCount0 = e.State.Players[0].Deck.Count, PersonalCount1 = e.State.Players[1].Deck.Count };
        internal void ObserveDecision(int actor, DecisionRequest d)
        {
            if (d.Context != "soi.scry" && d.Context != "soi.reorder") return;
            var facts=Center[actor];int n=d.Options.Count;
            var observed=new HashSet<int>(d.Options.Select(o=>o.CardInstanceId));
            for(int i=facts.Count-1;i>=0;i--)
            {
                var fact=facts[i];
                if(observed.Contains(fact.InstanceId))continue;
                // This seat has seen EVERY position in the prefix. An unseen
                // remembered identity cannot occupy any of those positions.
                if(fact.Max<n)
                {
                    if(!fact.UncertainPresence)throw new InvalidOperationException("Remembered center membership conflicts with private peek");
                    facts.RemoveAt(i);
                }
                else fact.Min=Math.Max(fact.Min,n);
            }
            for (int i = 0; i < d.Options.Count; i++)
                Put(facts, d.Options[i].DefId, d.Options[i].CardInstanceId, i);
        }
        internal void AfterSubmit(ShardsEngine e, int start, DecisionRequest request, PlayerAction action, Before before)
        {
            _centerCount = before.CenterCount;
            _personalCount[0] = before.PersonalCount0; _personalCount[1] = before.PersonalCount1;
            if (action is SubmitDecisionAction answer && request != null)
            {
                if (request.Context == "soi.scry" || request.Context == "soi.reorder")
                    ApplyCenterDecision(request, answer.Answer.ChosenOptionIds);
                if (request.Context == "soi.discard")
                    foreach (int id in answer.Answer.ChosenOptionIds)
                    {
                        var option=request.Options.Find(o=>o.Id==id);
                        RemoveHand(request.PlayerIndex,option?.DefId,option?.CardInstanceId??-1);
                    }
            }
            List<(string id,int instance)>[] draws = null;
            for (int i = start; i < e.Log.Count; i++)
            {
                switch (e.Log[i])
                {
                    case ShardsDeckShuffledEvent shuffle:
                        Personal[shuffle.PlayerIndex].Clear(); break;
                    case ShardsCardDrawnEvent drawn:
                        var known = Personal[drawn.PlayerIndex].Find(p => p.Min == 0 && p.Max == 0 && !p.UncertainPresence);
                        draws ??= new List<(string id,int instance)>[2];
                        (draws[drawn.PlayerIndex] ??= new()).Add((known?.DefId,known?.InstanceId??-1));
                        if (known != null) AddHand(drawn.PlayerIndex,known.DefId,known.InstanceId);
                        Pop(Personal[drawn.PlayerIndex]); _personalCount[drawn.PlayerIndex]--; break;
                    case ShardsCardsRevealedEvent reveal:
                        if (reveal.FromHand) RevealHand(reveal.PlayerIndex, reveal.DefIds,reveal.HandInstanceIds);
                        if (reveal.TakenFromCenterTop)
                        {
                            if(reveal.CenterInstanceIds==null||reveal.CenterInstanceIds.Count!=reveal.DefIds.Count)
                                throw new InvalidOperationException("Center reveal needs identity provenance");
                            for(int r=0;r<reveal.DefIds.Count;r++)
                            {
                                PopCenter(reveal.DefIds[r],reveal.CenterInstanceIds[r]);
                                RecordDetached(reveal.CenterInstanceIds[r],reveal.DefIds[r],-1,ShardsZone.CenterDeck);
                            }
                        }
                        if (reveal.PersonalTopPlayers != null)
                        {
                            if (reveal.PersonalTopPlayers.Count != reveal.DefIds.Count || reveal.PersonalTopInstanceIds?.Count != reveal.DefIds.Count)
                                throw new InvalidOperationException("Reveal provenance is not parallel");
                            for (int r = 0; r < reveal.DefIds.Count; r++)
                            {
                                int owner = reveal.PersonalTopPlayers[r], instance = reveal.PersonalTopInstanceIds[r];
                                if (reveal.RemovedFromPersonalTop)
                                {
                                    Pop(Personal[owner],reveal.DefIds[r],instance); _personalCount[owner]--; _removedReveals.Add(instance);
                                    RecordDetached(instance,reveal.DefIds[r],owner,ShardsZone.Deck);
                                }
                                else Put(Personal[owner], reveal.DefIds[r], instance, 0);
                            }
                        }
                        break;
                    case ShardsCardReturnedEvent returned:
                        Detached.RemoveAll(c=>c.InstanceId==returned.InstanceId);
                        if(ShardsCardDatabase.Get(returned.DefId).Type==ShardsCardType.Relic)
                            RecruitedRelicDefs[returned.PlayerIndex].Add(returned.DefId);
                        if (returned.FromDeck && !_removedReveals.Remove(returned.InstanceId))
                        { Remove(Personal[returned.PlayerIndex], returned.InstanceId); _personalCount[returned.PlayerIndex]--; }
                        if (returned.ToDeckTop)
                        {
                            foreach (var fact in Personal[returned.PlayerIndex]) { fact.Min++; fact.Max++; }
                            Put(Personal[returned.PlayerIndex], returned.DefId, returned.InstanceId, 0);
                            _personalCount[returned.PlayerIndex]++;
                        }
                        else AddHand(returned.PlayerIndex,returned.DefId,returned.InstanceId);
                        break;
                    case ShardsRevealedCardsDiscardedEvent discarded:
                        if(discarded.InstanceIds.Count!=discarded.DefIds.Count)
                            throw new InvalidOperationException("Revealed discard provenance is not parallel");
                        for(int d=0;d<discarded.InstanceIds.Count;d++)
                        {
                            int instance=discarded.InstanceIds[d];
                            if(instance<=0)throw new InvalidOperationException("Public reveal release needs physical identity provenance");
                            var record=Detached.Find(c=>c.InstanceId==instance);
                            if(record!=null&&(record.DefId!=discarded.DefIds[d]||record.Owner!=discarded.PlayerIndex||record.Zone!=ShardsZone.Deck))
                                throw new InvalidOperationException("Public reveal release conflicts with recorded definition/origin");
                            Detached.RemoveAll(c=>c.InstanceId==instance);
                            _removedReveals.Remove(instance);
                        }
                        break;
                    case ShardsCardPlayedEvent played: RemoveHand(played.PlayerIndex,played.DefId,played.InstanceId);Detached.RemoveAll(c=>c.InstanceId==played.InstanceId); break;
                    case ShardsCardBanishedEvent banished when banished.FromHand: RemoveHand(banished.PlayerIndex,banished.DefId,banished.InstanceId); break;
                    case ShardsShieldsRevealedEvent shields: RevealHand(shields.PlayerIndex, shields.DefIds,shields.HandInstanceIds); break;
                    case ShardsCleanupEvent cleanup:
                        Hand[cleanup.PlayerIndex].Clear();
                        PublicHandIds[cleanup.PlayerIndex].Clear();
                        var handDraws=draws?[cleanup.PlayerIndex];
                        if(handDraws!=null)
                            for(int draw=Math.Max(0,handDraws.Count-Math.Max(0,cleanup.RedrawCount));draw<handDraws.Count;draw++)
                            {
                                var knownDraw=handDraws[draw];
                                if(knownDraw.id!=null)AddHand(cleanup.PlayerIndex,knownDraw.id,knownDraw.instance);
                            }
                        break;
                    case ShardsRowRefilledEvent row: PopCenter(row.DefId,row.InstanceId); break;
                    case ShardsMonsterRevealedEvent monster: PopCenter(monster.DefId,monster.InstanceId); break;
                    case ShardsCenterCardBottomedEvent bottomed: BottomCenter(bottomed.DefId,bottomed.InstanceId); break;
                    case ShardsCenterDeckShuffledEvent _:
                        Center[0].Clear(); Center[1].Clear(); break;
                    case ShardsMercenaryReturnedEvent returned:
                        var detached=Detached.Find(c=>c.InstanceId==returned.InstanceId);
                        if(detached!=null)
                        {
                            if(detached.DefId!=returned.DefId)throw new InvalidOperationException("Public bottom return changed definition");
                            Detached.Remove(detached);
                        }
                        BottomCenter(returned.DefId,returned.InstanceId); break;
                    case ShardsMonsterDefeatedEvent defeated: BottomCenter(defeated.DefId,defeated.InstanceId); break;
                    case ShardsRelicRecruitedEvent relic: RecruitedRelicDefs[relic.PlayerIndex].Add(relic.DefId); break;
                }
            }
            _centerCount = e.State.CenterDeck.Count;
            // A removed reveal marker can outlive its temporary card record.
            // Retire either set whenever needed; empty sets need no zone scan.
            if(Detached.Count!=0||_removedReveals.Count!=0)
            {
                var publicIds=new HashSet<int>();
                foreach(var c in e.State.Banished)publicIds.Add(c.InstanceId);
                foreach(var player in e.State.Players)
                {
                    foreach(var c in player.Discard)publicIds.Add(c.InstanceId);
                    foreach(var c in player.PlayZone)publicIds.Add(c.InstanceId);
                    foreach(var c in player.Champions)publicIds.Add(c.InstanceId);
                    foreach(var c in player.Destinies)publicIds.Add(c.InstanceId);
                }
                foreach(var c in e.State.CenterRow)if(c!=null)publicIds.Add(c.InstanceId);
                foreach(var c in e.State.ActiveMonsters)publicIds.Add(c.InstanceId);
                foreach(var c in e.State.DestinyRow)publicIds.Add(c.InstanceId);
                Detached.RemoveAll(c=>publicIds.Contains(c.InstanceId));
                _removedReveals.RemoveWhere(id=>publicIds.Contains(id));
            }
            for (int p = 0; p < 2; p++)
            {
                _personalCount[p] = e.State.Players[p].Deck.Count;
                if (Hand[p].Count > e.State.Players[p].Hand.Count)
                    throw new InvalidOperationException("Public hand facts exceed hand count; unannotated hand movement");
                foreach(var group in PublicHandIds[p].Values.GroupBy(x=>x))
                    if(group.Count()>Hand[p].Count(x=>x==group.Key))
                        throw new InvalidOperationException("Public hand identities exceed known definition count");
            }
        }
        private void RecordDetached(int instance,string id,int owner,ShardsZone zone)
        {
            // A copied reveal can resolve, return these cards to discard, shuffle
            // and reveal them again within ONE Submit. Its final state no longer
            // contains the intermediate public discard. The latest public reveal
            // replaces that physical record, retaining the new reveal order.
            var previous=Detached.Find(c=>c.InstanceId==instance);
            if(previous!=null&&previous.DefId!=id)
                throw new InvalidOperationException("Repeated public reveal changed physical definition");
            Detached.RemoveAll(c=>c.InstanceId==instance);
            Detached.Add(new ShardsCard{InstanceId=instance,DefId=id,Owner=owner,Zone=zone});
        }
        private void AddHand(int owner,string id,int instance)
        {
            if(instance>=0)
            {
                if(PublicHandIds[owner].TryGetValue(instance,out string previous))
                {
                    if(previous!=id)throw new InvalidOperationException("Public hand identity changed definition");
                    return;
                }
                PublicHandIds[owner].Add(instance,id);
            }
            Hand[owner].Add(id);
        }
        private void RemoveHand(int owner,string id,int instance)
        {
            if(instance>=0&&PublicHandIds[owner].Remove(instance,out string previous))
            {
                if(previous!=id)throw new InvalidOperationException("Public hand movement changed definition");
                Hand[owner].Remove(id);return;
            }
            // A different public instance cannot have been one of the already
            // identified held copies. Only an anonymous lower-bound fact can
            // be weakened by this movement of another same-definition card.
            int identified=PublicHandIds[owner].Values.Count(x=>x==id);
            if(Hand[owner].Count(x=>x==id)>identified)Hand[owner].Remove(id);
        }
        private void PopCenter(string id,int instance=-1)
        {
            foreach (var facts in Center)
            {
                var top = facts.Find(p => p.Min == 0 && p.Max == 0 && !p.UncertainPresence);
                if (top != null && (top.DefId != id || instance>0&&top.InstanceId>=0&&top.InstanceId!=instance))
                    throw new InvalidOperationException("Known center top conflicts with public reveal");
                // Row/monster/reveal identities are PUBLIC. A remembered card
                // removed after a private reorder must be retired by identity;
                // every different remembered instance is certainly still there.
                Pop(facts,id,instance>0?instance:-1);
            }
            _centerCount--;
        }
        private void BottomCenter(string id,int instance)
        {
            if(instance<=0)throw new InvalidOperationException("Public center bottom return needs physical identity provenance");
            // Public return order is exact, even while the whole prefix is unknown.
            foreach (var facts in Center)
            {
                if(instance>=0)facts.RemoveAll(f=>f.InstanceId==instance);
                facts.Add(new KnownPosition { DefId = id, InstanceId = instance, Min = _centerCount, Max = _centerCount });
            }
            _centerCount++;
        }
        private void ApplyCenterDecision(DecisionRequest request, List<int> selected)
        {
            int actor = request.PlayerIndex, n = request.Options.Count;
            var own = Center[actor];
            var observedIds=new HashSet<int>(request.Options.Select(o=>o.CardInstanceId));
            own.RemoveAll(f => observedIds.Contains(f.InstanceId) || f.Min==f.Max&&f.Min<n);
            var tail=own.ToArray();
            if (request.Context == "soi.reorder")
            {
                for (int i = 0; i < selected.Count; i++)
                { var o = request.Options.Find(x => x.Id == selected[i]); Put(own, o.DefId, o.CardInstanceId, i); }
            }
            else
            {
                int at = 0;
                foreach (var o in request.Options)
                    if (!selected.Contains(o.Id)) Put(own, o.DefId, o.CardInstanceId, at++);
                foreach (var fact in tail) { fact.Min = Math.Max(0,fact.Min-selected.Count); fact.Max=Math.Max(0,fact.Max-selected.Count); }
                // Insert(0) means the LAST selected card becomes the new bottom.
                for (int i = 0; i < selected.Count; i++)
                { var o = request.Options.Find(x => x.Id == selected[i]); Put(own, o.DefId, o.CardInstanceId, _centerCount - selected.Count + i); }
            }
            // Other seat does not learn private selections. Preserve remembered
            // membership and possible positions, including pre-existing bottoms.
            foreach (var fact in Center[1 - actor])
            {
                if (request.Context == "soi.reorder")
                {
                    // Reordering cannot move a prefix card beyond that prefix.
                    // If an older interval also extends beyond it, retain that
                    // tail possibility without revealing the private permutation.
                    if(fact.Min<n){fact.Min=0;fact.Max=Math.Max(fact.Max,n-1);}
                }
                else if (fact.Min < n) { fact.Min = 0; fact.Max = _centerCount - 1; }
                else fact.Min = Math.Max(0, fact.Min - n);
            }
        }
        private static void Put(List<KnownPosition> facts, string id, int instance, int at)
        {
            facts.RemoveAll(f => (instance >= 0 && f.InstanceId == instance) || f.Min == at && f.Max == at);
            facts.Add(new KnownPosition { DefId = id, InstanceId = instance, Min = at, Max = at });
        }
        private static void Pop(List<KnownPosition> facts,string visibleDef=null,int visibleInstance=-1)
        {
            facts.RemoveAll(f => f.Min == 0 && f.Max == 0 ||
                visibleInstance>=0&&f.InstanceId==visibleInstance);
            foreach (var f in facts)
            {
                if (f.Min == 0 && (visibleInstance<0||f.InstanceId<0) &&
                    (visibleDef==null||f.DefId==visibleDef)) f.UncertainPresence = true;
                f.Min = Math.Max(0, f.Min - 1); f.Max = Math.Max(0, f.Max - 1);
            }
        }
        private static void Remove(List<KnownPosition> facts, int instance)
        {
            var removed = facts.Find(f => f.InstanceId == instance);
            foreach (var f in facts)
            {
                if (f == removed) continue;
                if (removed != null && removed.Max < f.Min) { f.Min--; f.Max--; }
                else if (removed == null || removed.Min < f.Max) f.Min = Math.Max(0, f.Min - 1);
            }
            if (removed != null) facts.Remove(removed);
        }
        private void RevealHand(int owner, List<string> ids,List<int> instances=null)
        {
            if(instances!=null)
            {
                if(instances.Count!=ids.Count)throw new InvalidOperationException("Hand reveal provenance is not parallel");
                for(int i=0;i<ids.Count;i++)
                {
                    if(instances[i]<=0)throw new InvalidOperationException("Public hand reveal needs physical identity provenance");
                    if(PublicHandIds[owner].TryGetValue(instances[i],out string previous)&&previous!=ids[i])
                        throw new InvalidOperationException("Publicly revealed hand identity changed definition");
                    PublicHandIds[owner][instances[i]]=ids[i];
                }
            }
            foreach (var group in ids.GroupBy(x => x))
                for (int n = Math.Max(group.Count(),PublicHandIds[owner].Values.Count(x=>x==group.Key)) - Hand[owner].Count(x => x == group.Key); n > 0; n--) Hand[owner].Add(group.Key);
        }
    }
}
