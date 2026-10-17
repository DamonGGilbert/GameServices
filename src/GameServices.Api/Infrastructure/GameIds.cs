namespace GameServices.Api.Infrastructure;

/// <summary>
/// WHICH GAME IS ASKING? One account works in every game, but some things belong to ONE game: a card-game guild
/// should never show up in the walking game, and a player may be in one guild in each game.
///
/// The game says who it is with a header on every request:
///
///     X-Game-Id: walking
///
/// No header means the card game ("yugioh"), so the card game - which was written before this existed - keeps
/// working without a single change, and every guild created before now counts as a card-game guild.
/// Unknown ids are refused, so a typo can't quietly create a third, empty "game".
/// </summary>
public static class GameIds
{
    public const string Header = "X-Game-Id";

    public const string Yugioh = "yugioh";
    public const string Walking = "walking";

    public static readonly string[] Known = { Yugioh, Walking };

    /// <summary>
    /// The game this request comes from. Returns false (and a ready-made 400 answer) for an unknown game id.
    /// </summary>
    public static bool TryFromRequest(HttpContext http, out string gameId, out IResult? problem)
    {
        string raw = http.Request.Headers[Header].ToString().Trim().ToLowerInvariant();
        problem = null;

        if (raw.Length == 0)
        {
            gameId = Yugioh;
            return true;
        }

        if (Array.IndexOf(Known, raw) >= 0)
        {
            gameId = raw;
            return true;
        }

        gameId = "";
        problem = Results.Problem($"Unknown game '{raw}'.", statusCode: StatusCodes.Status400BadRequest);
        return false;
    }

    /// <summary>
    /// The most members one guild can have, per game. The walking game keeps guilds small and friendly (10), so
    /// everyone's steps really matter to the shared goal; the card game keeps its 30. The Unity client mirrors these
    /// numbers for its "x/10" displays.
    /// </summary>
    public static int MaxGuildMembers(string gameId) => gameId == Walking ? 10 : 30;
}
