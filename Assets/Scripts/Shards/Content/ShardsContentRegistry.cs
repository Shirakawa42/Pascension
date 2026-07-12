using System.Collections.Generic;
using Pascension.Core;
using Shards.Engine;

namespace Shards.Content
{
    /// <summary>Registers the full Shards of Infinity card database (base + the three
    /// expansions) into ShardsCardDatabase, and builds standard game configs.</summary>
    public static class ShardsContentRegistry
    {
        /// <summary>Idempotent; safe to call from tests that clear the database.</summary>
        public static void EnsureRegistered()
        {
            if (ShardsCardDatabase.TryGet("crystal", out _) &&
                ShardsCardDatabase.TryGet("ingeminex_malice", out _))
                return;
            ShardsCardDatabase.Clear();
            ShardsBaseSet.Register();
            ShardsRelicsSet.Register();
            ShardsShadowSet.Register();
            ShardsHorizonSet.Register();
        }

        /// <summary>The playable characters; Rez requires Shadow of Salvation.</summary>
        public static IReadOnlyList<string> CharactersFor(ShardsDlc dlc) =>
            (dlc & ShardsDlc.ShadowOfSalvation) != 0
                ? new[] { "decima", "tetra", "volos", "kosynwu", "rez" }
                : new[] { "decima", "tetra", "volos", "kosynwu" };

        public static string CharacterDisplayName(string id) => id switch
        {
            "decima" => "Decima",
            "tetra" => "Tetra",
            "volos" => "Volos",
            "kosynwu" => "Ko Syn Wu",
            "rez" => "Rez",
            _ => id
        };

        public static ShardsConfig StandardConfig(ulong seed, List<PlayerSpec> players, ShardsDlc dlc)
        {
            EnsureRegistered();
            return new ShardsConfig
            {
                Seed = seed,
                Dlc = dlc,
                Players = players,
                Rules = new ShardsRules()
            };
        }
    }
}
