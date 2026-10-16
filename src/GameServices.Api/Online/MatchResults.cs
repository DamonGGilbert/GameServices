using GameServices.Api.Catalog;
using GameServices.Api.Data;
using GameServices.Api.Economy;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Online;

/// <summary>
/// Records a finished duel: the history row, the Duel Points, and (for Guild Matches) the guild ratings.
/// The result comes from the duel host, never from a phone.
/// </summary>
public class MatchResults
{
    public const int MinTurnsForRewards = 3;
    public const int EloK = 32;

    private readonly AppDbContext db;
    private readonly CardCatalog catalog;

    public MatchResults(AppDbContext db, CardCatalog catalog)
    {
        this.db = db;
        this.catalog = catalog;
    }

    /// <summary>Elo: how much the first guild's rating moves. The other guild moves the opposite way.</summary>
    public static int EloDelta(int rating0, int rating1, int winnerSeat)
    {
        double expected0 = 1.0 / (1.0 + Math.Pow(10.0, (rating1 - rating0) / 400.0));
        double score0 = winnerSeat == 0 ? 1.0 : winnerSeat == 1 ? 0.0 : 0.5;
        return (int)Math.Round(EloK * (score0 - expected0));
    }

    public async Task<Dictionary<int, RewardsMessage>> RecordAsync(Match match, Room room, DuelResultMessage result, CancellationToken cancel)
    {
        RoomSeat seat0 = room.Seats[0]!;
        RoomSeat seat1 = room.Seats[1]!;
        RoomSeat[] seats = { seat0, seat1 };

        int winner = result.WinnerSeat is 0 or 1 ? result.WinnerSeat : -1;
        bool counts = result.Turns >= MinTurnsForRewards;

        DuelMatch row = new()
        {
            RoomName = room.Name,
            StartedUtc = match.StartedUtc,
            Player0Id = seat0.UserId,
            Player1Id = seat1.UserId,
            Player0Name = seat0.Username,
            Player1Name = seat1.Username,
            WinnerSeat = winner,
            Reason = Truncate(result.Reason, 200),
            Turns = result.Turns,
            GuildMatch = room.GuildMatch,
            Guild0Id = seat0.GuildId,
            Guild1Id = seat1.GuildId
        };

        // ---- guild ratings ----
        int[] guildDelta = { 0, 0 };
        int[] guildRating = { 0, 0 };
        string[] guildName = { seat0.GuildName, seat1.GuildName };

        if (room.GuildMatch && seat0.GuildId != null && seat1.GuildId != null)
        {
            Guid guildId0 = seat0.GuildId.Value;
            Guid guildId1 = seat1.GuildId.Value;

            Dictionary<Guid, Guild> guilds = await db.Guilds
                .Where(g => g.Id == guildId0 || g.Id == guildId1)
                .ToDictionaryAsync(g => g.Id, cancel);

            if (guilds.TryGetValue(guildId0, out Guild? g0) && guilds.TryGetValue(guildId1, out Guild? g1))
            {
                if (counts)
                {
                    int delta = EloDelta(g0.Rating, g1.Rating, winner);
                    g0.Rating += delta;
                    g1.Rating -= delta;
                    guildDelta[0] = delta;
                    guildDelta[1] = -delta;

                    if (winner == 0) { g0.Wins++; g1.Losses++; }
                    else if (winner == 1) { g1.Wins++; g0.Losses++; }
                    else { g0.Draws++; g1.Draws++; }

                    // If the two guilds are at war this weekend, this duel counts for it.
                    await GameServices.Api.Guilds.GuildWars.RecordDuelAsync(db, g0.Id, g1.Id, seat0.UserId, seat1.UserId, winner, DateTime.UtcNow, cancel);
                }

                guildRating[0] = g0.Rating;
                guildRating[1] = g1.Rating;
                row.Guild0Delta = guildDelta[0];
                row.Guild1Delta = guildDelta[1];
            }
        }

        db.DuelMatches.Add(row);
        await db.SaveChangesAsync(cancel);

        // ---- Duel Points (server-paid, with the usual limits) ----
        Dictionary<int, RewardsMessage> messages = new();

        for (int seat = 0; seat < 2; seat++)
        {
            int awarded = 0;
            Guid userId = seats[seat].UserId;
            string outcome = winner < 0 ? "draw" : winner == seat ? "win" : "loss";

            // Online duels pay no Duel Points (they are for glory, ranking and the guild wars).
            CardGameProfile profile = await EconomyEndpoints.GetOrCreateProfile(db, catalog, userId, cancel);
            messages[seat] = new RewardsMessage(awarded, profile.DuelPoints, guildDelta[seat], guildRating[seat], guildName[seat], room.GuildMatch);
        }

        return messages;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
