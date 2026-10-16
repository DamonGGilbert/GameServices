using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Guilds;

public record WarFighterDto(string Username, int Slot, bool Played, bool Won);

public record WarInfo(
    string Status,
    string OpponentName,
    string OpponentTag,
    int OpponentRating,
    int MyScore,
    int TheirScore,
    DateTime StartsUtc,
    DateTime EndsUtc,
    DateTime NextWarStartsUtc,
    string Result,
    List<WarFighterDto> Mine,
    List<WarFighterDto> Theirs,
    string Note);

/// <summary>
/// How strong a guild is, from its Guild Match rating and its war wins. The tier decides which banner styles the
/// leader may pick, so a shiny banner tells everyone "this guild is good".
/// </summary>
public static class GuildTiers
{
    public const int WarWinPoints = 50;

    public static readonly string[] Names = { "Rookie", "Bronze", "Silver", "Gold", "Legend" };

    // The score needed for each tier.
    private static readonly int[] Thresholds = { 0, 1150, 1300, 1500, 1750 };

    public static int Score(int rating, int warWins) => rating + (WarWinPoints * warWins);

    public static int TierOf(int rating, int warWins)
    {
        int score = Score(rating, warWins);
        int tier = 0;

        for (int i = 0; i < Thresholds.Length; i++)
        {
            if (score >= Thresholds[i])
            {
                tier = i;
            }
        }

        return tier;
    }

    public static string NameOf(int tier) => Names[Math.Clamp(tier, 0, Names.Length - 1)];
}

/// <summary>
/// GUILD WARS. Every weekend (Saturday 00:00 to Monday 00:00, UTC) each guild with at least 3 players in its main
/// team is paired with a guild of similar rating. Each main-team player of both guilds has ONE war duel: a Guild Match
/// against a player of the other guild. A win scores a point. When the weekend ends the guild with more points wins:
/// its fighters earn more Duel Points, and the guild's war wins (and rating) go up.
///
/// Like the boss job, this is run by the background job once a minute, and every rule is checked here on the server.
/// </summary>
public static class GuildWars
{
    public const int MinFighters = 3;
    public const int WinnerReward = 300;
    public const int LoserReward = 100;
    public const int DrawReward = 150;
    public const int WinnerRatingBonus = 20;

    /// <summary>The Saturday 00:00 (UTC) that starts the weekend containing "now", or null on a weekday.</summary>
    public static DateTime? WeekendStart(DateTime now)
    {
        DateTime day = now.Date;

        return now.DayOfWeek switch
        {
            DayOfWeek.Saturday => day,
            DayOfWeek.Sunday => day.AddDays(-1),
            _ => null
        };
    }

    /// <summary>The Saturday 00:00 (UTC) of the next weekend that has not started yet.</summary>
    public static DateTime NextWeekendStart(DateTime now)
    {
        int ahead = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        DateTime saturday = now.Date.AddDays(ahead);

        if (saturday <= now && WeekendStart(now) != null)
        {
            saturday = saturday.AddDays(7);
        }

        return saturday;
    }

    public static DateTime WeekendEnd(DateTime weekendStart) => weekendStart.AddDays(2);

    /// <summary>One pass of the job: close wars that ended, then start this weekend's wars. Public so tests can run it with a pretend clock.</summary>
    public static async Task<int> RunOnce(AppDbContext db, DateTime now, CancellationToken cancel = default)
    {
        int changes = await CloseFinished(db, now, cancel);

        DateTime? weekend = WeekendStart(now);

        if (weekend != null)
        {
            changes += await PairGuilds(db, weekend.Value, cancel);
        }

        return changes;
    }

    // ------------------------------------------------------------------ starting wars

