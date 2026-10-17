using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Catalog;
using GameServices.Api.Chat;
using GameServices.Api.Data;
using GameServices.Api.Economy;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Guilds;

public record CreateGuildRequest(string Name, string Tag, string? Description, bool IsOpen);

public record GuildSummary(Guid Id, string Name, string Tag, string Description, bool IsOpen, int Members, int Rating = 1000, int Tier = 0, string TierName = "Rookie", int MaxMembers = 30);

public record MemberDto(Guid UserId, string Username, string Role, bool IsOnline, int Slot, ShowcaseDto Showcase);

public record LineupRequest(Guid UserId, int Slot);

public record LeaderboardEntry(string Username, int Damage, int Attacks);

public record BossInfo(Guid Id, string Key, string Name, int MaxHp, int CurrentHp, string Status, DateTime EndsUtc, int RewardPool, int MyAttacksLeft, List<LeaderboardEntry> Leaderboard);

public record GuildDetail(Guid Id, string Name, string Tag, string Description, bool IsOpen, string MyRole, List<MemberDto> Members, BossInfo? Boss, DateTime? NextNameChangeUtc, int Rating = 1000, int Tier = 0, string TierName = "Rookie", int WarWins = 0, int WarLosses = 0, int BannerStyle = 0, int MaxMembers = 30);

public record UpdateGuildRequest(string? Name, string? Tag, string? Description, bool? IsOpen, int? BannerStyle = null);

public record InviteDto(Guid GuildId, string GuildName, string GuildTag, string InvitedBy, DateTime ExpiresUtc);

public record StartBossRequest(string BossKey);

public record BossChoice(string Key, string Name, string Description, int RewardPool, int DurationHours, int HpForYourGuild);

public record AttackResult(int Damage, int BossHp, bool Defeated, int AttacksLeft, int RewardDp);

public record GuildMessageDto(long Id, Guid UserId, string Username, string Text, DateTime SentUtc);

/// <summary>
/// GUILDS: groups of players with their own chat and shared boss battles.
///
///   GET  /api/guilds?q=            find guilds        POST /api/guilds             create one
///   GET  /api/guilds/mine          my guild + members + boss
///   POST /api/guilds/{id}/join     join an open guild   POST /api/guilds/leave
///   POST /api/guilds/members/{id}/promote | demote | kick | transfer
///   GET/POST /api/guilds/invites   (and /{guildId}/accept | decline)
///   GET/POST /api/guilds/chat      guild chat
///   GET /api/guilds/bosses   GET/POST /api/guilds/boss   POST /api/guilds/boss/attack
///
/// Roles: LEADER (one per guild) can do everything. OFFICERS invite, kick members and summon bosses.
/// MEMBERS chat and fight. Every rule is checked here on the server, never trusted from the game.
///
/// PER GAME: every request says which game it comes from (the X-Game-Id header, see GameIds). A player sees, joins
/// and runs only that game's guilds, and can be in one guild in each game. Bosses, line-ups and Guild Wars are
/// card-game features (they use cards and Duel Points); the walking game has its own group goals.
/// </summary>
public static partial class GuildEndpoints
{
    /// <summary>The card game's member limit. Use GameIds.MaxGuildMembers(gameId) - the walking game allows 10.</summary>
    public const int MaxMembers = 30;
    public const int MainTeamSize = 5;
    public const int BenchSize = 3;
    public const int LineupSlots = MainTeamSize + BenchSize;
    public const int MaxAttacksPerDay = 3;
    public const int MaxMessageLength = 500;
    public const int MaxMessagesPer10Seconds = 10;
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(3);
    public static readonly TimeSpan NameChangeCooldown = TimeSpan.FromDays(14);

    [GeneratedRegex("^[A-Za-z0-9 ]{3,20}$")]
    private static partial Regex NameRule();

    [GeneratedRegex("^[A-Za-z0-9]{2,4}$")]
    private static partial Regex TagRule();

    public static IEndpointRouteBuilder MapGuildEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/guilds").WithTags("Guilds").RequireAuthorization();

        group.MapGet("", Search);
        group.MapPost("", Create);
        group.MapGet("/mine", Mine);
        group.MapPost("/{guildId:guid}/join", Join);
        group.MapPost("/leave", Leave);
        group.MapPut("/details", UpdateDetails);
        group.MapGet("/war", War);

