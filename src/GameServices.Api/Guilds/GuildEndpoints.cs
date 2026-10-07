using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Catalog;
using GameServices.Api.Chat;
using GameServices.Api.Data;
using GameServices.Api.Economy;
using GameServices.Api.Social;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Guilds;

public record CreateGuildRequest(string Name, string Tag, string? Description, bool IsOpen);

public record GuildSummary(Guid Id, string Name, string Tag, string Description, bool IsOpen, int Members);

public record MemberDto(Guid UserId, string Username, string Role, bool IsOnline);

public record LeaderboardEntry(string Username, int Damage, int Attacks);

public record BossInfo(Guid Id, string Key, string Name, int MaxHp, int CurrentHp, string Status, DateTime EndsUtc, int RewardPool, int MyAttacksLeft, List<LeaderboardEntry> Leaderboard);

public record GuildDetail(Guid Id, string Name, string Tag, string Description, bool IsOpen, string MyRole, List<MemberDto> Members, BossInfo? Boss);

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
/// </summary>
public static partial class GuildEndpoints
{
    public const int MaxMembers = 30;
    public const int MaxAttacksPerDay = 3;
    public const int MaxMessageLength = 500;
    public const int MaxMessagesPer10Seconds = 10;
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(3);

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

        group.MapPost("/members/{userId:guid}/promote", (Guid userId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => ChangeRole(userId, GuildRole.Officer, p, db, c));
        group.MapPost("/members/{userId:guid}/demote", (Guid userId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => ChangeRole(userId, GuildRole.Member, p, db, c));
        group.MapPost("/members/{userId:guid}/kick", Kick);
        group.MapPost("/members/{userId:guid}/transfer", Transfer);

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

    private static async Task<IResult> Search(string? q, AppDbContext db, CancellationToken cancel)
    {
        string needle = (q ?? "").Trim().ToUpperInvariant();

        IQueryable<Guild> guilds = db.Guilds.AsNoTracking();

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
            .Select(g => new GuildSummary(g.Id, g.Name, g.Tag, g.Description, g.IsOpen, counts.GetValueOrDefault(g.Id)))
            .OrderByDescending(g => g.Members).ThenBy(g => g.Name)
            .Take(25)
            .ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> Create(CreateGuildRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

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

        if (await FindMembership(db, me, cancel) != null)
        {
            return Results.Problem("You're already in a guild. Leave it first.", statusCode: StatusCodes.Status409Conflict);
        }

        string normalized = name.ToUpperInvariant();

        if (await db.Guilds.AnyAsync(g => g.NormalizedName == normalized, cancel))
        {
            return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
        }

        Guild guild = new() { Name = name, NormalizedName = normalized, Tag = tag.ToUpperInvariant(), Description = description, IsOpen = request!.IsOpen };
        db.Guilds.Add(guild);
        db.GuildMembers.Add(new GuildMember { GuildId = guild.Id, UserId = me, Role = GuildRole.Leader });

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            return Results.Problem("That guild name is taken.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new GuildSummary(guild.Id, guild.Name, guild.Tag, guild.Description, guild.IsOpen, 1));
    }

    private static async Task<IResult> Mine(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status404NotFound);
        }

        await Presence.Touch(db, me, cancel);

        Guild guild = await db.Guilds.AsNoTracking().FirstAsync(g => g.Id == membership.GuildId, cancel);
        DateTime now = DateTime.UtcNow;

        var rows = await (from m in db.GuildMembers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.GuildId == guild.Id
                          select new { m.Role, m.UserId, u.Username, u.LastSeenUtc }).ToListAsync(cancel);

        List<MemberDto> members = rows
            .OrderByDescending(x => x.Role).ThenBy(x => x.Username)
            .Select(x => new MemberDto(x.UserId, x.Username, x.Role.ToString(), x.LastSeenUtc != null && now - x.LastSeenUtc.Value < SocialEndpoints.OnlineWindow))
            .ToList();

        BossInfo? boss = await BuildBossInfo(db, guild.Id, me, cancel);

        return Results.Ok(new GuildDetail(guild.Id, guild.Name, guild.Tag, guild.Description, guild.IsOpen, membership.Role.ToString(), members, boss));
    }

    // ------------------------------------------------------------------ joining / leaving

    private static async Task<IResult> Join(Guid guildId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Guild? guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, cancel);

        if (guild == null)
        {
            return Results.Problem("That guild doesn't exist.", statusCode: StatusCodes.Status404NotFound);
        }

        if (!guild.IsOpen)
        {
            return Results.Problem("This guild is invite-only.", statusCode: StatusCodes.Status403Forbidden);
        }

        return await AddMember(db, guild.Id, me, cancel);
    }

    private static async Task<IResult> AddMember(AppDbContext db, Guid guildId, Guid userId, CancellationToken cancel)
    {
        if (await FindMembership(db, userId, cancel) != null)
        {
            return Results.Problem("You're already in a guild. Leave it first.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.GuildMembers.CountAsync(m => m.GuildId == guildId, cancel) >= MaxMembers)
        {
            return Results.Problem($"That guild is full ({MaxMembers} members).", statusCode: StatusCodes.Status409Conflict);
        }

        db.GuildMembers.Add(new GuildMember { GuildId = guildId, UserId = userId, Role = GuildRole.Member });

        // Joining uses up any invites you were holding.
        await db.GuildInvites.Where(i => i.UserId == userId).ExecuteDeleteAsync(cancel);

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

    private static async Task<IResult> Leave(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> ChangeRole(Guid userId, GuildRole newRole, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? mine = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> Kick(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? mine = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> Transfer(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? mine = await FindMembership(db, me, cancel);

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

    // ------------------------------------------------------------------ invites

    private static async Task<IResult> MyInvites(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime now = DateTime.UtcNow;

        List<InviteDto> invites = await (from i in db.GuildInvites.AsNoTracking()
                                         join g in db.Guilds.AsNoTracking() on i.GuildId equals g.Id
                                         join u in db.Users.AsNoTracking() on i.InvitedById equals u.Id
                                         where i.UserId == me && i.ExpiresUtc > now
                                         orderby i.CreatedUtc
                                         select new InviteDto(g.Id, g.Name, g.Tag, u.Username, i.ExpiresUtc)).ToListAsync(cancel);

        return Results.Ok(invites);
    }

    private static async Task<IResult> Invite(UsernameRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? mine = await FindMembership(db, me, cancel);

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

        if (await FindMembership(db, target.Id, cancel) != null)
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

    private static async Task<IResult> AcceptInvite(Guid guildId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime now = DateTime.UtcNow;

        if (!await db.GuildInvites.AnyAsync(i => i.GuildId == guildId && i.UserId == me && i.ExpiresUtc > now, cancel))
        {
            return Results.Problem("That invitation doesn't exist or has expired.", statusCode: StatusCodes.Status404NotFound);
        }

        return await AddMember(db, guildId, me, cancel);
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

    private static async Task<IResult> ChatHistory(long? afterId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> ChatSend(SendMessageRequest request, ClaimsPrincipal principal, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string text = (request?.Text ?? "").Trim();

        if (text.Length == 0 || text.Length > MaxMessageLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = new[] { $"A message must be 1-{MaxMessageLength} characters." } });
        }

        GuildMember? membership = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> BossChoices(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        int count = await db.GuildMembers.CountAsync(m => m.GuildId == membership.GuildId, cancel);

        return Results.Ok(Bosses.All.Select(b => new BossChoice(b.Key, b.Name, b.Description, b.RewardPool, b.DurationHours, HpFor(b, count))).ToList());
    }

    private static async Task<IResult> CurrentBoss(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

        if (membership == null)
        {
            return Results.Problem("You aren't in a guild.", statusCode: StatusCodes.Status403Forbidden);
        }

        BossInfo? boss = await BuildBossInfo(db, membership.GuildId, me, cancel);

        return boss == null
            ? Results.Problem("Your guild hasn't fought a boss yet.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(boss);
    }

    private static async Task<IResult> StartBoss(StartBossRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

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

    private static async Task<IResult> Attack(ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await FindMembership(db, me, cancel);

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

    private static Task<GuildMember?> FindMembership(AppDbContext db, Guid userId, CancellationToken cancel)
        => db.GuildMembers.FirstOrDefaultAsync(m => m.UserId == userId, cancel);

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
