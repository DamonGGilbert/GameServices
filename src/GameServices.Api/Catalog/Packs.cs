namespace GameServices.Api.Catalog;

/// <summary>Which cards a pack may contain.</summary>
public enum PackFilter { All, MonstersOnly, SpellsAndTrapsOnly }

public record RarityOdds(string Rarity, double Weight);

public record PackSlot(string Name, int Quantity, RarityOdds[] Odds);

public record PackDefinition(string Id, string Name, int Price, PackFilter Filter, PackSlot[] Slots)
{
    public int CardCount => Slots.Sum(s => s.Quantity);
}

/// <summary>
/// The shop's products. Prices and odds live HERE, on the server, so a player can't edit them.
/// (The game's shop screen shows the same numbers; if you change one, change the other.)
/// </summary>
public static class Packs
{
    private static readonly RarityOdds[] Commons = { new("C", 100) };
    private static readonly RarityOdds[] Premium = { new("UN", 60), new("R", 25), new("SR", 9), new("UR", 4.5), new("SCR", 1.5) };
    private static readonly RarityOdds[] Mixed = { new("C", 85), new("UN", 15) };
    private static readonly RarityOdds[] Upper = { new("UN", 50), new("R", 35), new("SR", 12), new("UR", 3) };
    private static readonly RarityOdds[] Foil = { new("R", 50), new("SR", 30), new("UR", 15), new("SCR", 5) };

    public static readonly IReadOnlyList<PackDefinition> All = new[]
    {
        new PackDefinition("damos-pack", "DAMO'S PACK", 300, PackFilter.All, new[]
        {
            new PackSlot("Common slot", 7, Commons),
            new PackSlot("Premium slot", 2, Premium)
        }),
        new PackDefinition("monster-mayhem", "MONSTER MAYHEM", 350, PackFilter.MonstersOnly, new[]
        {
            new PackSlot("Common slot", 6, Mixed),
            new PackSlot("Premium slot", 2, Upper),
            new PackSlot("Foil slot", 1, Foil)
        }),
        new PackDefinition("magic-and-mischief", "MAGIC & MISCHIEF", 350, PackFilter.SpellsAndTrapsOnly, new[]
        {
            new PackSlot("Common slot", 6, Mixed),
            new PackSlot("Premium slot", 2, Upper),
            new PackSlot("Foil slot", 1, Foil)
        }),
        new PackDefinition("secret-vault", "SECRET VAULT", 1200, PackFilter.All, new[]
        {
            new PackSlot("Rare+ slot", 3, new RarityOdds[] { new("R", 50), new("SR", 40), new("UR", 10) }),
            new PackSlot("Super+ slot", 1, new RarityOdds[] { new("SR", 55), new("UR", 35), new("SCR", 10) }),
            new PackSlot("Ultra+ slot", 1, new RarityOdds[] { new("UR", 60), new("SCR", 40) })
        })
    };

    public static PackDefinition? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Rolls every slot of the pack and returns the card ids.</summary>
    public static List<string> Roll(PackDefinition pack, CardCatalog catalog, Random random)
    {
        List<CatalogCard> eligible = catalog.Cards.Where(c => pack.Filter switch
        {
            PackFilter.MonstersOnly => c.Category == 0,
            PackFilter.SpellsAndTrapsOnly => c.Category != 0,
            _ => true
        }).ToList();

        List<string> result = new();

        foreach (PackSlot slot in pack.Slots)
        {
            for (int i = 0; i < slot.Quantity; i++)
            {
                // Only rarities that actually have cards can be rolled.
                RarityOdds[] usable = slot.Odds.Where(o => eligible.Any(c => c.Rarity == o.Rarity)).ToArray();

                CatalogCard pick;

                if (usable.Length == 0)
                {
                    pick = eligible[random.Next(eligible.Count)];
                }
                else
                {
                    double roll = random.NextDouble() * usable.Sum(o => o.Weight);
                    string rarity = usable[^1].Rarity;

                    foreach (RarityOdds odds in usable)
                    {
                        roll -= odds.Weight;

                        if (roll <= 0)
                        {
                            rarity = odds.Rarity;
                            break;
                        }
                    }

                    List<CatalogCard> pool = eligible.Where(c => c.Rarity == rarity).ToList();
                    pick = pool[random.Next(pool.Count)];
                }

                result.Add(pick.Id);
            }
        }

        return result;
    }
}