        group.MapPost("/members/{userId:guid}/promote", (Guid userId, HttpContext h, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => ChangeRole(userId, GuildRole.Officer, h, p, db, c));
        group.MapPost("/members/{userId:guid}/demote", (Guid userId, HttpContext h, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => ChangeRole(userId, GuildRole.Member, h, p, db, c));
        group.MapPost("/members/{userId:guid}/kick", Kick);
        group.MapPost("/members/{userId:guid}/transfer", Transfer);
        group.MapPost("/lineup", SetLineup);

        group.MapGet("/invites", MyInvites);
        group.MapPost("/invites", Invite);
        group.MapPost("/invites/{guildId:guid}/accept", AcceptInvite);
        group.MapPost("/invites/{guildId:guid}/decline", DeclineInvite);

        group.MapGet("/chat", ChatHistory);
        group.MapPost("/chat", ChatSend);

        group.MapGet("/bosses", BossChoices);
        group.MapGet("/boss", CurrentBoss);
        group.MapPost("/boss", StartBoss);
        group.MapPost("/boss/attack", Attack);

        return app;
    }

    // ------------------------------------------------------------------ find / create

    private static async Task<IResult> Search(string? q, HttpContext http, AppDbContext db, CancellationToken cancel)
    {
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        string needle = (q ?? "").Trim().ToUpperInvariant();

        // Only this game's guilds.
        IQueryable<Guild> guilds = db.Guilds.AsNoTracking().Where(g => g.GameId == game);

        if (needle.Length > 0)
        {
            guilds = guilds.Where(g => g.NormalizedName.Contains(needle) || g.Tag.ToUpper() == needle);
        }

        List<Guild> found = await guilds.OrderBy(g => g.Name).Take(200).ToListAsync(cancel);
        List<Guid> ids = found.Select(g => g.Id).ToList();

        Dictionary<Guid, int> counts = await db.GuildMembers.AsNoTracking()
            .Where(m => ids.Contains(m.GuildId))
            .GroupBy(m => m.GuildId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancel);

        List<GuildSummary> result = found
            .Select(g => new GuildSummary(g.Id, g.Name, g.Tag, g.Description, g.IsOpen, counts.GetValueOrDefault(g.Id), g.Rating, GuildTiers.TierOf(g.Rating, g.WarWins), GuildTiers.NameOf(GuildTiers.TierOf(g.Rating, g.WarWins)), GameIds.MaxGuildMembers(game)))
            .OrderByDescending(g => g.Members).ThenBy(g => g.Name)
            .Take(50)
            .ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> Create(CreateGuildRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        string name = (request?.Name ?? "").Trim();
        string tag = (request?.Tag ?? "").Trim();
        string description = (request?.Description ?? "").Trim();
        Dictionary<string, string[]> problems = new();

        if (!NameRule().IsMatch(name)) problems["name"] = new[] { "Guild names are 3-20 letters, numbers or spaces." };
        if (!TagRule().IsMatch(tag)) problems["tag"] = new[] { "The tag is 2-4 letters or numbers." };
        if (description.Length > 140) problems["description"] = new[] { "The description can be at most 140 characters." };

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        if (await FindMembership(db, me, game, cancel) != null)
        {
            return Results.Problem("You're already in a guild. Leave it first.", statusCode: StatusCodes.Status409Conflict);
        }

        string normalized = name.ToUpperInvariant();

        if (await db.Guilds.AnyAsync(g => g.GameId == game && g.NormalizedName == normalized, cancel))
        {
            return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
        }

        Guild guild = new() { GameId = game, Name = name, NormalizedName = normalized, Tag = tag.ToUpperInvariant(), Description = description, IsOpen = request!.IsOpen };
        db.Guilds.Add(guild);
        db.GuildMembers.Add(new GuildMember { GuildId = guild.Id, UserId = me, GameId = game, Role = GuildRole.Leader, Slot = 1 });

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new GuildSummary(guild.Id, guild.Name, guild.Tag, guild.Description, guild.IsOpen, 1, MaxMembers: GameIds.MaxGuildMembers(game)));
    }

    private static async Task<IResult> Mine(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status404NotFound);
        }

        await Presence.Touch(db, me, cancel);

        Guild guild = await db.Guilds.AsNoTracking().FirstAsync(g => g.Id == membership.GuildId, cancel);
        DateTime now = DateTime.UtcNow;

        await EnsureLineup(db, guild.Id, cancel);

