using System;
using System.Collections.Generic;

namespace Pascension.Engine.Cards
{
    /// <summary>
    /// Global registry of card definitions, populated once at startup by the Content
    /// assembly (ContentRegistry.RegisterAll). Read-only afterwards.
    /// </summary>
    public static class CardDatabase
    {
        private static readonly Dictionary<string, CardDefinition> Defs = new();

        public static void Register(CardDefinition def)
        {
            if (string.IsNullOrEmpty(def.Id))
                throw new ArgumentException("Card definition needs an id");
            Defs[def.Id] = def;
        }

        public static CardDefinition Get(string id)
        {
            if (!Defs.TryGetValue(id, out var def))
                throw new KeyNotFoundException($"Unknown card id '{id}' — is ContentRegistry.RegisterAll() called?");
            return def;
        }

        public static bool TryGet(string id, out CardDefinition def) => Defs.TryGetValue(id, out def);

        /// <summary>All registered definitions (sorted by id for deterministic iteration).</summary>
        public static IReadOnlyList<CardDefinition> All
        {
            get
            {
                var list = new List<CardDefinition>(Defs.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                return list;
            }
        }

        public static int Count => Defs.Count;

        /// <summary>Test hook: clear between test fixtures that register scripted cards.</summary>
        public static void Clear() => Defs.Clear();
    }
}
