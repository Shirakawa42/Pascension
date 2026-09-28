using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.AI
{
    // Additional memory is built from public events, never hidden zone identities.
    // Draw events are consumed for their public player index only; DefId is ignored.
    internal sealed class SupplementKnowledge
    {
        internal const int TopCapacity = 4;
        private readonly List<string>[] _top = { new(), new() };
        // Lower bounds on public hand contents, not identities read from hidden
        // hands. Repeated reveals take maximum multiplicities rather than add.
        private readonly List<string>[] _hand = { new(), new() };
        private readonly List<string>[] _drawnThisSubmit = { new(), new() };
        private readonly int[][] _acquired = { new int[192], new int[192] };
        private ShardsCardsRevealedEvent _lastReveal;
        private ShardsCardsRevealedEvent _fabricatorReveal;
        private bool _expectFabricatorReveal;
        private readonly int[] _expectedDeckCount = { -1, -1 };
        internal IReadOnlyList<string> Top(int seat) => _top[seat];
        internal IReadOnlyList<string> Hand(int seat) => _hand[seat];
        internal int HandInvalidations { get; private set; }
        internal int[] Acquired(int seat) => _acquired[seat];

        internal void BeforeSubmit(int deck0, int deck1, DecisionRequest request, PlayerAction action)
        {
            // A later decision must not interpret an earlier unrelated reveal.
            _lastReveal = null;
            _fabricatorReveal = null;
            _expectFabricatorReveal = false;
            _expectedDeckCount[0] = deck0; _expectedDeckCount[1] = deck1;
            _drawnThisSubmit[0].Clear(); _drawnThisSubmit[1].Clear();
            // Called only after acceptance. The selected discarded cards are
            // now public; never inspect the unselected private menu options.
            if(action is SubmitDecisionAction discard && request?.Context == "soi.discard")
                foreach(int id in discard.Answer.ChosenOptionIds)
                {
                    var selected=request.Options.Find(o=>o.Id==id);
                    if(selected?.DefId!=null)_hand[request.PlayerIndex].Remove(selected.DefId);
                }
            // A copy selection explicitly names the next effect. Other reveals
            // (including hand Unify) are never treated as deck-top knowledge.
            if (action is SubmitDecisionAction answer && request?.Context == "soi.copy" &&
                answer.Answer.ChosenOptionIds.Count == 1)
            {
                int selected = answer.Answer.ChosenOptionIds[0];
                var option = request.Options.Find(o => o.Id == selected);
                _expectFabricatorReveal = IsFabricator(option?.DefId);
            }
        }

        internal void AfterSubmit(ShardsEngine engine, int logStart)
        {
            for (int i = logStart; i < engine.Log.Count; i++)
            {
                switch (engine.Log[i])
                {
                    case ShardsDeckShuffledEvent shuffled:
                        _top[shuffled.PlayerIndex].Clear();
                        _expectedDeckCount[shuffled.PlayerIndex] = -1;
                        _fabricatorReveal = null;
                        break;
                    case ShardsCardDrawnEvent drawn:
                        // Infer identity ONLY from an already public deck top.
                        // The private Drawn.DefId and InstanceId are never read.
                        string known=_top[drawn.PlayerIndex].Count>0?_top[drawn.PlayerIndex][0]:null;
                        _drawnThisSubmit[drawn.PlayerIndex].Add(known);
                        if(known!=null){_hand[drawn.PlayerIndex].Add(known);_top[drawn.PlayerIndex].RemoveAt(0);}
                        if (_expectedDeckCount[drawn.PlayerIndex] >= 0) _expectedDeckCount[drawn.PlayerIndex]--;
                        _fabricatorReveal = null;
                        break;
                    case ShardsCardReturnedEvent returned:
                        if (returned.FromDeck) _top[returned.PlayerIndex].Clear();
                        if (returned.FromDeck && _expectedDeckCount[returned.PlayerIndex] >= 0) _expectedDeckCount[returned.PlayerIndex]--;
                        if (returned.ToDeckTop)
                        {
                            var top = _top[returned.PlayerIndex];
                            top.Insert(0, returned.DefId);
                            if (top.Count > TopCapacity) top.RemoveAt(TopCapacity);
                            if (_expectedDeckCount[returned.PlayerIndex] >= 0) _expectedDeckCount[returned.PlayerIndex]++;
                        }
                        else _hand[returned.PlayerIndex].Add(returned.DefId);
                        _fabricatorReveal = null;
                        break;
                    case ShardsCardPlayedEvent played:
                        _hand[played.PlayerIndex].Remove(played.DefId);
                        if (IsFabricator(played.DefId)) _expectFabricatorReveal = true;
                        break;
                    case ShardsCardBanishedEvent banished when banished.FromHand:
                        _hand[banished.PlayerIndex].Remove(banished.DefId);
                        break;
                    case ShardsShieldsRevealedEvent shields:
                        RevealHand(shields.PlayerIndex,shields.DefIds);
                        break;
                    case ShardsCleanupEvent cleanup:
                        // Cleanup is emitted AFTER discard and redraw. Preserve
                        // only facts inferred for the freshly drawn hand, never
                        // a shield/return reveal from the discarded old hand.
                        _hand[cleanup.PlayerIndex].Clear();
                        var draws=_drawnThisSubmit[cleanup.PlayerIndex];
                        if(cleanup.RedrawCount>=0&&cleanup.RedrawCount<=draws.Count)
                            foreach(string id in draws.Skip(draws.Count-cleanup.RedrawCount))
                                if(id!=null)_hand[cleanup.PlayerIndex].Add(id);
                        break;
                    case ShardsCardsRevealedEvent reveal:
                        if(reveal.FromHand)RevealHand(reveal.PlayerIndex,reveal.DefIds);
                        _lastReveal = reveal;
                        if (_expectFabricatorReveal) { _fabricatorReveal = reveal; _expectFabricatorReveal = false; }
                        break;
                    case ShardsCardBoughtEvent bought when !bought.FastPlay:
                        AddAcquisition(bought.PlayerIndex, bought.DefId);
                        break;
                    case ShardsRelicRecruitedEvent relic:
                        AddAcquisition(relic.PlayerIndex, relic.DefId);
                        break;
                }
            }
            // Gatekeeper and Legion Carrier remove personal deck cards without
            // emitting Drawn events. Public size changes invalidate stale tops.
            for (int seat = 0; seat < 2; seat++)
            {
                if (_expectedDeckCount[seat] >= 0 && _expectedDeckCount[seat] != engine.State.Players[seat].Deck.Count)
                    _top[seat].Clear();
                // Public sizes can detect an unreported movement. Forget rather
                // than guessing which known card remains in a private hand.
                if(_hand[seat].Count>engine.State.Players[seat].Hand.Count)
                {_hand[seat].Clear();HandInvalidations++;}
            }
            if (_fabricatorReveal != null) CaptureFabricatorReveal(engine, _fabricatorReveal);
        }

        private static bool IsFabricator(string id) => id == "duplication_fabricator_duel" || id == "duplication_fabricator";

        private void RevealHand(int seat,IEnumerable<string> ids)
        {
            foreach(var group in ids.GroupBy(id=>id))
                for(int missing=group.Count()-_hand[seat].Count(id=>id==group.Key);missing>0;missing--)
                    _hand[seat].Add(group.Key);
        }

        private void AddAcquisition(int player, string id)
        {
            int card = Encoder.CardIndex(id);
            if (player >= 0 && player < 2 && card >= 0) _acquired[player][card]++;
        }

        internal void ObserveDecision(ShardsEngine engine, int actor, DecisionRequest request)
        {
            // These exact public titles are emitted only by FabricatorDuel after
            // publishing one top card per nonempty personal deck, in turn order.
            // We do not generalize CardsRevealed events: Unify reveals hand cards.
            if (_lastReveal == null || request.Context != "soi.copy" ||
                (request.Title != "Copy the effect of a revealed ally" &&
                 request.Title != "Copy the effects of any revealed allies")) return;
            CaptureFabricatorReveal(engine, _lastReveal);
            _lastReveal = null;
        }

        private void CaptureFabricatorReveal(ShardsEngine engine, ShardsCardsRevealedEvent reveal)
        {
            var seats = new List<int>();
            for (int step = 0; step < engine.State.Players.Count; step++)
            {
                var p = engine.State.Players[(engine.State.TurnPlayerIndex + step) % engine.State.Players.Count];
                if (!p.Eliminated && p.Deck.Count > 0) seats.Add(p.Index);
            }
            if (seats.Count != reveal.DefIds.Count) return;
            for (int i = 0; i < seats.Count; i++)
            {
                var top = _top[seats[i]];
                string id = reveal.DefIds[i];
                // A conflicting reveal invalidates older inferred tails.
                if (top.Count > 0 && top[0] != id) top.Clear();
                if (top.Count == 0) top.Add(id);
            }
        }

        private static readonly FieldInfo ActiveContext = typeof(ShardsEngine).GetField(
            "_activeContext", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Engine effect-context contract changed");

        internal static ShardsCard AuthorizedSource(Adapter game)
        {
            if (game.Decision == null || ActiveContext.GetValue(game.Engine) is not ShardsContext ctx ||
                ctx.Source == null) return null;
            var card = ctx.Source;
            var state = game.Engine.State;
            // Membership, not merely owner/zone labels, establishes authorization.
            var own = state.Players[game.Actor];
            if (own.Hand.Contains(card) || own.SetAside.Contains(card)) return card;
            foreach (var p in state.Players)
                if (p.PlayZone.Contains(card) || p.Champions.Contains(card) ||
                    p.Destinies.Contains(card) || p.Discard.Contains(card)) return card;
            if (state.CenterRow.Contains(card) || state.ActiveMonsters.Contains(card) ||
                state.DestinyRow.Contains(card) || state.Banished.Contains(card)) return card;
            return null;
        }
    }
}
