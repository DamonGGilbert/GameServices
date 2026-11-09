using System.Globalization;
using System.Text.Json;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Progress;

/// <summary>The numbers quests, achievements and medals follow. The same list, in the same order, as the game's StatType.</summary>
public enum ExplorerStat
{
    StepsToday = 0, LifetimeSteps = 1, DailyGoalsCompleted = 2, MonstersOwned = 3, DiscoveriesToday = 4, RareFinds = 5, LegendaryFinds = 6,
    BattlesWon = 7, BattlesWonToday = 8, TradesCompleted = 9, TradesToday = 10, ClanJoined = 11, ClanStepsContributed = 12, AreasVisitedToday = 13,
    AreasVisited = 14, LongestStreak = 15, KilometresWalked = 16, CompanionBestLevel = 17, MonstersFullyTrained = 18, RoutesCompleted = 19,
    ChallengesCompleted = 20, WalksTogetherDays = 21, CompanionBestPower = 22, PlayerLevel = 23,
}

/// <summary>What a reward (or several) came to, so the game can say what you collected.</summary>
public sealed class RewardTally
{
    public int Glimmer { get; set; }
    public int Xp { get; set; }
    public int Fragments { get; set; }
    public int Monsters { get; set; }
    public List<string> Extras { get; } = new();

    public string Describe()
    {
        var parts = new List<string>();
        if (Glimmer != 0) parts.Add("+" + Glimmer + " Glimmer");
        if (Xp != 0) parts.Add("+" + Xp + " XP");
        if (Fragments != 0) parts.Add("+" + Fragments + " fragments");
        if (Monsters != 0) parts.Add("+" + Monsters + " monster" + (Monsters == 1 ? "" : "s"));
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}

/// <summary>
/// AN EXPLORER'S PROGRESS, KEPT BY THE SERVER: their level and experience (which bring monster slots), the daily goal and
/// streak, and every number that quests, achievements and medals follow. This is the rules; ProgressEndpoints is how the
/// game asks. The phone only ever shows what's here.
///
/// The rules themselves are the game's own design data, exported to monster-catalog.json (MonsterCatalog.Progression),
/// so a reward changed in Unity needs exporting again.
///
/// The server's own "today" is the walker's calendar day, worked out from the time zone of their last step report. Every
/// daily limit is kept against a row for that day (WalkingDay), so changing the phone's clock or time zone can't give
/// anyone a second helping: there are only so many days, and each one's row remembers what's been used.
/// </summary>
public static class Explorer
{
    public const double MaxMetresPerStep = 1.2;

    private static MonsterCatalog.ProgressionEntry Rules => MonsterCatalog.Shared.Progression;

    // ------------------------------------------------------------------ the day

    public static string Today(WalkingProfile profile, DateTime nowUtc) =>
        nowUtc.AddMinutes(profile.UtcOffsetMinutes).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>This walker's row for a day, ready to change (a new one is saved with whatever else is being saved).</summary>
    public static async Task<WalkingDay> DayAsync(AppDbContext db, Guid user, string day, CancellationToken cancel)
    {
        WalkingDay? row = db.WalkingDays.Local.FirstOrDefault(d => d.UserId == user && d.Day == day)
                          ?? await db.WalkingDays.FirstOrDefaultAsync(d => d.UserId == user && d.Day == day, cancel);
        if (row != null) return row;

        row = new WalkingDay { UserId = user, Day = day };
        db.WalkingDays.Add(row);
        return row;
    }

    // ------------------------------------------------------------------ levels

    public static int XpForLevel(int level)
    {
        List<int> table = Rules.XpForLevel;
        return level >= 1 && level <= table.Count ? table[level - 1] : int.MaxValue;
    }

    public static int SlotsFor(int level)
    {
        int slots = MonsterSlots.Starting;
        foreach (int l in Rules.SlotLevels) if (level >= l) slots++;
        return Math.Min(slots, MonsterSlots.Most);
    }

    public static void AddXp(AppDbContext db, WalkingProfile profile, int amount, string reason)
    {
        if (amount <= 0) return;
        profile.PlayerXp += amount;
        while (profile.PlayerLevel < Rules.PlayerMaxLevel && profile.PlayerXp >= XpForLevel(profile.PlayerLevel))
        {
            profile.PlayerXp -= XpForLevel(profile.PlayerLevel);
            profile.PlayerLevel++;
            profile.MonsterSlots = SlotsFor(profile.PlayerLevel);
            ActivityLog.Add(db, profile.UserId, "level-up", "Level " + profile.PlayerLevel + " (" + reason + "), " + profile.MonsterSlots + " slots");
        }
    }

