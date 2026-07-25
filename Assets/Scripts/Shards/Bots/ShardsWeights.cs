namespace Shards.Bots
{
    /// <summary>Named indices into the tuned weight vector. The vector drives the
    /// greedy policy (play/buy/decision choices), the ISMCTS rollout policy and move
    /// ordering. Tuned by `soisim tune` (CMA-ES self-play); stored in the generated
    /// ShardsEvalWeights.g.cs. Add new weights at the END — old vectors stay loadable.</summary>
    public static class W
    {
        // Resource values (deck quality / buy evaluation).
        public const int Gems = 0, Power = 1, Mastery = 2, Health = 3, Draw = 4;
        // Condition-class discounts (probability the gated line fires).
        public const int Unify = 5, Dominion = 6, If = 7, Faction = 8;
        public const int PerCountUnits = 9;
        public const int ChampionExhaustMult = 10;
        public const int ShieldPerPoint = 11, DefensePerPoint = 12, TauntBonus = 13;
        // Structural capability values.
        public const int WarpPerCost = 14, RecruitRowPerCost = 15, DestroyChampion = 16,
            BanishPerCapacity = 17, ReturnFromDiscard = 18, CopyEffect = 19,
            OppMasteryLoss = 20, AllLoseHealth = 21, AllLoseMastery = 22;
        // Buy policy.
        public const int BuyThreshold = 23, DeckDilutionPerCard = 24, FastPlayMasteryGate = 25;
        // Play-order scoring.
        public const int PlayMastery = 26, PlayDraw = 27, PlayChampionBonus = 28, PlayConditionLit = 29;
        // Action-ladder base scores (argmax scale).
        public const int ExhaustBase = 30, AttackMonsterBase = 31, TakeDestinyBase = 32,
            RecruitRelicBase = 33, FocusBase = 34, EndTurnBase = 35;
        // Decision scoring.
        public const int DiscardShieldKeep = 36, BanishStarterValue = 37,
            SplitFaceBias = 38, SplitKillPerCost = 39;
        public const int PlayBase = 40, BuyBase = 41;
        // Play-order synergy: penalty on a play's currently-UNLIT conditional value
        // while other hand cards could still enable it (defers Carnivorous Vine-style
        // cards behind their enablers). Added 2026-07-21; older vectors lack it —
        // ShardsValueModel falls back to the default via WeightAt.
        public const int PlayDeferPotential = 42;

        // Duel of Doom (added 2026-07-25). Until now the two Duel actions were scored
        // with hardcoded constants either side of EndTurnBase: the hero ability always
        // fired (unconditionally, as the last act of every turn) and the row reroll could
        // NEVER be chosen by an argmax policy, so it appeared in zero rollouts and zero
        // training positions. These make both real, tunable decisions.
        /// <summary>MULTIPLIES the hero ability's net value (effect worth minus its gem
        /// and health costs) rather than adding a base to it. That matters: an additive
        /// base large enough to matter in the action ladder swamps a net value of ±2, so
        /// the ability would fire unconditionally — which is precisely what the old
        /// hardcoded `EndTurnBase + 0.05` did, and why Ko Syn Wu would pay 3 health every
        /// turn for a banish this model prices as negative. Multiplying keeps the sign of
        /// `net` in charge of whether to use it at all.</summary>
        public const int HeroAbilityValueScale = 43;
        public const int RerollBase = 44;
        /// <summary>Scales how far the rerolled slot sits below the buy threshold —
        /// i.e. how DEAD the slot is — into reroll value.</summary>
        public const int RerollRowQualityDelta = 45;
        // Center-deck manipulation and hand disruption, previously walked to exactly 0.
        // Rez's entire hero ability is Scry(2), so it was literally valueless.
        public const int ScryPerCard = 46, ReorderPerCard = 47, OppHandStrip = 48;

        public const int Count = 49;

        /// <summary>Fallback for every index. Two consumers, one source of truth:
        /// ShardsValueModel.WeightAt uses it when an older (shorter) tuned vector is
        /// loaded, and the tuner uses it to pad a short champion up to the current layout
        /// so newly appended weights actually get tuned instead of silently sitting at
        /// their default forever. Indices 0..41 are V1's hand-set starting point.
        /// APPENDING A WEIGHT MEANS APPENDING ITS DEFAULT HERE.</summary>
        public static readonly double[] Defaults =
        {
            1.0, 1.0, 3.0, 0.3, 1.6, 0.6,
            0.3, 0.5, 0.5, 2.0, 2.0, 0.3,
            0.2, 1.0, 0.5, 0.3, 2.5, 0.8,
            1.2, 2.0, 1.5, 0.5, 0.5, 0.6,
            0.02, 0.67, 100.0, 10.0, 50.0, 20.0,
            1500.0, 1400.0, 1300.0, 1250.0, 500.0, 0.0,
            1.0, 1.0, 1.0, 1.0, 2000.0, 1000.0,
            0.6,
            // Duel. The hero scale lifts a net value of ~±2 into the action ladder's range
            // while keeping its SIGN decisive. Reroll carries a standing reluctance that
            // the deadness term must overcome, so a healthy row is never churned and a
            // dead one is. NOTE these must be non-zero: the CMA-ES tuner scales each
            // dimension by max(|start|, 0.05), so a weight defaulting to 0.0 gets a search
            // range near zero and never moves (which is why EndTurnBase has sat at ~0
            // since V1).
            50.0, -10.0, 100.0,
            0.15, 0.25, 0.5
        };

        /// <summary>Pads a tuned vector up to the current layout with <see cref="Defaults"/>.
        /// Returns the input unchanged when it is already long enough.</summary>
        public static double[] Pad(double[] weights)
        {
            if (weights != null && weights.Length >= Count) return weights;
            var padded = new double[Count];
            for (int i = 0; i < Count; i++)
                padded[i] = weights != null && i < weights.Length ? weights[i] : Defaults[i];
            return padded;
        }
    }
}
