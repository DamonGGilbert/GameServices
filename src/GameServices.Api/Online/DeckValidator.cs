using GameServices.Api.Catalog;

namespace GameServices.Api.Online;

/// <summary>
/// Is this deck allowed? Checked on the SERVER against what the player really OWNS, so a hacked game
/// can't bring cards it hasn't got. Returns plain-English problems (empty list = fine).
/// </summary>
public static class DeckValidator
{
    public const int MainMin = 40;
    public const int MainMax = 60;
    public const int ExtraMax = 15;
    public const int CopiesMax = 3;

    public static List<string> Check(DeckList deck, CardCatalog catalog, IReadOnlyDictionary<string, int> owned)
    {
        List<string> problems = new();

        List<string> main = deck.Main ?? new List<string>();
        List<string> extra = deck.Extra ?? new List<string>();

        if (main.Count < MainMin) problems.Add($"The Main Deck needs at least {MainMin} cards (it has {main.Count}).");
        if (main.Count > MainMax) problems.Add($"The Main Deck can have at most {MainMax} cards (it has {main.Count}).");
        if (extra.Count > ExtraMax) problems.Add($"The Extra Deck can have at most {ExtraMax} cards (it has {extra.Count}).");

        foreach (string id in main.Concat(extra).Distinct())
        {
            if (!catalog.Exists(id))
            {
                problems.Add($"Unknown card '{id}'.");
            }
        }

        foreach (IGrouping<string, string> group in main.Concat(extra).GroupBy(id => id))
        {
            CatalogCard? card = catalog.Find(group.Key);

            if (card == null)
            {
                continue;
            }

            int copies = group.Count();
            int limit = Math.Min(CopiesMax, card.Limit);
            int have = owned.GetValueOrDefault(group.Key);

            if (limit == 0) problems.Add($"{card.Name} is forbidden.");
            else if (copies > limit) problems.Add($"You can only use {limit} of {card.Name} (you have {copies} in the deck).");

            if (copies > have) problems.Add($"You only own {have} of {card.Name}.");
        }

        foreach (string id in main.Distinct())
        {
            if (catalog.Find(id) is { Extra: true } card)
            {
                problems.Add($"{card.Name} belongs in the Extra Deck, not the Main Deck.");
            }
        }

        foreach (string id in extra.Distinct())
        {
            if (catalog.Find(id) is { Extra: false } card)
            {
                problems.Add($"{card.Name} can't go in the Extra Deck.");
            }
        }

        return problems.Distinct().Take(8).ToList();
    }
}