    // ------------------------------------------------------------------ rewards

    /// <summary>Pays a reward, writing Glimmer in the ledger like everything else. Monsters are made here too.</summary>
    /// <param name="kind">The ledger's word for where it came from ("milestone", "quest"...).</param>
    /// <param name="detail">Says which ("Milestone: 2,500 steps").</param>
    public static async Task GrantAsync(AppDbContext db, WalkingProfile profile, MonsterFinder finder, IEnumerable<MonsterCatalog.RewardEntry> reward, string kind, string detail,
        string day, DateTime now, RewardTally tally, CancellationToken cancel)
    {
        foreach (MonsterCatalog.RewardEntry entry in reward)
        {
            switch (entry.Type)
            {
                case "Currency":
                    WalkingWallet.Change(db, profile, entry.Amount, kind, detail);
                    tally.Glimmer += entry.Amount;
                    break;

                case "XP":
                    AddXp(db, profile, entry.Amount, detail);
                    tally.Xp += entry.Amount;
                    break;

                case "Monster":
                    if (await MonsterSlots.HasRoomAsync(db, profile.UserId, cancel))
                    {
                        (string species, int tier, long seed, int potential) = finder.Roll(null, entry.MinTier);
                        await MonsterEndpoints.Add(db, profile.UserId, species, tier, seed, potential, MonsterCatalog.Shared.RulesVersion, "reward", null, day, cancel);
                        tally.Monsters++;
                        tally.Extras.Add("A new monster has found you!");
                    }
                    else
                    {
                        WalkingWallet.Change(db, profile, Rules.GlimmerInsteadOfMonster, kind, detail + " - team full, Glimmer instead of a monster");
                        tally.Glimmer += Rules.GlimmerInsteadOfMonster;
                        tally.Extras.Add("Your team is full, so you got Glimmer instead of a monster");
                    }
                    break;

                case "Pack":
                    // Packs are gone (you keep a small group of monsters): an old pack reward is Glimmer now.
                    int glimmer = Rules.GlimmerPerPack * Math.Max(1, entry.Amount);
                    WalkingWallet.Change(db, profile, glimmer, kind, detail);
                    tally.Glimmer += glimmer;
                    break;

                case "Cosmetic":
                    profile.Cosmetics = WithItem(profile.Cosmetics, entry.ItemId);
                    tally.Extras.Add("Unlocked cosmetic: " + entry.ItemId);
                    break;

                case "EncounterBoost":
                    DateTime from = profile.BoostUntilUtc > now ? profile.BoostUntilUtc.Value : now;
                    profile.BoostUntilUtc = from.AddMinutes(entry.Amount);
                    profile.BoostMultiplier = Math.Max(entry.Multiplier, profile.BoostUntilUtc > now && from > now ? profile.BoostMultiplier : 1f);
                    tally.Extras.Add("Discovery boost x" + entry.Multiplier.ToString("0.0", CultureInfo.InvariantCulture) + " for " + entry.Amount + " min");
                    break;

                case "CollectionItem":
                    profile.Fragments += entry.Amount;
                    tally.Fragments += entry.Amount;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ walking

    /// <summary>
    /// A step report's steps have been accepted: walking experience, the daily goal and streak, and how far (the phone
    /// measures distance and new areas, and the server believes as much as those steps can explain - no more).
    /// No position ever comes here: areas are only a count.
    /// </summary>
    public static async Task OnStepsAsync(AppDbContext db, MonsterFinder finder, WalkingProfile profile, WalkingDay row, int added, double metres, int areasToday, int newAreas,
        DateTime now, List<string> notices, CancellationToken cancel)
    {
        int before = row.Steps - added;

        // Walking experience, up to a daily limit on the steps that count.
        int cap = Rules.DailyRewardStepCap, perXp = Math.Max(1, Rules.StepsPerXp);
        AddXp(db, profile, Math.Min(row.Steps, cap) / perXp - Math.Min(before, cap) / perXp, "Walking");

        // Distance: never more than a long stride for every step.
        int metresToday = (int)Math.Min(Math.Max(0, metres), row.Steps * MaxMetresPerStep);
        if (metresToday > row.Metres)
        {
            profile.LifetimeMetres += metresToday - row.Metres;
            row.Metres = metresToday;
        }

        // New places: a count only, and no more than the steps could have reached.
        int mostAreas = 1 + row.Steps / 50;
        if (areasToday > row.Areas) row.Areas = Math.Min(areasToday, mostAreas);
        profile.AreasVisited += Math.Clamp(newAreas, 0, 1 + added / 50);

        if (added > 0 && await db.GuildMembers.AnyAsync(m => m.UserId == profile.UserId && m.GameId == GameIds.Walking, cancel))
        {
            profile.ClanSteps += added;
        }

        if (!row.GoalCompleted && row.Steps >= Rules.DailyGoalSteps)
        {
            row.GoalCompleted = true;
            profile.DailyGoalsCompleted++;
            ActivityLog.Add(db, profile.UserId, "daily-goal", row.Day + ": " + row.Steps + " steps");
            await CountStreakAsync(db, profile, finder, row.Day, now, notices, cancel);
        }
    }

    // The same forgiving rules as the game's StreakCalculator: a missed day can use up a rest day, and rest days refill weekly.
    private static async Task CountStreakAsync(AppDbContext db, WalkingProfile profile, MonsterFinder finder, string day, DateTime now, List<string> notices, CancellationToken cancel)
    {
        if (!ApplyStreak(profile, day)) return;

        foreach (MonsterCatalog.StreakRewardEntry entry in Rules.StreakRewards)
        {
            if (entry.Days != profile.CurrentStreak) continue;
            var tally = new RewardTally();
            await GrantAsync(db, profile, finder, entry.Reward, "streak", entry.Days + "-day streak", day, now, tally, cancel);
            notices.Add(entry.Days + "-day streak reward: " + tally.Describe() + "!");
            ActivityLog.Add(db, profile.UserId, "streak-reward", entry.Days + " days: " + tally.Describe());
        }
    }

    /// <summary>Counts today's goal in the streak. False if it didn't move on (already counted, or an older day).</summary>
    public static bool ApplyStreak(WalkingProfile profile, string day)
    {
        string week = WeekKey(day);
        if (profile.RestDaysWeek != week)
        {
            profile.RestDaysWeek = week;
            profile.RestDays = Math.Min(2, profile.RestDays + Rules.RestDaysPerWeek);
        }

        if (profile.LastGoalDay == day) return false;

        if (string.IsNullOrEmpty(profile.LastGoalDay) || profile.CurrentStreak <= 0)
        {
            profile.CurrentStreak = 1;
        }
        else
        {
            int days = DaysBetween(profile.LastGoalDay, day);
            if (days < 1) return false;   // a day older than the last one counted: the streak is counted in date order

            int missed = days - 1;
            if (missed == 0) profile.CurrentStreak++;
            else if (missed <= profile.RestDays)
            {
                profile.RestDays -= missed;
                profile.CurrentStreak++;
            }
            else
            {
                profile.CurrentStreak = 1;
            }
        }

        profile.LastGoalDay = day;
        profile.LongestStreak = Math.Max(profile.LongestStreak, profile.CurrentStreak);
        return true;
    }

    public static string WeekKey(string day)
    {
        DateTime date = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        int week = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        return date.Year.ToString(CultureInfo.InvariantCulture) + "-W" + week.ToString("00", CultureInfo.InvariantCulture);
    }

    public static int DaysBetween(string a, string b) =>
        (int)Math.Round((DateTime.ParseExact(b, "yyyy-MM-dd", CultureInfo.InvariantCulture) - DateTime.ParseExact(a, "yyyy-MM-dd", CultureInfo.InvariantCulture)).TotalDays);

    // ------------------------------------------------------------------ things that happen elsewhere

    /// <summary>A battle against a wild monster or a trainer was fought here: experience, and it counts.</summary>
    public static async Task OnBattleAsync(AppDbContext db, WalkingProfile profile, bool won, DateTime now, CancellationToken cancel)
    {
        AddXp(db, profile, won ? MonsterCatalog.Shared.Battle.PlayerXpPerWin : MonsterCatalog.Shared.Battle.PlayerXpPerLoss, "Battle");
        if (!won)
        {
            profile.BattlesLost++;
            return;
        }

        profile.BattlesWon++;
        await CountWinTodayAsync(db, profile, now, cancel);
    }

    /// <summary>A win counts for today's quests (ranked wins do this too; they're already in the walker's record).</summary>
    public static async Task CountWinTodayAsync(AppDbContext db, WalkingProfile profile, DateTime now, CancellationToken cancel) =>
        (await DayAsync(db, profile.UserId, Today(profile, now), cancel)).BattlesWon++;

    /// <summary>A monster has joined its walker, by whatever way: a first of its kind earns experience.</summary>
    public static void OnMonsterArrived(AppDbContext db, WalkingProfile profile, string speciesId, int tier, string how)
    {
        if (!HasItem(profile.DiscoveredSpecies, speciesId))
        {
            profile.DiscoveredSpecies = WithItem(profile.DiscoveredSpecies, speciesId);
            AddXp(db, profile, Rules.XpPerNewSpecies, "New species");
        }

        if (how != "trade")
        {
            if (tier >= 2) profile.RareFinds++;
            if (tier >= 4) profile.LegendaryFinds++;
        }

        ActivityLog.Add(db, profile.UserId, "monster", how + ": " + speciesId + ", rarity " + tier);
    }

    /// <summary>A monster changed hands between two walkers (the Trader doesn't count): experience and a trade for them both.</summary>
    public static async Task OnTradeAsync(AppDbContext db, Guid from, Guid to, WalkingMonster monster, DateTime now, CancellationToken cancel)
    {
        foreach (Guid user in new[] { from, to })
        {
            WalkingProfile profile = db.WalkingProfiles.Local.FirstOrDefault(p => p.UserId == user) ?? await WalkingEndpoints.GetOrCreateProfile(db, user, cancel);
            profile.TradesCompleted++;
            (await DayAsync(db, user, Today(profile, now), cancel)).Trades++;
            AddXp(db, profile, Rules.XpPerTrade, "Trade");
            if (user == to) OnMonsterArrived(db, profile, monster.SpeciesId, monster.RarityTier, "trade");
            else ActivityLog.Add(db, user, "monster", "traded away: " + monster.SpeciesId + ", rarity " + monster.RarityTier);
        }
    }

    // ------------------------------------------------------------------ the numbers

    public static async Task<Dictionary<ExplorerStat, long>> StatsAsync(AppDbContext db, WalkingProfile profile, WalkingDay row, CancellationToken cancel)
    {
        var monsters = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == profile.UserId).ToListAsync(cancel);
        bool inGuild = await db.GuildMembers.AsNoTracking().AnyAsync(m => m.UserId == profile.UserId && m.GameId == GameIds.Walking, cancel);

        int bestLevel = 0, bestPower = 0, fullyTrained = 0;
        foreach (WalkingMonster m in monsters)
        {
            bestLevel = Math.Max(bestLevel, m.Level);
            bestPower = Math.Max(bestPower, PowerBattles.PowerOf(RankedEndpoints.StatsOf(MonsterCatalog.Shared, m)));
            if (m.TrainedPower + m.TrainedGuard + m.TrainedTricks + m.TrainedWits >= MostTraining(m)) fullyTrained++;
        }

        return new Dictionary<ExplorerStat, long>
        {
            [ExplorerStat.StepsToday] = row.Steps,
            [ExplorerStat.LifetimeSteps] = profile.LifetimeSteps + profile.StartingSteps,
            [ExplorerStat.DailyGoalsCompleted] = profile.DailyGoalsCompleted,
            [ExplorerStat.MonstersOwned] = monsters.Count,
            [ExplorerStat.DiscoveriesToday] = row.Discoveries,
            [ExplorerStat.RareFinds] = profile.RareFinds,
            [ExplorerStat.LegendaryFinds] = profile.LegendaryFinds,
            [ExplorerStat.BattlesWon] = profile.BattlesWon + profile.RankedWins,
            [ExplorerStat.BattlesWonToday] = row.BattlesWon,
            [ExplorerStat.TradesCompleted] = profile.TradesCompleted,
            [ExplorerStat.TradesToday] = row.Trades,
            [ExplorerStat.ClanJoined] = inGuild ? 1 : 0,
            [ExplorerStat.ClanStepsContributed] = profile.ClanSteps,
            [ExplorerStat.AreasVisitedToday] = row.Areas,
            [ExplorerStat.AreasVisited] = profile.AreasVisited,
            [ExplorerStat.LongestStreak] = profile.LongestStreak,
            [ExplorerStat.KilometresWalked] = profile.LifetimeMetres / 1000,
            [ExplorerStat.CompanionBestLevel] = bestLevel,
            [ExplorerStat.MonstersFullyTrained] = fullyTrained,
            [ExplorerStat.RoutesCompleted] = profile.RoutesCompleted,
            [ExplorerStat.ChallengesCompleted] = profile.ChallengesCompleted,
            [ExplorerStat.WalksTogetherDays] = profile.TogetherDaysTotal,
            [ExplorerStat.CompanionBestPower] = bestPower,
            [ExplorerStat.PlayerLevel] = profile.PlayerLevel,
        };
    }

    public static int MostTraining(WalkingMonster m) => 10 + (int)Math.Round(m.Potential * 0.4);

    // ------------------------------------------------------------------ achievements and medals

    /// <summary>Climbs every medal ladder and unlocks every achievement the numbers now reach. Nothing's paid until it's collected.</summary>
    /// <param name="alreadyPaid">The numbers came across from the phone (ProgressEndpoints.Start): count them, but they pay nothing.</param>
    public static async Task RefreshAsync(AppDbContext db, WalkingProfile profile, Dictionary<ExplorerStat, long> stats, DateTime now, CancellationToken cancel, bool alreadyPaid = false)
    {
        Dictionary<string, WalkingAchievement> achievements = await db.WalkingAchievements.Where(a => a.UserId == profile.UserId).ToDictionaryAsync(a => a.AchievementId, cancel);
        foreach (WalkingAchievement a in db.WalkingAchievements.Local.Where(a => a.UserId == profile.UserId)) achievements[a.AchievementId] = a;

        foreach (MonsterCatalog.AchievementEntry def in Rules.Achievements)
        {
            achievements.TryGetValue(def.Id, out WalkingAchievement? row);
            if (row is { Unlocked: true } || stats.GetValueOrDefault((ExplorerStat)def.Stat) < def.Target) continue;

            if (row == null)
            {
                row = new WalkingAchievement { UserId = profile.UserId, AchievementId = def.Id };
                db.WalkingAchievements.Add(row);
            }

            row.Unlocked = true;
            row.UnlockedUtc = now;
            if (!def.HasReward || alreadyPaid) row.Claimed = true;
            if (!string.IsNullOrEmpty(def.Badge)) profile.Badges = WithItem(profile.Badges, def.Badge);
            if (!string.IsNullOrEmpty(def.Title)) profile.Titles = WithItem(profile.Titles, def.Title);
            ActivityLog.Add(db, profile.UserId, "achievement", "Unlocked " + def.Id + (alreadyPaid ? " (came from the phone)" : ""));
        }

        Dictionary<string, WalkingMedal> medals = await db.WalkingMedals.Where(m => m.UserId == profile.UserId).ToDictionaryAsync(m => m.TrackId, cancel);
        foreach (WalkingMedal m in db.WalkingMedals.Local.Where(m => m.UserId == profile.UserId)) medals[m.TrackId] = m;

        foreach (MonsterCatalog.MedalEntry track in Rules.Medals)
        {
            int reached = track.Reached(stats.GetValueOrDefault((ExplorerStat)track.Stat));
            medals.TryGetValue(track.Id, out WalkingMedal? row);
            if (row == null && reached == 0 && !alreadyPaid) continue;

            if (row == null)
            {
                row = new WalkingMedal { UserId = profile.UserId, TrackId = track.Id };
                db.WalkingMedals.Add(row);
            }

            if (reached > row.Reached)
            {
                long unix = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
                for (int rung = row.Reached; rung < reached; rung++)
                {
                    row.EarnedUnix = WithItem(row.EarnedUnix, unix + ":" + rung);
                    if (rung >= Rules.MedalTitleFromRung) profile.Titles = WithItem(profile.Titles, TierName(rung) + " " + track.Noun);
                }
                ActivityLog.Add(db, profile.UserId, "medal", track.Id + " reached " + TierName(reached - 1) + (alreadyPaid ? " (came from the phone)" : ""));
                row.Reached = reached;
            }

            if (alreadyPaid) row.Claimed = Math.Max(row.Claimed, row.Reached);
        }
    }

    /// <summary>The frames this walker has earned for their profile picture, by the game's own rules.</summary>
    public static async Task<List<string>> EarnedFramesAsync(AppDbContext db, WalkingProfile profile, CancellationToken cancel)
    {
        List<string> unlocked = await db.WalkingAchievements.AsNoTracking().Where(a => a.UserId == profile.UserId && a.Unlocked).Select(a => a.AchievementId).ToListAsync(cancel);
        int bestMedals = await db.WalkingMedals.AsNoTracking().Where(m => m.UserId == profile.UserId).Select(m => (int?)m.Reached).MaxAsync(cancel) ?? 0;

        return Rules.Frames.Where(f =>
                (f.Cosmetic.Length > 0 && HasItem(profile.Cosmetics, f.Cosmetic)) ||
                (f.Level > 0 && profile.PlayerLevel >= f.Level) ||
                (f.Achievement.Length > 0 && unlocked.Contains(f.Achievement)) ||
                (f.MedalRung >= 0 && bestMedals > f.MedalRung))
            .Select(f => f.Id).ToList();
    }

    public static int MedalReward(int rung) => Math.Min(Rules.MedalRewardMost, Rules.MedalRewardFirst + Rules.MedalRewardPerRung * rung);

    /// <summary>Bronze, Silver... Legend, then Legend II, Legend III... for ever.</summary>
    public static string TierName(int rung)
    {
        List<string> tiers = Rules.MedalTiers;
        return rung < tiers.Count ? tiers[rung] : "Legend " + Roman(rung - tiers.Count + 2);
    }

    private static string Roman(int n)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((int value, string letters) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            while (n >= value)
            {
                sb.Append(letters);
                n -= value;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ today's quests

    public sealed class QuestEntry
    {
        public string Id { get; set; } = "";
        public long Baseline { get; set; }
        public bool Claimed { get; set; }
    }

    private static readonly HashSet<ExplorerStat> TodayStats = new()
    {
        ExplorerStat.StepsToday, ExplorerStat.DiscoveriesToday, ExplorerStat.BattlesWonToday, ExplorerStat.TradesToday, ExplorerStat.AreasVisitedToday,
    };

    public static List<QuestEntry> Quests(WalkingDay row) =>
        string.IsNullOrEmpty(row.Quests) ? new List<QuestEntry>() : JsonSerializer.Deserialize<List<QuestEntry>>(row.Quests) ?? new List<QuestEntry>();

    public static void SaveQuests(WalkingDay row, List<QuestEntry> quests) => row.Quests = JsonSerializer.Serialize(quests);

    /// <summary>Today's quests: those always offered, then a few from the pool. A number that only ever grows starts from where it is today.</summary>
    public static List<QuestEntry> EnsureQuests(WalkingDay row, Dictionary<ExplorerStat, long> stats)
    {
        List<QuestEntry> quests = Quests(row);
        if (quests.Count > 0) return quests;

        List<MonsterCatalog.QuestEntry> pool = Rules.Quests.Where(q => !q.Always).ToList();
        var picked = Rules.Quests.Where(q => q.Always).ToList();
        while (picked.Count < Rules.DailyQuestCount && pool.Count > 0)
        {
            double roll = Random.Shared.NextDouble() * pool.Sum(q => q.Weight);
            MonsterCatalog.QuestEntry pick = pool[^1];
            foreach (MonsterCatalog.QuestEntry q in pool)
            {
                roll -= q.Weight;
                if (roll > 0) continue;
                pick = q;
                break;
            }
            pool.Remove(pick);
            picked.Add(pick);
        }

        foreach (MonsterCatalog.QuestEntry q in picked)
        {
            ExplorerStat stat = (ExplorerStat)q.Stat;
            quests.Add(new QuestEntry { Id = q.Id, Baseline = TodayStats.Contains(stat) ? 0 : stats.GetValueOrDefault(stat) });
        }

        SaveQuests(row, quests);
        return quests;
    }

    // ------------------------------------------------------------------ small lists kept on a walker's row

    public static List<string> Items(string list) => string.IsNullOrEmpty(list) ? new List<string>() : list.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    public static bool HasItem(string list, string item) => Items(list).Contains(item);

    public static string WithItem(string list, string item)
    {
        if (string.IsNullOrWhiteSpace(item) || HasItem(list, item)) return list;
        return string.IsNullOrEmpty(list) ? item : list + "\n" + item;
    }
}