    private static async Task<int> PairGuilds(AppDbContext db, DateTime weekendStart, CancellationToken cancel)
    {
        // Guilds that already have a war this weekend.
        var pairs = await db.GuildWars.AsNoTracking()
            .Where(w => w.StartsUtc == weekendStart)
            .Select(w => new { w.GuildAId, w.GuildBId })
            .ToListAsync(cancel);

        List<Guid> busy = pairs.SelectMany(x => new[] { x.GuildAId, x.GuildBId }).ToList();

        // Guilds with enough players in the main team (places 1-5).
        List<Guid> ready = await db.GuildMembers.AsNoTracking()
            .Where(m => m.Slot >= 1 && m.Slot <= GuildEndpoints.MainTeamSize)
            .GroupBy(m => m.GuildId)
            .Where(g => g.Count() >= MinFighters)
            .Select(g => g.Key)
            .ToListAsync(cancel);

        List<Guild> guilds = await db.Guilds
            .Where(g => ready.Contains(g.Id) && !busy.Contains(g.Id))
            .ToListAsync(cancel);

        // Similar ratings face each other.
        List<Guild> ordered = guilds.OrderByDescending(g => g.Rating).ThenBy(g => g.Id).ToList();
        int created = 0;

        for (int i = 0; i + 1 < ordered.Count; i += 2)
        {
            db.GuildWars.Add(new GuildWar
            {
                GuildAId = ordered[i].Id,
                GuildBId = ordered[i + 1].Id,
                StartsUtc = weekendStart,
                EndsUtc = WeekendEnd(weekendStart)
            });

            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(cancel);
        }

        return created;
    }

    // ------------------------------------------------------------------ counting duels

    /// <summary>
    /// A Guild Match between two guilds just finished. If those guilds are at war right now, each main-team player's
    /// FIRST duel of the war counts (a win scores a point). Called by the duel result code.
    /// </summary>
    public static async Task RecordDuelAsync(AppDbContext db, Guid guild0, Guid guild1, Guid user0, Guid user1, int winnerSeat, DateTime now, CancellationToken cancel)
    {
        GuildWar? war = await db.GuildWars.FirstOrDefaultAsync(w =>
            w.Status == WarStatus.Active && w.StartsUtc <= now && w.EndsUtc > now &&
            ((w.GuildAId == guild0 && w.GuildBId == guild1) || (w.GuildAId == guild1 && w.GuildBId == guild0)), cancel);

        if (war == null)
        {
            return;
        }

        Guid[] users = { user0, user1 };
        Guid[] guilds = { guild0, guild1 };

        for (int seat = 0; seat < 2; seat++)
        {
            Guid userId = users[seat];

            bool inMainTeam = await db.GuildMembers.AsNoTracking().AnyAsync(m =>
                m.UserId == userId && m.GuildId == guilds[seat] && m.Slot >= 1 && m.Slot <= GuildEndpoints.MainTeamSize, cancel);

            if (!inMainTeam || await db.GuildWarDuels.AnyAsync(d => d.WarId == war.Id && d.UserId == userId, cancel))
            {
                continue;
            }

            bool won = winnerSeat == seat;

            db.GuildWarDuels.Add(new GuildWarDuel { WarId = war.Id, UserId = userId, GuildId = guilds[seat], Won = won });

            if (won)
            {
                if (guilds[seat] == war.GuildAId) war.ScoreA++; else war.ScoreB++;

                // Winning your duel in a Guild War earns a Lucky Draw ticket (once per war).
                await GameServices.Api.Economy.Tickets.GrantAsync(db, userId, "war-" + war.Id.ToString("N"), cancel);
            }
        }
    }

    // ------------------------------------------------------------------ ending wars

    private static async Task<int> CloseFinished(AppDbContext db, DateTime now, CancellationToken cancel)
    {
        List<GuildWar> over = await db.GuildWars.Where(w => w.Status == WarStatus.Active && w.EndsUtc <= now).ToListAsync(cancel);

        foreach (GuildWar war in over)
        {
            Guild? a = await db.Guilds.FirstOrDefaultAsync(g => g.Id == war.GuildAId, cancel);
            Guild? b = await db.Guilds.FirstOrDefaultAsync(g => g.Id == war.GuildBId, cancel);

            war.Status = WarStatus.Finished;

            Guid? winner = war.ScoreA > war.ScoreB ? war.GuildAId : war.ScoreB > war.ScoreA ? war.GuildBId : null;
            war.WinnerGuildId = winner;

            if (a != null && b != null && winner != null)
            {
                Guild won = winner == a.Id ? a : b;
                Guild lost = winner == a.Id ? b : a;

                won.WarWins++;
                won.Rating += WinnerRatingBonus;
                lost.WarLosses++;
            }

            // Everyone who fought is paid (more for the winning guild).
            List<GuildWarDuel> fighters = await db.GuildWarDuels.Where(d => d.WarId == war.Id).ToListAsync(cancel);

            foreach (GuildWarDuel fighter in fighters)
            {
                int reward = winner == null ? DrawReward : fighter.GuildId == winner ? WinnerReward : LoserReward;
                await Pay(db, fighter.UserId, reward, "Guild war", cancel);
            }
        }

        if (over.Count > 0)
        {
            await db.SaveChangesAsync(cancel);
        }

        return over.Count;
    }

