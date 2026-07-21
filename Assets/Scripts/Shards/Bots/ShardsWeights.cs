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

        public const int Count = 42;
    }
}
