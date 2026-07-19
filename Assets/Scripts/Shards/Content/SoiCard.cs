using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Fluent builder for Shards of Infinity card definitions. One builder chain
    /// per card, mirroring Pascension's CardBuilder pattern. Rules text is a display
    /// paraphrase only — behavior lives in the composed effects.</summary>
    public sealed class SoiCard
    {
        private readonly ShardsCardDef _def = new();

        public static SoiCard New(string id, string name)
        {
            var b = new SoiCard();
            b._def.Id = id;
            b._def.Name = name;
            return b;
        }

        public ShardsCardDef Def => _def;

        public SoiCard InSet(string set) { _def.Set = set; return this; }
        public SoiCard Faction(ShardsFaction faction) { _def.Faction = faction; return this; }
        public SoiCard Type(ShardsCardType type) { _def.Type = type; return this; }
        public SoiCard Cost(int cost) { _def.Cost = cost; return this; }
        public SoiCard Qty(int quantity) { _def.Quantity = quantity; return this; }
        public SoiCard Defense(int defense) { _def.Defense = defense; return this; }
        public SoiCard Shield(int shield) { _def.Shield = shield; return this; }
        public SoiCard Character(string characterId) { _def.Character = characterId; return this; }
        public SoiCard Plays(IShardsEffect effect) { _def.PlayEffect = effect; return this; }
        public SoiCard Exhausts(IShardsEffect effect) { _def.ExhaustEffect = effect; return this; }
        /// <summary>"Pay N gems, Exhaust:" — the gems are part of the activation COST
        /// (unactivatable while unaffordable), never checked inside the effect.</summary>
        public SoiCard ExhaustCosts(int gems) { _def.ExhaustGemCost = gems; return this; }
        public SoiCard Reward(IShardsEffect effect) { _def.RewardEffect = effect; return this; }
        public SoiCard MonsterAttack(IShardsEffect effect) { _def.MonsterAttackEffect = effect; return this; }
        public SoiCard Text(string rulesText) { _def.RulesText = rulesText; return this; }
        public SoiCard Art(string prompt) { _def.ArtPrompt = prompt; return this; }

        public void Register() => ShardsCardDatabase.Register(_def);
    }

    /// <summary>Effect shorthands so builder chains read like card text.</summary>
    public static class E
    {
        public static IShardsEffect Gems(int n) => new Gain { Gems = n };
        public static IShardsEffect Power(int n) => new Gain { Power = n };
        public static IShardsEffect Mastery(int n) => new Gain { Mastery = n };
        public static IShardsEffect Health(int n) => new Gain { Health = n };
        public static IShardsEffect Draw(int n) => new Gain { Draw = n };
        public static IShardsEffect Mix(int gems = 0, int power = 0, int mastery = 0, int health = 0, int draw = 0) =>
            new Gain { Gems = gems, Power = power, Mastery = mastery, Health = health, Draw = draw };
        public static IShardsEffect Seq(params IShardsEffect[] parts) => new ShardsComposite(parts);
        public static IShardsEffect At(int mastery, IShardsEffect inner) => new AtMastery(mastery, inner);
    }
}