    private static async Task Pay(AppDbContext db, Guid userId, int amount, string detail, CancellationToken cancel)
    {
        CardGameProfile? wallet = await db.CardGameProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancel);

        if (wallet == null)
        {
            return;
        }

        wallet.DuelPoints += amount;
        wallet.Version += 1;

        db.Ledger.Add(new LedgerEntry { UserId = userId, Kind = "guild-war", Detail = detail, Amount = amount, BalanceAfter = wallet.DuelPoints });
    }

    // ------------------------------------------------------------------ what the game shows

    public static async Task<WarInfo> Describe(AppDbContext db, Guid guildId, DateTime now, CancellationToken cancel)
    {
        DateTime next = NextWeekendStart(now);

        GuildWar? war = await db.GuildWars.AsNoTracking()
            .Where(w => w.GuildAId == guildId || w.GuildBId == guildId)
            .OrderByDescending(w => w.StartsUtc)
            .FirstOrDefaultAsync(cancel);

        // The last war only matters while it is going on, or for a few days after, so the result can be seen.
        if (war == null || (war.Status == WarStatus.Finished && now - war.EndsUtc > TimeSpan.FromDays(5)))
        {
            int fighters = await db.GuildMembers.CountAsync(m => m.GuildId == guildId && m.Slot >= 1 && m.Slot <= GuildEndpoints.MainTeamSize, cancel);

            string note = fighters < MinFighters
                ? $"Your guild needs at least {MinFighters} players in the main team to take part in a war. Put players in the main team on the TEAM tab."
                : "A war starts for every guild with a full enough main team each Saturday, and lasts until Monday.";

            return new WarInfo("None", "", "", 0, 0, 0, next, WeekendEnd(next), next, "", new List<WarFighterDto>(), new List<WarFighterDto>(), note);
        }

        bool iAmA = war.GuildAId == guildId;
        Guid opponentId = iAmA ? war.GuildBId : war.GuildAId;
        Guild? opponent = await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == opponentId, cancel);

        List<GuildWarDuel> duels = await db.GuildWarDuels.AsNoTracking().Where(d => d.WarId == war.Id).ToListAsync(cancel);

        async Task<List<WarFighterDto>> Fighters(Guid id)
        {
            var rows = await (from m in db.GuildMembers.AsNoTracking()
                              join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                              where m.GuildId == id && m.Slot >= 1 && m.Slot <= GuildEndpoints.MainTeamSize
                              orderby m.Slot
                              select new { m.UserId, m.Slot, u.Username }).ToListAsync(cancel);

            return rows.Select(r =>
            {
                GuildWarDuel? duel = duels.FirstOrDefault(d => d.UserId == r.UserId);
                return new WarFighterDto(r.Username, r.Slot, duel != null, duel?.Won ?? false);
            }).ToList();
        }

        int mine = iAmA ? war.ScoreA : war.ScoreB;
        int theirs = iAmA ? war.ScoreB : war.ScoreA;
        bool active = war.Status == WarStatus.Active && war.EndsUtc > now;
        string result = active ? "" : war.WinnerGuildId == null ? "Draw" : war.WinnerGuildId == guildId ? "Won" : "Lost";

        string explain = active
            ? "Each main-team player fights ONE Guild Match against the other guild (Online Duels > Guild Match). A win scores a point."
            : "The war is over. The next one starts on Saturday.";

        return new WarInfo(
            active ? "Active" : "Finished",
            opponent?.Name ?? "?",
            opponent?.Tag ?? "",
            opponent?.Rating ?? 0,
            mine,
            theirs,
            war.StartsUtc,
            war.EndsUtc,
            next,
            result,
            await Fighters(guildId),
            await Fighters(opponentId),
            explain);
    }
}
