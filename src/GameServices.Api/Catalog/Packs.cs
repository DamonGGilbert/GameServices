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
    private static readonly RarityOdds[] Premium = { new("UN", 64), new("R", 24), new("SR", 8), new("UR", 3.2), new("SCR", 0.8) };
    private static readonly RarityOdds[] Mixed = { new("C", 88), new("UN", 12) };
    private static readonly RarityOdds[] Upper = { new("UN", 55), new("R", 33), new("SR", 9.5), new("UR", 2.5) };
    private static readonly RarityOdds[] Foil = { new("R", 56), new("SR", 29), new("UR", 12.5), new("SCR", 2.5) };

    // The official set packs of the Lucky Draw: 9 cards like the real boosters.
    private static readonly RarityOdds[] OfficialRare = { new("R", 62), new("SR", 25), new("UR", 11), new("SCR", 2) };

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
        })
    };

    /// <summary>The slots of an official set pack (Lucky Draw prize).</summary>
    public static readonly PackSlot[] OfficialSlots =
    {
        new PackSlot("Common slot", 7, Commons),
        new PackSlot("Premium slot", 1, Premium),
        new PackSlot("Rare slot", 1, OfficialRare)
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

        return RollSlots(pack.Slots, eligible, random);
    }

    /// <summary>Rolls an official set pack from the cards of that set only.</summary>
    public static List<string> RollOfficial(CatalogSet set, CardCatalog catalog, Random random)
    {
        List<CatalogCard> eligible = set.Cards.Select(catalog.Find).Where(c => c != null).Select(c => c!).ToList();
        return RollSlots(OfficialSlots, eligible, random);
    }

    private static List<string> RollSlots(PackSlot[] slots, List<CatalogCard> eligible, Random random)
    {
        List<string> result = new();

        foreach (PackSlot slot in slots)
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