        var rows = await (from m in db.GuildMembers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.GuildId == guild.Id
                          select new { m.Role, m.UserId, m.Slot, u.Username, u.LastSeenUtc, u.ShowLevel, u.ShowPrestige, u.ShowEmblem, u.ShowFrame, u.ShowTitle }).ToListAsync(cancel);

        List<MemberDto> members = rows
            .OrderByDescending(x => x.Role).ThenBy(x => x.Username)
            .Select(x => new MemberDto(x.UserId, x.Username, x.Role.ToString(), x.LastSeenUtc != null && now - x.LastSeenUtc.Value < SocialEndpoints.OnlineWindow, x.Slot, new ShowcaseDto(x.ShowLevel, x.ShowPrestige, x.ShowEmblem, x.ShowFrame, x.ShowTitle)))
            .ToList();

        // Bosses are a card-game feature.
        BossInfo? boss = game == GameIds.Yugioh ? await BuildBossInfo(db, guild.Id, me, cancel) : null;

        DateTime? nextNameChange = guild.NameChangedUtc != null && guild.NameChangedUtc.Value + NameChangeCooldown > now
            ? guild.NameChangedUtc.Value + NameChangeCooldown
            : null;

        int tier = GuildTiers.TierOf(guild.Rating, guild.WarWins);

