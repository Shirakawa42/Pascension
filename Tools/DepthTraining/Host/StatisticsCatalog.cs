using System.Linq;
using Shards.Content;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class StatisticsCatalog
    {
        internal static object Export() => new
        {
            schema = "shards-balance-card-catalog-v1",
            observation_schema = Encoder.SchemaVersion,
            cards = Encoder.CardIds.Select((id, index) =>
            {
                var d = ShardsCardDatabase.Get(id);
                return new { card_id = index + 1, id, name = d.Name, set = d.Set,
                    faction = d.Faction.ToString(), type = d.Type.ToString(), cost = d.Cost,
                    character = d.Character, quantity = d.Quantity, replaces_id = d.ReplacesId,
                    rules_text = d.RulesText, defense = d.Defense, shield = d.Shield,
                    image_url = "/api/card-image?id=" + id };
            }).ToArray(),
            heroes = ShardsEngine.DraftableCharacters.Select(id => new { id,
                name = id == "kosynwu" ? "Ko Syn Wu" : char.ToUpperInvariant(id[0]) + id.Substring(1),
                image_url = "/api/card-image?id=" + id }).ToArray()
        };
    }
}
