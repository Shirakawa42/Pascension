namespace Shards.Preflight
{
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Shards.Content;
using Shards.Engine;

internal static class BalanceCatalog
{
    internal static void Print()
    {
        ShardsContentRegistry.EnsureRegistered();
        var assembly = typeof(ShardsEngine).Assembly;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "shards-balance-card-catalog-v1",
            host_binary_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant(),
            cards = ShardsCardDatabase.All.OrderBy(x => x.Id, StringComparer.Ordinal).Select((x, i) => new
            {
                card_id = i + 1, id = x.Id, name = x.Name, set = x.Set,
                faction = x.Faction.ToString(), type = x.Type.ToString(), cost = x.Cost,
                character = x.Character, quantity = x.Quantity, replaces_id = x.ReplacesId,
                rules_text = x.RulesText, defense = x.Defense, shield = x.Shield,
                image_url = "/api/card-image?id=" + Uri.EscapeDataString(x.Id)
            }),
            heroes = ShardsEngine.DraftableCharacters.Select(id => new
            {
                id, name = ShardsContentRegistry.CharacterDisplayName(id),
                image_url = "/api/card-image?id=" + Uri.EscapeDataString(id)
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

}
