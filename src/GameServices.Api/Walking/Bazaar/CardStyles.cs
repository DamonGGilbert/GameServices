namespace GameServices.Api.Walking.Bazaar;

/// <summary>
/// THE MONSTER MAKER's card styles: the colours and pattern behind a monster on its card. Bought once with Glimmer,
/// then worn by any of your monsters (one style each). Only for looks - a style never changes a battle. A monster keeps
/// its style when it goes to a new home. The game draws each one (its own CardStyles has the colours, by the same id).
/// </summary>
public static class CardStyles
{
    public record Style(string Id, string Name, int Price);

    public static readonly IReadOnlyList<Style> All = new List<Style>
    {
        new("meadow", "Meadow Dots", 200),
        new("ocean", "Ocean Waves", 250),
        new("sunset", "Sunset Stripes", 250),
        new("candy", "Candy Checks", 300),
        new("peaks", "Mountain Peaks", 300),
        new("starry", "Starry Night", 450),
        new("ember", "Ember Rays", 450),
        new("frost", "Frost Sparkle", 600),
        new("gold", "Golden Glow", 1200),
        new("rainbow", "Rainbow Road", 2000),
    };

    public static Style? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The styles a walker owns, from "sunset,ocean" (anything no longer sold is left out).</summary>
    public static HashSet<string> Owned(string list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => Find(id) != null).ToHashSet();

    public static string Write(IEnumerable<string> ids) => string.Join(",", ids.Where(id => Find(id) != null).Distinct());
}
