using System;
namespace Shards.AI
{
    /// <summary>Bounded turn planning. Budgets count decision steps, not turns.
    /// Search does not guarantee optimal play or exhaustive hidden-state coverage.</summary>
    public sealed class PolicySearchSettings
    {
        public int Candidates=4, Depth=24, Worlds=2, Workers=8, TerminalNodes=512;
        // Margin applies to the legacy planner. The hybrid ranks plans directly
        // and tapers Prior near saturated values at normal action positions.
        public double Margin=.04, Prior=.015;
        // The hybrid preserves primitive choices while grouping equivalent resources.
        public bool Hybrid=true;
        public bool HybridSkipForced=true;
        // Optional extra budget for unusually long turns; most branches already
        // stop at the next turn boundary before the ordinary depth.
        public bool HybridFinishTurn;
        // Extra decisions only after End Turn has begun, to settle damage,
        // shields and cleanup effects before evaluating a leaf.
        public int EndTurnExtension;
        // Experimental rollout through a reply turn. One preserves the normal
        // turn-boundary critic; two evaluates after the following turn as well.
        // Each simulated actor sees only its own observation. This is a fixed
        // policy continuation, not an adversarial search of all replies.
        public int HorizonTurns=1;
        // Experimental profiles remain opt-in until paired strength and natural
        // game audits justify promotion. The frozen baseline keeps both false.
        public bool TacticalGuards, MixedResourcePlans, MenuPlans, SetupPlans;
        // Preserve optional decline and bound policy regularization at visible
        // effect menus. Experimental until natural games and paired tests pass.
        public bool OptionalChoices;
        // Enumerate complete selections only after Scry reveals its cards to
        // the actor. Experimental; does not preselect unknown future reveals.
        public bool ScryPlans;
        // Experimental removal of certified inactive destiny activations and
        // fully enumerated, side-effect-free canceled sacrifice previews.
        public bool PruneNoEffectPlans;
        // Prefer cheaper, shorter validated wins; stop once End Turn itself
        // survives public-world and defensive shield validation. Experimental.
        public bool SimplifyWins;
        // Experimental equivalent-prefix repairs: use a free banish before a
        // planned paid one, and apply known mastery setups before resources.
        public bool SequenceRepairs;
        // Relative trust in the policy at visible optional target choices.
        // Separate from ordinary turn-action priors; experimental profiles only.
        public double ChoicePriorScale=1;
        // Zero preserves the ordinary action prior; positive values bound its
        // negative log likelihood without changing candidate generation.
        public double ActionPriorCap;
        // Recheck only cap-induced action changes with a larger public-world
        // sample. Zero disables verification; never less than the root budget.
        public int PriorVerificationWorlds;
        public int RolloutStyles=1;
        // Optional branching at later visible effect menus, rather than only
        // the live root. Each branch can improve at most this many menus.
        public int MenuDepth;
        internal PolicySearchSettings ValidatedCopy()
        {
            if(HorizonTurns<1||HorizonTurns>2)throw new ArgumentOutOfRangeException(nameof(HorizonTurns));
            if(Candidates<1||Candidates>64||Depth<1||Depth>64||Worlds<1||Worlds>16||Workers<1||Workers>32||TerminalNodes<0||TerminalNodes>8192||RolloutStyles<1||RolloutStyles>4||MenuDepth<0||MenuDepth>2||EndTurnExtension<0||EndTurnExtension>128||double.IsNaN(Margin)||Margin<0||Margin>2||double.IsNaN(Prior)||Prior<0||Prior>1||PriorVerificationWorlds<0||PriorVerificationWorlds>16||double.IsNaN(ActionPriorCap)||ActionPriorCap<0||ActionPriorCap>30||double.IsNaN(ChoicePriorScale)||ChoicePriorScale<0||ChoicePriorScale>8)
                throw new ArgumentOutOfRangeException(nameof(PolicySearchSettings));
            return (PolicySearchSettings)MemberwiseClone();
        }
    }
}
