using System.Text.Json;

namespace GameServices.Api.Catalog;

/// <summary>
/// Extra = an Extra Deck card (Fusion Monster). Limit = how many copies may be in a deck (3 normally;
/// 2, 1 or 0 on the ban list). Both are exported from Unity along with the rest.
/// </summary>
public record CatalogCard(string Id, string Name, string Rarity, int Category, int Atk = 0, bool Extra = false, int Limit = 3);

public record StarterEntry(string Id, int Qty);

/// <summary>
/// The server's own list of every card that exists, and which rarity each one is.
/// It is exported from the Unity project (Unity menu: Tools > Server > Export Catalog) into
/// Catalog/catalog.json. The server must know this to roll packs itself: if the game rolled
/// the packs, a hacked game could just roll "Secret Rare" every time.
/// </summary>
public class CardCatalog
{
    private class FileShape
    {
        public int StarterDuelPoints { get; set; }
        public List<CatalogCard> Cards { get; set; } = new();
        public List<StarterEntry> StarterDeck { get; set; } = new();
    }

    private readonly Dictionary<string, CatalogCard> byId;

    public int StarterDuelPoints { get; }
    public IReadOnlyList<CatalogCard> Cards { get; }
    public IReadOnlyList<StarterEntry> StarterDeck { get; }

    public CardCatalog(int starterDuelPoints, IReadOnlyList<CatalogCard> cards, IReadOnlyList<StarterEntry> starterDeck)
    {
        StarterDuelPoints = starterDuelPoints;
        Cards = cards;
        StarterDeck = starterDeck;
        byId = cards.ToDictionary(c => c.Id);
    }

    public bool Exists(string cardId) => byId.ContainsKey(cardId);

    public CatalogCard? Find(string cardId) => byId.GetValueOrDefault(cardId);

    public static CardCatalog LoadFile(string path)
    {
        FileShape shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Catalog file is empty.");

        if (shape.Cards.Count == 0)
        {
            throw new InvalidOperationException("Catalog has no cards. Export it from Unity first.");
        }

        return new CardCatalog(shape.StarterDuelPoints, shape.Cards, shape.StarterDeck);
    }
}
