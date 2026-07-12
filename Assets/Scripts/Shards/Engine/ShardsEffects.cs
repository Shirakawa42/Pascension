using System.Collections.Generic;
using Pascension.Engine.Decisions;

namespace Shards.Engine
{
    /// <summary>
    /// Shards' effect-as-iterator (same pattern as Pascension's, typed on this game's
    /// context): yield steps; AwaitDecision pauses the engine for the given player.
    /// </summary>
    public interface IShardsEffect
    {
        IEnumerable<ShardsStep> Resolve(ShardsContext ctx);
    }

    public sealed class ShardsStep
    {
        public DecisionRequest Decision;
        public static readonly ShardsStep Done = new();
        public static ShardsStep AwaitDecision(DecisionRequest request) => new() { Decision = request };
    }

    /// <summary>Per-resolution context: the engine API, controller, and the answer to the
    /// most recent AwaitDecision.</summary>
    public sealed class ShardsContext
    {
        public ShardsEngine Engine;
        public int ControllerIndex;
        public ShardsCard Source;
        public DecisionAnswer Answer;

        public ShardsPlayer Controller => Engine.State.Players[ControllerIndex];
    }

    public sealed class ShardsNullEffect : IShardsEffect
    {
        public static readonly ShardsNullEffect Instance = new();
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx) { yield break; }
    }

    /// <summary>Run parts in order. Sequencing is rules-critical: a card's own mastery
    /// gain resolves BEFORE later threshold checks (the Fungal Hermit ruling).</summary>
    public sealed class ShardsComposite : IShardsEffect
    {
        private readonly IShardsEffect[] _parts;
        public ShardsComposite(params IShardsEffect[] parts) => _parts = parts;
        /// <summary>Read-only view for bot heuristics.</summary>
        public IReadOnlyList<IShardsEffect> Parts => _parts;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            foreach (var part in _parts)
                foreach (var step in part.Resolve(ctx))
                    yield return step;
        }
    }

    /// <summary>Mastery Threshold wrapper: the inner effect fires only if the controller's
    /// mastery is at least N AT THIS MOMENT of resolution (never retro-checked).</summary>
    public sealed class AtMastery : IShardsEffect
    {
        private readonly int _threshold;
        private readonly IShardsEffect _inner;
        public int Threshold => _threshold;
        public IShardsEffect Inner => _inner;

        public AtMastery(int threshold, IShardsEffect inner)
        {
            _threshold = threshold;
            _inner = inner;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (ctx.Controller.Mastery < _threshold) yield break;
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Faction trigger wrapper: fires only if the controller played another card
    /// of the faction this turn (fast-played mercenaries count). TODO-VERIFY exact
    /// Unify/Dominion conditions from rules-notes (M4) — condition kind is data.</summary>
    public sealed class FactionTrigger : IShardsEffect
    {
        private readonly ShardsFaction _faction;
        private readonly int _required; // cards of the faction played this turn (excluding this one)
        private readonly IShardsEffect _inner;

        public FactionTrigger(ShardsFaction faction, int required, IShardsEffect inner)
        {
            _faction = faction;
            _required = required;
            _inner = inner;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (ctx.Controller.FactionPlays(_faction) - 1 < _required) yield break; // -1: itself
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    // ---------------- primitive effects (built from lambdas for content brevity) ----------------

    public sealed class Gain : IShardsEffect
    {
        public int Gems, Power, Mastery, Health, Draw;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var e = ctx.Engine;
            int p = ctx.ControllerIndex;
            if (Gems != 0) e.GainGems(p, Gems);
            if (Power != 0) e.GainPower(p, Power);
            if (Mastery != 0) e.GainMastery(p, Mastery);
            if (Health != 0) e.GainHealth(p, Health);
            if (Draw != 0) e.DrawCards(p, Draw);
            yield break;
        }
    }

    /// <summary>Escape hatch for bespoke card logic without a dedicated class.</summary>
    public sealed class Custom : IShardsEffect
    {
        private readonly System.Func<ShardsContext, IEnumerable<ShardsStep>> _body;
        public Custom(System.Func<ShardsContext, IEnumerable<ShardsStep>> body) => _body = body;
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx) => _body(ctx);
    }

    /// <summary>Immediate side effect with no decisions (turn flags etc.).</summary>
    public sealed class Do : IShardsEffect
    {
        private readonly System.Action<ShardsContext> _body;
        public Do(System.Action<ShardsContext> body) => _body = body;
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx) { _body(ctx); yield break; }
    }

    /// <summary>"Gain N instead" mastery scaling: resolves the HIGHEST tier whose
    /// threshold the controller meets right now (Shard Reactor, Infinity Shard).</summary>
    public sealed class BestByMastery : IShardsEffect
    {
        private readonly (int threshold, IShardsEffect effect)[] _tiers;
        public BestByMastery(params (int threshold, IShardsEffect effect)[] tiers) => _tiers = tiers;
        public IReadOnlyList<(int threshold, IShardsEffect effect)> Tiers => _tiers;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            IShardsEffect best = null;
            int bestThreshold = int.MinValue;
            foreach (var (threshold, effect) in _tiers)
                if (ctx.Controller.Mastery >= threshold && threshold >= bestThreshold)
                {
                    bestThreshold = threshold;
                    best = effect;
                }
            if (best == null) yield break;
            foreach (var step in best.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Gate on a simple state predicate (character identity, full health,
    /// champions in play — Inspire, Echo, character-affinity lines).</summary>
    public sealed class If : IShardsEffect
    {
        private readonly System.Func<ShardsContext, bool> _condition;
        private readonly IShardsEffect _inner;
        public IShardsEffect Inner => _inner;
        public If(System.Func<ShardsContext, bool> condition, IShardsEffect inner)
        {
            _condition = condition;
            _inner = inner;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (!_condition(ctx)) yield break;
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }

        // Common conditions, named for content readability.
        public static If Inspire(IShardsEffect inner) =>
            new(ctx => ctx.Controller.Champions.Count > 0, inner);
        public static If Echo(IShardsEffect inner) =>
            new(ctx => ctx.Controller.Discard.Exists(c =>
                ShardsEngine.CountsAs(ctx.Controller, c.Def, ShardsFaction.Wraethe)), inner);
        public static If Character(string characterId, IShardsEffect inner) =>
            new(ctx => ctx.Controller.CharacterId == characterId, inner);
        public static If FullHealth(IShardsEffect inner) =>
            new(ctx => ctx.Controller.Health >= ctx.Engine.State.Rules.MaxHealth, inner);
    }

    /// <summary>Unify: fires if the controller played ANOTHER ally of the faction this
    /// turn, or reveals one from hand (a decision when it matters). Champions never
    /// satisfy it; fast-played mercenaries do; the card never satisfies itself.</summary>
    public sealed class Unify : IShardsEffect
    {
        private readonly ShardsFaction _faction;
        private readonly IShardsEffect _inner;
        public IShardsEffect Inner => _inner;
        public Unify(IShardsEffect inner, ShardsFaction faction = ShardsFaction.Undergrowth)
        {
            _inner = inner;
            _faction = faction;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var player = ctx.Controller;
            int plays = player.FactionAllyPlays(_faction);
            var source = ctx.Source;
            if (source != null && ShardsEngine.CountsAs(player, source.Def, _faction) &&
                !source.Def.IsChampion && player.PlayedThisTurn.Contains(source))
                plays--; // "another" — itself never counts

            if (plays < 1)
            {
                var candidates = player.Hand.FindAll(c =>
                    ShardsEngine.CountsAs(player, c.Def, _faction) && !c.Def.IsChampion);
                if (candidates.Count == 0) yield break;
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Reveal a {_faction} ally from your hand to trigger Unify?",
                    Context = "soi.reveal",
                    Min = 0,
                    Max = 1
                };
                foreach (var card in candidates)
                    request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId });
                yield return ShardsStep.AwaitDecision(request);
                if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
                var chosen = player.Hand.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                if (chosen == null) yield break;
                ctx.Engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = new List<string> { chosen.DefId } });
            }

            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Dominion: fires if the controller played and/or reveals from hand at
    /// least one card of EACH of the three non-Order base factions this turn.</summary>
    public sealed class Dominion : IShardsEffect
    {
        private static readonly ShardsFaction[] Required =
            { ShardsFaction.Homodeus, ShardsFaction.Undergrowth, ShardsFaction.Wraethe };
        private readonly IShardsEffect _inner;
        public IShardsEffect Inner => _inner;
        public Dominion(IShardsEffect inner) => _inner = inner;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var missing = new List<ShardsFaction>();
            foreach (var faction in Required)
                if (player.FactionPlays(faction) < 1)
                    missing.Add(faction);

            if (missing.Count > 0)
            {
                var candidates = player.Hand.FindAll(c => missing.Contains(c.Def.Faction));
                // Quick impossibility check: every missing faction needs a hand card.
                foreach (var faction in missing)
                    if (!candidates.Exists(c => c.Def.Faction == faction))
                        yield break;

                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Reveal cards to complete Dominion? (" + string.Join(", ", missing) + " needed)",
                    Context = "soi.reveal",
                    Min = 0,
                    Max = candidates.Count
                };
                foreach (var card in candidates)
                    request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name + " (" + card.Def.Faction + ")") { CardInstanceId = card.InstanceId });
                yield return ShardsStep.AwaitDecision(request);

                var revealedFactions = new HashSet<ShardsFaction>();
                var revealedIds = new List<string>();
                foreach (int id in ctx.Answer.ChosenOptionIds)
                {
                    var card = player.Hand.Find(c => c.InstanceId == id);
                    if (card == null) continue;
                    revealedFactions.Add(card.Def.Faction);
                    revealedIds.Add(card.DefId);
                }
                if (revealedIds.Count > 0)
                    ctx.Engine.Emit(new ShardsCardsRevealedEvent { PlayerIndex = player.Index, DefIds = revealedIds });
                foreach (var faction in missing)
                    if (!revealedFactions.Contains(faction))
                        yield break;
            }

            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Count-scaled gains: counter(ctx) × the per-unit amounts (Scion of
    /// Nothingness, Additri, Evokatus, Terminal Crescents…).</summary>
    public sealed class PerCount : IShardsEffect
    {
        private readonly System.Func<ShardsContext, int> _counter;
        private readonly int _gems, _power, _mastery, _health, _draw;
        /// <summary>Per-unit amounts (bot heuristics assume ~2 units).</summary>
        public (int gems, int power, int mastery, int health, int draw) PerUnit =>
            (_gems, _power, _mastery, _health, _draw);
        public PerCount(System.Func<ShardsContext, int> counter,
            int gems = 0, int power = 0, int mastery = 0, int health = 0, int draw = 0)
        {
            _counter = counter;
            _gems = gems; _power = power; _mastery = mastery; _health = health; _draw = draw;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            int n = _counter(ctx);
            if (n <= 0) yield break;
            var e = ctx.Engine;
            int p = ctx.ControllerIndex;
            if (_gems != 0) e.GainGems(p, _gems * n);
            if (_power != 0) e.GainPower(p, _power * n);
            if (_mastery != 0) e.GainMastery(p, _mastery * n);
            if (_health != 0) e.GainHealth(p, _health * n);
            if (_draw != 0) e.DrawCards(p, _draw * n);
        }
    }

    /// <summary>Choose a living opponent who loses N mastery (Venator of the Wastes).</summary>
    public sealed class OpponentLosesMastery : IShardsEffect
    {
        private readonly int _amount;
        public OpponentLosesMastery(int amount) => _amount = amount;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var opponents = new List<ShardsPlayer>(ctx.Engine.State.LivingOpponentsOf(ctx.ControllerIndex));
            if (opponents.Count == 0) yield break;
            if (opponents.Count == 1)
            {
                ctx.Engine.LoseMastery(opponents[0].Index, _amount);
                yield break;
            }
            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseMode,
                Title = $"Choose an opponent to lose {_amount} mastery",
                Context = "soi.target",
                Min = 1,
                Max = 1
            };
            foreach (var opponent in opponents)
                request.Options.Add(new DecisionOption(opponent.Index, opponent.Name));
            yield return ShardsStep.AwaitDecision(request);
            ctx.Engine.LoseMastery(ctx.Answer.ChosenOptionIds[0], _amount);
        }
    }

    /// <summary>"You may banish up to N cards from your hand and/or discard pile."
    /// optional=false: the card prints no "may" — banishing is mandatory (Forged in Flame).</summary>
    public sealed class BanishUpTo : IShardsEffect
    {
        private readonly int _count;
        private readonly bool _optional;
        public BanishUpTo(int count = 1, bool optional = true)
        {
            _count = count;
            _optional = optional;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var candidates = new List<ShardsCard>();
            candidates.AddRange(player.Hand);
            candidates.AddRange(player.Discard);
            if (candidates.Count == 0) yield break;
            var request = new DecisionRequest
            {
                PlayerIndex = player.Index,
                Kind = DecisionKind.ChooseCards,
                Title = _count == 1 ? "Banish a card from your hand or discard pile?"
                                    : $"Banish up to {_count} cards from your hand/discard?",
                Context = "soi.banish",
                Min = _optional ? 0 : System.Math.Min(_count, candidates.Count),
                Max = _count
            };
            foreach (var card in candidates)
            {
                string zone = card.Zone == ShardsZone.Hand ? "hand" : "discard";
                request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name + " (" + zone + ")") { CardInstanceId = card.InstanceId });
            }
            yield return ShardsStep.AwaitDecision(request);
            foreach (int id in ctx.Answer.ChosenOptionIds)
            {
                var card = candidates.Find(c => c.InstanceId == id);
                if (card == null) continue;
                ctx.Engine.Banish(card, card.Zone == ShardsZone.Hand ? player.Hand : player.Discard);
            }
        }
    }

    /// <summary>Return matching cards from the discard pile to hand — choose one, or all
    /// matches (World Piercer M20). The printed texts are IMPERATIVE ("Return a…", no
    /// "may") so the return is mandatory by default; optional=true for "may" wordings
    /// (Ingeminex Malice's reward).</summary>
    public sealed class ReturnFromDiscard : IShardsEffect
    {
        private readonly System.Func<ShardsCardDef, bool> _filter;
        private readonly string _what;
        private readonly bool _all;
        private readonly bool _optional;
        public ReturnFromDiscard(System.Func<ShardsCardDef, bool> filter, string what,
            bool all = false, bool optional = false)
        {
            _filter = filter;
            _what = what;
            _all = all;
            _optional = optional;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var matches = player.Discard.FindAll(c => _filter(c.Def));
            if (matches.Count == 0) yield break;

            if (!_all && (matches.Count > 1 || _optional))
            {
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Return a {_what} from your discard pile to your hand" + (_optional ? "?" : ""),
                    Context = "soi.return",
                    Min = _optional ? 0 : 1,
                    Max = 1
                };
                foreach (var card in matches)
                    request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId });
                yield return ShardsStep.AwaitDecision(request);
                matches = new List<ShardsCard>();
                foreach (int id in ctx.Answer.ChosenOptionIds)
                {
                    var chosen = player.Discard.Find(c => c.InstanceId == id);
                    if (chosen != null) matches.Add(chosen);
                }
            }

            foreach (var card in matches)
            {
                if (!player.Discard.Remove(card)) continue;
                card.Zone = ShardsZone.Hand;
                player.Hand.Add(card);
                ctx.Engine.Emit(new ShardsCardReturnedEvent { PlayerIndex = player.Index, InstanceId = card.InstanceId, DefId = card.DefId });
            }
        }
    }

    /// <summary>Destroy an enemy champion outright (bypasses defense — Thorn Zealot can
    /// even kill Li Hin), or ALL enemy champions (Ghostwillow M15).</summary>
    public sealed class DestroyEnemyChampions : IShardsEffect
    {
        private readonly bool _all;
        public DestroyEnemyChampions(bool all = false) => _all = all;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var targets = new List<(ShardsPlayer owner, ShardsCard champion)>();
            foreach (var opponent in engine.State.LivingOpponentsOf(ctx.ControllerIndex))
                foreach (var champion in opponent.Champions)
                    targets.Add((opponent, champion));
            if (targets.Count == 0) yield break;

            if (!_all && targets.Count > 1)
            {
                var request = new DecisionRequest
                {
                    PlayerIndex = ctx.ControllerIndex,
                    Kind = DecisionKind.ChooseCards,
                    Title = "Destroy an enemy champion",
                    Context = "soi.destroy",
                    Min = 1,
                    Max = 1
                };
                foreach (var (owner, champion) in targets)
                    request.Options.Add(new DecisionOption(champion.InstanceId, champion.Def.Name + " (" + owner.Name + ")") { CardInstanceId = champion.InstanceId });
                yield return ShardsStep.AwaitDecision(request);
                targets = targets.FindAll(t => ctx.Answer.ChosenOptionIds.Contains(t.champion.InstanceId));
            }
            else if (!_all)
            {
                targets = new List<(ShardsPlayer, ShardsCard)> { targets[0] };
            }

            foreach (var (owner, champion) in targets)
                engine.DestroyChampion(owner, champion, ctx.ControllerIndex);
        }
    }

    /// <summary>Warp N: fast-play a center-row ALLY costing ≤ maxCost for FREE (Brute 2,
    /// Lucky 3, Breaker/J-Chord). maxCost &lt; 0 = no limit.</summary>
    public sealed class WarpUpTo : IShardsEffect
    {
        private readonly int _maxCost;
        public WarpUpTo(int maxCost) => _maxCost = maxCost;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var slots = new List<int>();
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
            {
                var card = engine.State.CenterRow[s];
                if (card == null || card.Def.IsChampion) continue;
                if (_maxCost >= 0 && card.Def.Cost > _maxCost) continue;
                slots.Add(s);
            }
            if (slots.Count == 0) yield break;

            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = _maxCost >= 0 ? $"Warp: fast-play an ally costing {_maxCost} or less for free?"
                                      : "Warp: fast-play any ally from the row for free?",
                Context = "soi.warp",
                Min = 0,
                Max = 1
            };
            foreach (int s in slots)
            {
                var card = engine.State.CenterRow[s];
                request.Options.Add(new DecisionOption(s, card.Def.Name + " (cost " + card.Def.Cost + ")") { CardInstanceId = card.InstanceId });
            }
            yield return ShardsStep.AwaitDecision(request);
            if (ctx.Answer.ChosenOptionIds.Count == 0) yield break;
            engine.WarpFromRow(ctx.ControllerIndex, ctx.Answer.ChosenOptionIds[0]);
        }
    }

    /// <summary>Recruit a center-row card costing ≤ maxCost for free (Portal Monk,
    /// The Crystal Gate). MANDATORY when any card qualifies — the printed texts have no
    /// "may"; forced deck pollution is part of the design (BGG strategy consensus).
    /// toHand: Portal Monk M15 puts it in hand instead of discard.</summary>
    public sealed class RecruitFromRow : IShardsEffect
    {
        private readonly int _maxCost;
        private readonly bool _toHand;
        public RecruitFromRow(int maxCost, bool toHand = false)
        {
            _maxCost = maxCost;
            _toHand = toHand;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var engine = ctx.Engine;
            var slots = new List<int>();
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
            {
                var card = engine.State.CenterRow[s];
                if (card != null && card.Def.Cost <= _maxCost)
                    slots.Add(s);
            }
            if (slots.Count == 0) yield break;

            if (slots.Count == 1)
            {
                engine.RecruitFromRowFree(ctx.ControllerIndex, slots[0], _toHand);
                yield break;
            }

            var request = new DecisionRequest
            {
                PlayerIndex = ctx.ControllerIndex,
                Kind = DecisionKind.ChooseCards,
                Title = $"Recruit a card costing {_maxCost} or less for free",
                Context = "soi.recruit",
                Min = 1,
                Max = 1
            };
            foreach (int s in slots)
            {
                var card = engine.State.CenterRow[s];
                request.Options.Add(new DecisionOption(s, card.Def.Name + " (cost " + card.Def.Cost + ")") { CardInstanceId = card.InstanceId });
            }
            yield return ShardsStep.AwaitDecision(request);
            engine.RecruitFromRowFree(ctx.ControllerIndex, ctx.Answer.ChosenOptionIds[0], _toHand);
        }
    }

    /// <summary>Copy the play effect of a card played this turn (Ojas: non-champion;
    /// Taur: Undergrowth ally). Copying is NOT playing — no faction counts, no
    /// play-triggers; just the effect (FAQ).</summary>
    public sealed class CopyPlayedEffect : IShardsEffect
    {
        private readonly System.Func<ShardsCardDef, bool> _filter;
        private readonly string _what;
        private readonly int _copies;
        public CopyPlayedEffect(System.Func<ShardsCardDef, bool> filter, string what, int copies = 1)
        {
            _filter = filter;
            _what = what;
            _copies = copies;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var player = ctx.Controller;
            var candidates = player.PlayedThisTurn.FindAll(c =>
                c != ctx.Source && _filter(c.Def) && c.Def.PlayEffect != null);
            if (candidates.Count == 0) yield break;

            ShardsCard chosen;
            if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Copy the effect of a {_what} you played this turn",
                    Context = "soi.copy",
                    Min = 1,
                    Max = 1
                };
                foreach (var card in candidates)
                    request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId });
                yield return ShardsStep.AwaitDecision(request);
                chosen = candidates.Find(c => c.InstanceId == ctx.Answer.ChosenOptionIds[0]);
                if (chosen == null) yield break;
            }

            for (int i = 0; i < _copies; i++)
                foreach (var step in chosen.Def.PlayEffect.Resolve(ctx))
                    yield return step;
        }
    }

    /// <summary>Every living player loses N health (Ingeminex Brutality/Corruption,
    /// Bound for Life) — a LOSS, not damage: shields never apply; ties possible.</summary>
    public sealed class AllPlayersLoseHealth : IShardsEffect
    {
        private readonly int _amount;
        private readonly bool _includeController;
        public AllPlayersLoseHealth(int amount, bool includeController = true)
        {
            _amount = amount;
            _includeController = includeController;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            foreach (var player in ctx.Engine.State.Players)
            {
                if (player.Eliminated) continue;
                if (!_includeController && player.Index == ctx.ControllerIndex) continue;
                ctx.Engine.LoseHealth(player.Index, _amount);
            }
            yield break;
        }
    }

    /// <summary>Every living player loses N mastery (Ingeminex Torment).</summary>
    public sealed class AllPlayersLoseMastery : IShardsEffect
    {
        private readonly int _amount;
        public AllPlayersLoseMastery(int amount) => _amount = amount;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            foreach (var player in ctx.Engine.State.Players)
                if (!player.Eliminated)
                    ctx.Engine.LoseMastery(player.Index, _amount);
            yield break;
        }
    }

    /// <summary>Every living player discards N cards, each choosing which, clockwise from
    /// the active player (Ingeminex Agony).</summary>
    public sealed class AllPlayersDiscard : IShardsEffect
    {
        private readonly int _count;
        public AllPlayersDiscard(int count) => _count = count;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var state = ctx.Engine.State;
            for (int step = 0; step < state.Players.Count; step++)
            {
                var player = state.Players[(state.TurnPlayerIndex + step) % state.Players.Count];
                if (player.Eliminated || player.Hand.Count == 0) continue;
                int count = System.Math.Min(_count, player.Hand.Count);
                var request = new DecisionRequest
                {
                    PlayerIndex = player.Index,
                    Kind = DecisionKind.ChooseCards,
                    Title = $"Discard {count} card{(count > 1 ? "s" : "")}",
                    Context = "soi.discard",
                    Min = count,
                    Max = count
                };
                foreach (var card in player.Hand)
                    request.Options.Add(new DecisionOption(card.InstanceId, card.Def.Name) { CardInstanceId = card.InstanceId });
                yield return ShardsStep.AwaitDecision(request);
                foreach (int id in ctx.Answer.ChosenOptionIds)
                {
                    var card = player.Hand.Find(c => c.InstanceId == id);
                    if (card == null) continue;
                    player.Hand.Remove(card);
                    card.Zone = ShardsZone.Discard;
                    player.Discard.Add(card);
                }
            }
        }
    }

    /// <summary>Every player destroys their own highest-cost champion (Ingeminex Malice).
    /// Ties break deterministically: lowest instance id.</summary>
    public sealed class AllPlayersDestroyBiggestChampion : IShardsEffect
    {
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            foreach (var player in ctx.Engine.State.Players)
            {
                if (player.Eliminated || player.Champions.Count == 0) continue;
                ShardsCard biggest = null;
                foreach (var champion in player.Champions)
                    if (biggest == null || champion.Def.Cost > biggest.Def.Cost ||
                        (champion.Def.Cost == biggest.Def.Cost && champion.InstanceId < biggest.InstanceId))
                        biggest = champion;
                ctx.Engine.DestroyChampion(player, biggest, byPlayer: -1);
            }
            yield break;
        }
    }
}