        return Results.Ok(new GuildDetail(guild.Id, guild.Name, guild.Tag, guild.Description, guild.IsOpen, membership.Role.ToString(), members, boss, nextNameChange,
            guild.Rating, tier, GuildTiers.NameOf(tier), guild.WarWins, guild.WarLosses, guild.BannerStyle, GameIds.MaxGuildMembers(game)));
    }

    // ------------------------------------------------------------------ guild details (leader only)

    /// <summary>
    /// The leader can change the description and the open/invite-only setting any time. Changing the NAME or TAG
    /// is limited to once every 14 days, so a guild's identity can't be swapped back and forth to dodge a bad name.
    /// Only the fields that are sent are changed.
    /// </summary>
    private static async Task<IResult> UpdateDetails(UpdateGuildRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role != GuildRole.Leader)
        {
            return Results.Problem("Only the guild leader can change the guild's details.", statusCode: StatusCodes.Status403Forbidden);
        }

        Guild guild = await db.Guilds.FirstAsync(g => g.Id == mine.GuildId, cancel);
        DateTime now = DateTime.UtcNow;
        Dictionary<string, string[]> problems = new();

        string? newName = request?.Name?.Trim();
        string? newTag = request?.Tag?.Trim();
        string? newDescription = request?.Description?.Trim();

        bool nameChanges = newName != null && newName != guild.Name;
        bool tagChanges = newTag != null && !string.Equals(newTag, guild.Tag, StringComparison.OrdinalIgnoreCase);

        if (nameChanges && !NameRule().IsMatch(newName!)) problems["name"] = new[] { "Guild names are 3-20 letters, numbers or spaces." };
        if (tagChanges && !TagRule().IsMatch(newTag!)) problems["tag"] = new[] { "The tag is 2-4 letters or numbers." };
        if (newDescription != null && newDescription.Length > 140) problems["description"] = new[] { "The description can be at most 140 characters." };

        // Banner styles are unlocked by the guild's tier (its rating and war wins).
        if (request?.BannerStyle != null)
        {
            int tier = GuildTiers.TierOf(guild.Rating, guild.WarWins);

            if (request.BannerStyle < 0 || request.BannerStyle > tier)
            {
                problems["bannerStyle"] = new[] { $"Your guild is {GuildTiers.NameOf(tier)} tier and hasn't unlocked that banner yet." };
            }
        }

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        if ((nameChanges || tagChanges) && guild.NameChangedUtc != null && guild.NameChangedUtc.Value + NameChangeCooldown > now)
        {
            TimeSpan left = guild.NameChangedUtc.Value + NameChangeCooldown - now;
            return Results.Problem($"The name and tag can only be changed every 14 days. Try again in {(int)Math.Ceiling(left.TotalDays)} day(s).", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (nameChanges)
        {
            string normalized = newName!.ToUpperInvariant();

            if (await db.Guilds.AnyAsync(g => g.GameId == guild.GameId && g.NormalizedName == normalized && g.Id != guild.Id, cancel))
            {
                return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
            }

            guild.Name = newName;
            guild.NormalizedName = normalized;
        }

        if (tagChanges)
        {
            guild.Tag = newTag!.ToUpperInvariant();
        }

        if (nameChanges || tagChanges)
        {
            guild.NameChangedUtc = now;
        }

        if (newDescription != null)
        {
            guild.Description = newDescription;
        }

        if (request?.IsOpen != null)
        {
            guild.IsOpen = request.IsOpen.Value;
        }

        if (request?.BannerStyle != null)
        {
            guild.BannerStyle = request.BannerStyle.Value;
        }

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
        }

        int count = await db.GuildMembers.CountAsync(m => m.GuildId == guild.Id, cancel);
        return Results.Ok(new GuildSummary(guild.Id, guild.Name, guild.Tag, guild.Description, guild.IsOpen, count, MaxMembers: GameIds.MaxGuildMembers(game)));
    }

    // ------------------------------------------------------------------ guild war

    private static async Task<IResult> War(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(await GuildWars.Describe(db, membership.GuildId, DateTime.UtcNow, cancel));
    }

    // ------------------------------------------------------------------ joining / leaving

    private static async Task<IResult> Join(Guid guildId, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        // A guild from another game looks exactly like one that doesn't exist.
        Guild? guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId && g.GameId == game, cancel);

        if (guild == null)
        {
            return Results.Problem("That guild doesn't exist.", statusCode: StatusCodes.Status404NotFound);
        }

        if (!guild.IsOpen)
        {
            return Results.Problem("This guild is invite-only.", statusCode: StatusCodes.Status403Forbidden);
        }

        return await AddMember(db, guild, me, cancel);
    }

    private static async Task<IResult> AddMember(AppDbContext db, Guild guild, Guid userId, CancellationToken cancel)
    {
        if (await FindMembership(db, userId, guild.GameId, cancel) != null)
        {
            return Results.Problem("You're already in a guild. Leave it first.", statusCode: StatusCodes.Status409Conflict);
        }

        int maxMembers = GameIds.MaxGuildMembers(guild.GameId);

        if (await db.GuildMembers.CountAsync(m => m.GuildId == guild.Id, cancel) >= maxMembers)
        {
            return Results.Problem($"That guild is full ({maxMembers} members).", statusCode: StatusCodes.Status409Conflict);
        }

        db.GuildMembers.Add(new GuildMember { GuildId = guild.Id, UserId = userId, GameId = guild.GameId, Role = GuildRole.Member, Slot = await FirstFreeSlot(db, guild.Id, cancel) });

        // Joining uses up any invites you were holding for guilds in THIS game (invites from the other game stay).
        IQueryable<Guid> sameGameGuilds = db.Guilds.Where(g => g.GameId == guild.GameId).Select(g => g.Id);
        await db.GuildInvites.Where(i => i.UserId == userId && sameGameGuilds.Contains(i.GuildId)).ExecuteDeleteAsync(cancel);

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("You're already in a guild.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> Leave(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status404NotFound);
        }

        if (membership.Role == GuildRole.Leader)
        {
            int others = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId && m.UserId != me, cancel);

            if (others > 0)
            {
                return Results.Problem("Hand leadership to someone else before you leave.", statusCode: StatusCodes.Status409Conflict);
            }

            // Last member out closes the guild (its chat, invites and bosses go with it).
            await db.Guilds.Where(g => g.Id == membership.GuildId).ExecuteDeleteAsync(cancel);
            return Results.NoContent();
        }

        db.GuildMembers.Remove(membership);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ roles

    private static async Task<IResult> ChangeRole(Guid userId, GuildRole newRole, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role != GuildRole.Leader)
        {
            return Results.Problem("Only the guild leader can do that.", statusCode: StatusCodes.Status403Forbidden);
        }

        GuildMember? target = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == mine.GuildId && m.UserId == userId, cancel);

        if (target == null || target.UserId == me)
        {
            return Results.Problem("That player isn't another member of your guild.", statusCode: StatusCodes.Status404NotFound);
        }

        target.Role = newRole;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Kick(Guid userId, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role < GuildRole.Officer)
        {
            return Results.Problem("Only officers and the leader can remove members.", statusCode: StatusCodes.Status403Forbidden);
        }

        GuildMember? target = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == mine.GuildId && m.UserId == userId, cancel);

        if (target == null || target.UserId == me)
        {
            return Results.Problem("That player isn't another member of your guild.", statusCode: StatusCodes.Status404NotFound);
        }

        // You can only remove people ranked BELOW you.
        if (target.Role >= mine.Role)
        {
            return Results.Problem("You can't remove someone of your rank or higher.", statusCode: StatusCodes.Status403Forbidden);
        }

        db.GuildMembers.Remove(target);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Transfer(Guid userId, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role != GuildRole.Leader)
        {
            return Results.Problem("Only the guild leader can hand over leadership.", statusCode: StatusCodes.Status403Forbidden);
        }

        GuildMember? target = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == mine.GuildId && m.UserId == userId, cancel);

        if (target == null || target.UserId == me)
        {
            return Results.Problem("That player isn't another member of your guild.", statusCode: StatusCodes.Status404NotFound);
        }

        target.Role = GuildRole.Leader;
        mine.Role = GuildRole.Officer;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ line-up

    // The first empty place in the line-up (main team first, then the bench), or 0 (reserve) if it is full.
    private static async Task<int> FirstFreeSlot(AppDbContext db, Guid guildId, CancellationToken cancel)
    {
        List<int> taken = await db.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId && m.Slot > 0).Select(m => m.Slot).ToListAsync(cancel);

        for (int slot = 1; slot <= LineupSlots; slot++)
        {
            if (!taken.Contains(slot))
            {
                return slot;
            }
        }

        return 0;
    }

    // Guilds made before line-ups existed have nobody placed: fill it in (leader, officers, then oldest members).
    private static async Task EnsureLineup(AppDbContext db, Guid guildId, CancellationToken cancel)
    {
        List<GuildMember> members = await db.GuildMembers.Where(m => m.GuildId == guildId).ToListAsync(cancel);

        if (members.Count == 0 || members.Any(m => m.Slot > 0))
        {
            return;
        }

        int slot = 1;

        foreach (GuildMember member in members.OrderByDescending(m => m.Role).ThenBy(m => m.JoinedUtc).Take(LineupSlots))
        {
            member.Slot = slot++;
        }

        await db.SaveChangesAsync(cancel);
    }

    // Leader and officers choose who plays where. Putting someone into a taken place swaps the two.
    private static async Task<IResult> SetLineup(LineupRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role == GuildRole.Member)
        {
            return Results.Problem("Only the leader and officers can change the line-up.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (request == null || request.Slot < 1 || request.Slot > LineupSlots)
        {
            return Results.Problem($"Pick a place from 1 to {LineupSlots}.", statusCode: StatusCodes.Status400BadRequest);
        }

        List<GuildMember> members = await db.GuildMembers.Where(m => m.GuildId == mine.GuildId).ToListAsync(cancel);
        GuildMember? target = members.FirstOrDefault(m => m.UserId == request.UserId);

        if (target == null)
        {
            return Results.Problem("That player isn't in your guild.", statusCode: StatusCodes.Status404NotFound);
        }

        GuildMember? occupant = members.FirstOrDefault(m => m.Slot == request.Slot && m.UserId != target.UserId);

        if (occupant != null)
        {
            occupant.Slot = target.Slot;
        }

        target.Slot = request.Slot;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ invites

    private static async Task<IResult> MyInvites(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        DateTime now = DateTime.UtcNow;

        // Only invitations to this game's guilds.
        List<InviteDto> invites = await (from i in db.GuildInvites.AsNoTracking()
                                         join g in db.Guilds.AsNoTracking() on i.GuildId equals g.Id
                                         join u in db.Users.AsNoTracking() on i.InvitedById equals u.Id
                                         where i.UserId == me && i.ExpiresUtc > now && g.GameId == game
                                         orderby i.CreatedUtc
                                         select new InviteDto(g.Id, g.Name, g.Tag, u.Username, i.ExpiresUtc)).ToListAsync(cancel);

        return Results.Ok(invites);
    }

    private static async Task<IResult> Invite(UsernameRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? mine = await FindMembership(db, me, game, cancel);

        if (mine == null || mine.Role < GuildRole.Officer)
        {
            return Results.Problem("Only officers and the leader can invite players.", statusCode: StatusCodes.Status403Forbidden);
        }

        string normalized = (request?.Username ?? "").Trim().ToUpperInvariant();
        User? target = normalized.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // Same privacy rule as friend requests: blocked looks like "doesn't exist".
        if (target == null || await SocialEndpoints.IsBlockedEitherWay(db, me, target.Id, cancel))
        {
            return Results.Problem("No player with that name.", statusCode: StatusCodes.Status404NotFound);
        }

        if (await FindMembership(db, target.Id, game, cancel) != null)
        {
            return Results.Problem($"{target.Username} is already in a guild.", statusCode: StatusCodes.Status409Conflict);
        }

        DateTime now = DateTime.UtcNow;
        GuildInvite? existing = await db.GuildInvites.FirstOrDefaultAsync(i => i.GuildId == mine.GuildId && i.UserId == target.Id, cancel);

        if (existing != null)
        {
            // Inviting again just renews it.
            existing.ExpiresUtc = now + InviteLifetime;
        }
        else
        {
            db.GuildInvites.Add(new GuildInvite { GuildId = mine.GuildId, UserId = target.Id, InvitedById = me, ExpiresUtc = now + InviteLifetime });
        }

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("That player was just invited.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new { invited = target.Username });
    }

    private static async Task<IResult> AcceptInvite(Guid guildId, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        DateTime now = DateTime.UtcNow;
        Guild? guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId && g.GameId == game, cancel);

        if (guild == null || !await db.GuildInvites.AnyAsync(i => i.GuildId == guildId && i.UserId == me && i.ExpiresUtc > now, cancel))
        {
            return Results.Problem("That invitation doesn't exist or has expired.", statusCode: StatusCodes.Status404NotFound);
        }

        return await AddMember(db, guild, me, cancel);
    }

    private static async Task<IResult> DeclineInvite(Guid guildId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        int removed = await db.GuildInvites.Where(i => i.GuildId == guildId && i.UserId == me).ExecuteDeleteAsync(cancel);

        return removed > 0
            ? Results.NoContent()
            : Results.Problem("That invitation doesn't exist.", statusCode: StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------ guild chat

    private static async Task<IResult> ChatHistory(long? afterId, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        await Presence.Touch(db, me, cancel);

        IQueryable<GuildMessage> query = db.GuildMessages.AsNoTracking().Where(m => m.GuildId == membership.GuildId);

        List<GuildMessage> messages = afterId.HasValue
            ? await query.Where(m => m.Id > afterId.Value).OrderBy(m => m.Id).Take(100).ToListAsync(cancel)
            : (await query.OrderByDescending(m => m.Id).Take(50).ToListAsync(cancel)).OrderBy(m => m.Id).ToList();

        List<Guid> senderIds = messages.Select(m => m.SenderId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => senderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);

        return Results.Ok(messages.Select(m => new GuildMessageDto(m.Id, m.SenderId, names.GetValueOrDefault(m.SenderId, "?"), m.Text, m.SentUtc)).ToList());
    }

    private static async Task<IResult> ChatSend(SendMessageRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string text = (request?.Text ?? "").Trim();

        if (text.Length == 0 || text.Length > MaxMessageLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = new[] { $"A message must be 1-{MaxMessageLength} characters." } });
        }

        if (!GameIds.TryFromRequest(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        DateTime windowStart = DateTime.UtcNow.AddSeconds(-10);

        if (await db.GuildMessages.CountAsync(m => m.SenderId == me && m.SentUtc > windowStart, cancel) >= MaxMessagesPer10Seconds)
        {
            return Results.Problem("Slow down! You're sending messages too fast.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        GuildMessage message = new() { GuildId = membership.GuildId, SenderId = me, Text = text };
        db.GuildMessages.Add(message);
        await db.SaveChangesAsync(cancel);

        string username = await db.Users.Where(u => u.Id == me).Select(u => u.Username).FirstAsync(cancel);
        GuildMessageDto dto = new(message.Id, me, username, message.Text, message.SentUtc);

        await hub.Clients.Group(ChatHub.GuildGroup(membership.GuildId)).SendAsync("ReceiveGuildMessage", dto, cancel);

        return Results.Ok(dto);
    }

    // ------------------------------------------------------------------ bosses

    private static async Task<IResult> BossChoices(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        int count = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId, cancel);

        return Results.Ok(Bosses.All.Select(b => new BossChoice(b.Key, b.Name, b.Description, b.RewardPool, b.DurationHours, HpFor(b, count))).ToList());
    }

    private static async Task<IResult> CurrentBoss(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        BossInfo? boss = await BuildBossInfo(db, membership.GuildId, me, cancel);

        return boss == null
            ? Results.Problem("Your guild hasn't fought a boss yet.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(boss);
    }

    private static async Task<IResult> StartBoss(StartBossRequest request, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null || membership.Role < GuildRole.Officer)
        {
            return Results.Problem("Only officers and the leader can summon a boss.", statusCode: StatusCodes.Status403Forbidden);
        }

        BossDefinition? definition = Bosses.Find(request?.BossKey ?? "");

        if (definition == null)
        {
            return Results.Problem("There is no such boss.", statusCode: StatusCodes.Status404NotFound);
        }

        DateTime now = DateTime.UtcNow;

        // An overdue boss that the background job hasn't closed yet must not block a new one.
        await BossLifecycleService.RunOnce(db, now, cancel);

        if (await db.GuildBosses.AnyAsync(b => b.GuildId == membership.GuildId && b.Status == BossStatus.Active, cancel))
        {
            return Results.Problem("Your guild is already fighting a boss.", statusCode: StatusCodes.Status409Conflict);
        }

        int members = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId, cancel);
        int hp = HpFor(definition, members);

        GuildBoss boss = new()
        {
            GuildId = membership.GuildId,
            BossKey = definition.Key,
            Name = definition.Name,
            MaxHp = hp,
            CurrentHp = hp,
            RewardPool = definition.RewardPool,
            StartedById = me,
            StartedUtc = now,
            EndsUtc = now.AddHours(definition.DurationHours)
        };

        db.GuildBosses.Add(boss);

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Two officers summoned at the same moment; the database allows only one active boss.
            return Results.Problem("Your guild is already fighting a boss.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(await BuildBossInfo(db, membership.GuildId, me, cancel));
    }

    private static async Task<IResult> Attack(HttpContext http, ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!CardGameOnly(http, out string game, out IResult? bad)) return bad!;

        GuildMember? membership = await FindMembership(db, me, game, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        // The SERVER works out how hard you hit, from the cards the server says you own.
        // Your best five monsters add up to your "power"; each hit is 8-12% of it.
        await EconomyEndpoints.GetOrCreateProfile(db, catalog, me, cancel);

        List<string> ownedIds = await db.OwnedCards.AsNoTracking().Where(c => c.UserId == me && c.Quantity > 0).Select(c => c.CardId).ToListAsync(cancel);
        int power = ownedIds.Select(catalog.Find).Where(c => c != null && c.Category == 0).OrderByDescending(c => c!.Atk).Take(5).Sum(c => c!.Atk);

        // Two members can hit the boss at the same instant. The concurrency token lets only one save
        // win; the loser simply tries again against the new health value.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            db.ChangeTracker.Clear();
            DateTime now = DateTime.UtcNow;
            int today = DayNumber(now);

            GuildBoss? boss = await db.GuildBosses.FirstOrDefaultAsync(b => b.GuildId == membership.GuildId && b.Status == BossStatus.Active, cancel);

            if (boss == null || boss.EndsUtc <= now)
            {
                return Results.Problem("Your guild has no boss to fight right now.", statusCode: StatusCodes.Status409Conflict);
            }

            BossContribution? mine = await db.BossContributions.FirstOrDefaultAsync(c => c.BossId == boss.Id && c.UserId == me, cancel);
            bool isNewContribution = mine == null;
            mine ??= new BossContribution { BossId = boss.Id, UserId = me };

            if (mine.AttackDay != today)
            {
                mine.AttackDay = today;
                mine.AttacksToday = 0;
            }

            if (mine.AttacksToday >= MaxAttacksPerDay)
            {
                return Results.Problem("You've used all your attacks for today. They come back tomorrow!", statusCode: StatusCodes.Status429TooManyRequests);
            }

            int damage = Math.Max(50, (int)(power * (0.08 + (Random.Shared.NextDouble() * 0.04))));
            damage = Math.Min(damage, boss.CurrentHp);

            boss.CurrentHp -= damage;
            boss.Version += 1;
            mine.TotalDamage += damage;
            mine.Attacks += 1;
            mine.AttacksToday += 1;

            if (isNewContribution)
            {
                db.BossContributions.Add(mine);
            }

            int myReward = 0;
            bool defeated = boss.CurrentHp <= 0;

            if (defeated)
            {
                boss.Status = BossStatus.Defeated;
                myReward = await PayOut(db, boss, mine, cancel);
            }

            try
            {
                await db.SaveChangesAsync(cancel);
            }
            catch (DbUpdateException)
            {
                continue;
            }

            return Results.Ok(new AttackResult(damage, boss.CurrentHp, defeated, MaxAttacksPerDay - mine.AttacksToday, myReward));
        }

        return Results.Problem("The boss is very busy right now. Try again.", statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>The boss fell: share the reward pool between everyone who hit it, in proportion to damage.</summary>
    private static async Task<int> PayOut(AppDbContext db, GuildBoss boss, BossContribution mine, CancellationToken cancel)
    {
        List<BossContribution> contributors = await db.BossContributions.Where(c => c.BossId == boss.Id && c.UserId != mine.UserId).ToListAsync(cancel);
        contributors.Add(mine);

        long total = contributors.Sum(c => (long)c.TotalDamage);
        List<Guid> ids = contributors.Select(c => c.UserId).ToList();
        Dictionary<Guid, CardGameProfile> wallets = await db.CardGameProfiles.Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancel);

        int myReward = 0;

        foreach (BossContribution contributor in contributors)
        {
            if (!wallets.TryGetValue(contributor.UserId, out CardGameProfile? wallet) || total <= 0)
            {
                continue;
            }

            int share = Math.Max(25, (int)Math.Round(boss.RewardPool * (contributor.TotalDamage / (double)total)));

            wallet.DuelPoints += share;
            wallet.Version += 1;

            db.Ledger.Add(new LedgerEntry
            {
                UserId = contributor.UserId,
                Kind = "guild-boss",
                Detail = "Defeated " + boss.Name,
                Amount = share,
                BalanceAfter = wallet.DuelPoints
            });

            if (contributor.UserId == mine.UserId)
            {
                myReward = share;
            }
        }

        return myReward;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The player's guild membership in ONE game (a player can be in one guild per game).</summary>
    private static Task<GuildMember?> FindMembership(AppDbContext db, Guid userId, string gameId, CancellationToken cancel)
        => db.GuildMembers.FirstOrDefaultAsync(m => m.UserId == userId && m.GameId == gameId, cancel);

    /// <summary>
    /// For card-game-only features (bosses, line-ups, Guild Wars): they use cards and Duel Points, which don't exist
    /// in the walking game. Other games get a clear "not in this game" answer instead of a confusing one.
    /// </summary>
    private static bool CardGameOnly(HttpContext http, out string gameId, out IResult? problem)
    {
        if (!GameIds.TryFromRequest(http, out gameId, out problem))
        {
            return false;
        }

        if (gameId != GameIds.Yugioh)
        {
            problem = Results.Problem("That guild feature isn't part of this game.", statusCode: StatusCodes.Status404NotFound);
            return false;
        }

        return true;
    }

    private static int HpFor(BossDefinition definition, int members) => definition.HpPerMember * Math.Max(3, members);

    private static int DayNumber(DateTime utc) => (int)(utc.Date - DateTime.UnixEpoch.Date).TotalDays;

    /// <summary>The active boss, or the most recent one (so you can still see the result of the last fight).</summary>
    private static async Task<BossInfo?> BuildBossInfo(AppDbContext db, Guid guildId, Guid me, CancellationToken cancel)
    {
        GuildBoss? boss = await db.GuildBosses.AsNoTracking()
            .Where(b => b.GuildId == guildId)
            .OrderBy(b => b.Status) // Active (0) first
            .ThenByDescending(b => b.StartedUtc)
            .FirstOrDefaultAsync(cancel);

        if (boss == null)
        {
            return null;
        }

        List<LeaderboardEntry> board = await (from c in db.BossContributions.AsNoTracking()
                                              join u in db.Users.AsNoTracking() on c.UserId equals u.Id
                                              where c.BossId == boss.Id
                                              orderby c.TotalDamage descending
                                              select new LeaderboardEntry(u.Username, c.TotalDamage, c.Attacks)).Take(10).ToListAsync(cancel);

        BossContribution? mine = await db.BossContributions.AsNoTracking().FirstOrDefaultAsync(c => c.BossId == boss.Id && c.UserId == me, cancel);
        int used = mine != null && mine.AttackDay == DayNumber(DateTime.UtcNow) ? mine.AttacksToday : 0;

        bool fighting = boss.Status == BossStatus.Active && boss.EndsUtc > DateTime.UtcNow;
        string status = boss.Status == BossStatus.Active && !fighting ? BossStatus.Expired.ToString() : boss.Status.ToString();

        return new BossInfo(boss.Id, boss.BossKey, boss.Name, boss.MaxHp, boss.CurrentHp, status, boss.EndsUtc, boss.RewardPool, fighting ? MaxAttacksPerDay - used : 0, board);
    }
}
