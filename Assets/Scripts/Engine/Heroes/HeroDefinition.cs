using System;
using System.Collections.Generic;
using Pascension.Engine.Effects;

namespace Pascension.Engine.Heroes
{
    /// <summary>
    /// A hero: level-gated passive abilities plus an active (L3) and an ultimate (L9).
    /// Passives are the same static/triggered primitives cards use, each with a minimum level.
    /// </summary>
    public sealed class HeroDefinition
    {
        public string Id;
        public string Name;
        public string Archetype;
        public string Description = "";
        public string ArtPrompt = "";

        public List<(int minLevel, IStaticAbility ability)> PassiveStatics = new();
        public List<(int minLevel, TriggeredAbility ability)> PassiveTriggers = new();

        public int ActiveUnlockLevel = 3;
        public ActivatedAbility Active;
        public int UltimateUnlockLevel = 9;
        public ActivatedAbility Ultimate;
    }

    public static class HeroDatabase
    {
        private static readonly Dictionary<string, HeroDefinition> Defs = new();

        public static void Register(HeroDefinition def)
        {
            if (string.IsNullOrEmpty(def.Id)) throw new ArgumentException("Hero definition needs an id");
            Defs[def.Id] = def;
        }

        public static HeroDefinition Get(string id)
        {
            if (!Defs.TryGetValue(id, out var def))
                throw new KeyNotFoundException($"Unknown hero id '{id}'");
            return def;
        }

        public static IReadOnlyList<HeroDefinition> All
        {
            get
            {
                var list = new List<HeroDefinition>(Defs.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                return list;
            }
        }

        public static void Clear() => Defs.Clear();
    }
}
