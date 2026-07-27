using System.Collections.Generic;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Bots;
using Shards.Engine;

namespace SoiSim
{
    /// <summary>A measurement instrument, not a game bot: plays the PLAY phase with one
    /// weight vector and the ACQUISITION phase with another, so the Elo cost of degrading
    /// each axis can be measured separately.
    ///
    /// The question it answers: the deck-builder literature says most of the decision
    /// complexity lives in the buy/acquire choice rather than in card play (Dominion's
    /// Provincial deliberately uses a dumb play model; the Dominion DQN learns buys only).
    /// But Slay the Spire is the inverse, and SoI has two properties Dominion lacks —
    /// mastery thresholds that resolve mid-sequence, and multiplicative burst — that should
    /// push value back toward play. Rather than inherit either answer, measure it here.
    ///
    /// WHY TWO STAGES INSTEAD OF ONE BLENDED ARGMAX. Scores from two tuned vectors are not
    /// on a common scale (V1's PlayBase is 2000, V5's is 810), so ranking a V1 play action
    /// against a V5 buy action in one argmax compares nothing meaningful. Each stage
    /// therefore runs its own argmax against its own EndTurn baseline, which is also how a
    /// SoI turn is actually shaped: play out, then spend.
    ///
    /// Stateless by construction — the phase is derived from the position each time it is
    /// asked, so there is no per-turn flag to reset and no way for the two seats to desync.</summary>
    public sealed class PhaseHybridBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _playModel;
        private readonly ShardsValueModel _buyModel;

        public PhaseHybridBot(ShardsEngine engine, ShardsValueModel playModel, ShardsValueModel buyModel)
        {
            _engine = engine;
            _playModel = playModel;
            _buyModel = buyModel;
        }

        /// <summary>Acquisition-phase actions: everything that converts gems into lasting
        /// board or deck state. Focus counts — it is a gem spend competing with a buy.</summary>
        public static bool IsAcquisition(PlayerAction action) => action switch
        {
            ShardsBuyCardAction => true,
            ShardsRerollRowAction => true,
            ShardsFocusAction => true,
            ShardsTakeDestinyAction => true,
            ShardsRecruitRelicAction => true,
            _ => false
        };

        /// <summary>Play-phase actions: everything that resolves cards or board abilities.
        /// The hero ability sits here — it is a per-turn tactical activation, not a purchase.</summary>
        public static bool IsPlay(PlayerAction action) => action switch
        {
            ShardsPlayCardAction => true,
            ShardsExhaustAction => true,
            ShardsAttackMonsterAction => true,
            ShardsHeroAbilityAction => true,
            _ => false
        };

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            if (pending.Kind == PendingInputKind.Decision)
                // Splits, shields, reveals, scry — tactical resolution, so the PLAY policy
                // owns them. Attributing them to the buy axis would flatter it.
                return new SubmitDecisionAction
                {
                    PlayerIndex = pending.PlayerIndex,
                    Answer = _playModel.ChooseAnswer(_engine, pending.Decision)
                };

            int index = pending.PlayerIndex;
            var legal = _engine.LegalActions(index);
            var player = _engine.State.Players[index];
            var endTurn = new ShardsEndTurnAction { PlayerIndex = index };

            var play = Best(legal, player, _playModel, IsPlay, out double playScore);
            if (play != null && playScore > _playModel.ScoreAction(_engine, player, endTurn))
                return play;

            var buy = Best(legal, player, _buyModel, IsAcquisition, out double buyScore);
            if (buy != null && buyScore > _buyModel.ScoreAction(_engine, player, endTurn))
                return buy;

            return endTurn;
        }

        private PlayerAction Best(IReadOnlyList<PlayerAction> legal, ShardsPlayer player,
            ShardsValueModel model, System.Func<PlayerAction, bool> belongs, out double bestScore)
        {
            PlayerAction best = null;
            bestScore = double.MinValue;
            foreach (var action in legal)
            {
                if (!belongs(action)) continue;
                double score = model.ScoreAction(_engine, player, action);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                }
            }
            return best;
        }
    }
}
