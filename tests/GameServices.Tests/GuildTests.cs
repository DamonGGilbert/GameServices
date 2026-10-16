using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Chat;
using GameServices.Api.Data;
using GameServices.Api.Economy;
using GameServices.Api.Guilds;
using GameServices.Api.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

public class GuildTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public GuildTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name, string Token);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "g" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name, auth.AccessToken);
    }

    private static string UniqueName() => "Guild " + Guid.NewGuid().ToString("N")[..8];

    private static async Task<GuildSummary> MakeGuild(Player leader, bool open = true)
    {
        HttpResponseMessage response = await leader.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(UniqueName(), "TST", "for tests", open));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuildSummary>())!;
    }

    private static async Task<GuildDetail> Mine(Player player)
        => (await player.Http.GetFromJsonAsync<GuildDetail>("/api/guilds/mine"))!;

    private static async Task JoinGuild(Player player, GuildSummary guild)
        => Assert.Equal(HttpStatusCode.NoContent, (await player.Http.PostAsync($"/api/guilds/{guild.Id}/join", null)).StatusCode);

    private static Task<HttpResponseMessage> Post(Player player, string url) => player.Http.PostAsync(url, null);

    /// <summary>A guild with a leader, an officer and one ordinary member.</summary>
    private async Task<(Player Leader, Player Officer, Player Member, GuildSummary Guild)> FullGuild()
    {
        Player leader = await NewPlayer();
        Player officer = await NewPlayer();
        Player member = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader);

        await JoinGuild(officer, guild);
        await JoinGuild(member, guild);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(leader, $"/api/guilds/members/{officer.Id}/promote")).StatusCode);

        return (leader, officer, member, guild);
    }

    // ------------------------------------------------------------------ editing the guild's details

    private static Task<HttpResponseMessage> Edit(Player by, UpdateGuildRequest body)
        => by.Http.PutAsJsonAsync("/api/guilds/details", body);

    [Fact]
    public async Task The_leader_can_change_the_description_and_open_setting_any_time()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader);

        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(null, null, "New words", false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(null, null, "Newer words", true))).StatusCode);

        GuildDetail detail = await Mine(leader);
        Assert.Equal("Newer words", detail.Description);
        Assert.True(detail.IsOpen);
        Assert.Null(detail.NextNameChangeUtc);
    }

    [Fact]
    public async Task Only_the_leader_can_edit_the_guild()
    {
        (Player leader, Player officer, Player member, _) = await FullGuild();

        Assert.Equal(HttpStatusCode.Forbidden, (await Edit(officer, new UpdateGuildRequest(null, null, "x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Edit(member, new UpdateGuildRequest(null, null, "x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(null, null, "x", null))).StatusCode);
    }

    [Fact]
    public async Task The_name_and_tag_can_only_be_changed_every_14_days()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader);

        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(UniqueName(), "NEW", null, null))).StatusCode);

        GuildDetail detail = await Mine(leader);
        Assert.Equal("NEW", detail.Tag);
        Assert.NotNull(detail.NextNameChangeUtc);

        // A second change straight away is refused ...
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Edit(leader, new UpdateGuildRequest(UniqueName(), null, null, null))).StatusCode);

        // ... but the description is still free to change.
        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(null, null, "still fine", null))).StatusCode);

        // 14 days later it works again.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Guilds.Where(g => g.Id == detail.Id).ExecuteUpdateAsync(u => u.SetProperty(g => g.NameChangedUtc, DateTime.UtcNow.AddDays(-15)));
        }

        Assert.Equal(HttpStatusCode.OK, (await Edit(leader, new UpdateGuildRequest(UniqueName(), null, null, null))).StatusCode);
    }

    [Fact]
    public async Task Edited_names_are_checked_for_shape_and_uniqueness()
    {
        Player first = await NewPlayer();
        Player second = await NewPlayer();
        GuildSummary taken = await MakeGuild(first);
        await MakeGuild(second);

        Assert.Equal(HttpStatusCode.Conflict, (await Edit(second, new UpdateGuildRequest(taken.Name.ToUpperInvariant(), null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(second, new UpdateGuildRequest("x", null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(second, new UpdateGuildRequest(null, "TOOLONG", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(second, new UpdateGuildRequest(null, null, new string('a', 141), null))).StatusCode);
    }

    // ------------------------------------------------------------------ line-up (5 main + 3 bench)

    private static Task<HttpResponseMessage> SetSlot(Player by, Guid userId, int slot)
        => by.Http.PostAsJsonAsync("/api/guilds/lineup", new LineupRequest(userId, slot));

    [Fact]
    public async Task New_members_fill_the_main_team_then_the_bench_then_the_reserve()
    {
        Player leader = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader);
        List<Player> joiners = new();

        for (int i = 0; i < 9; i++)
        {
            Player joiner = await NewPlayer();
            await JoinGuild(joiner, guild);
            joiners.Add(joiner);
        }

        GuildDetail detail = await Mine(leader);
        Assert.Equal(1, detail.Members.Single(m => m.UserId == leader.Id).Slot);

        // 10 members, 8 places: slots 1-8 are each used once and two people wait in the reserve.
        Assert.Equal(Enumerable.Range(1, 8), detail.Members.Where(m => m.Slot > 0).Select(m => m.Slot).OrderBy(x => x));
        Assert.Equal(2, detail.Members.Count(m => m.Slot == 0));
    }

    [Fact]
    public async Task Officers_can_move_members_and_a_taken_place_swaps()
    {
        (Player leader, Player officer, Player member, _) = await FullGuild();

        GuildDetail before = await Mine(leader);
        int memberSlot = before.Members.Single(m => m.UserId == member.Id).Slot;
        int officerSlot = before.Members.Single(m => m.UserId == officer.Id).Slot;

        Assert.Equal(HttpStatusCode.NoContent, (await SetSlot(officer, member.Id, officerSlot)).StatusCode);

        GuildDetail after = await Mine(leader);
        Assert.Equal(officerSlot, after.Members.Single(m => m.UserId == member.Id).Slot);
        Assert.Equal(memberSlot, after.Members.Single(m => m.UserId == officer.Id).Slot);

        // Into an empty place.
        Assert.Equal(HttpStatusCode.NoContent, (await SetSlot(leader, member.Id, 8)).StatusCode);
        Assert.Equal(8, (await Mine(leader)).Members.Single(m => m.UserId == member.Id).Slot);
    }

    [Fact]
    public async Task Ordinary_members_cannot_change_the_line_up()
    {
        (Player leader, _, Player member, _) = await FullGuild();

        Assert.Equal(HttpStatusCode.Forbidden, (await SetSlot(member, leader.Id, 5)).StatusCode);
    }

    [Fact]
    public async Task The_line_up_rejects_bad_places_and_outsiders()
    {
        Player leader = await NewPlayer();
        Player outsider = await NewPlayer();
        await MakeGuild(leader);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetSlot(leader, leader.Id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetSlot(leader, leader.Id, 9)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetSlot(leader, outsider.Id, 3)).StatusCode);
    }

    [Fact]
    public async Task A_player_who_leaves_frees_their_place_for_the_next_joiner()
    {
        (Player leader, _, Player member, GuildSummary guild) = await FullGuild();
        int slot = (await Mine(leader)).Members.Single(m => m.UserId == member.Id).Slot;

        Assert.Equal(HttpStatusCode.NoContent, (await Post(member, "/api/guilds/leave")).StatusCode);

        Player newcomer = await NewPlayer();
        await JoinGuild(newcomer, guild);

        Assert.Equal(slot, (await Mine(leader)).Members.Single(m => m.UserId == newcomer.Id).Slot);
    }

    // ------------------------------------------------------------------ creating and joining

    [Fact]
    public async Task Guilds_require_sign_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/guilds")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_guild_makes_you_its_leader()
    {
        Player leader = await NewPlayer();

        GuildSummary guild = await MakeGuild(leader);

        GuildDetail detail = await Mine(leader);
        Assert.Equal(guild.Id, detail.Id);
        Assert.Equal("Leader", detail.MyRole);
        Assert.Equal("TST", detail.Tag);
        Assert.Single(detail.Members);
    }

    [Fact]
    public async Task A_player_without_a_guild_gets_not_found()
    {
        Player player = await NewPlayer();

        Assert.Equal(HttpStatusCode.NotFound, (await player.Http.GetAsync("/api/guilds/mine")).StatusCode);
    }

    [Fact]
    public async Task Guild_names_must_be_unique_ignoring_case()
    {
        Player first = await NewPlayer();
        Player second = await NewPlayer();
        string name = UniqueName();

        await first.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(name, "AAA", null, true));
        HttpResponseMessage clash = await second.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(name.ToUpperInvariant(), "BBB", null, true));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    [Theory]
    [InlineData("ab", "TAG")]
    [InlineData("Valid Name", "X")]
    [InlineData("Valid Name", "TOOLONG")]
    [InlineData("Bad<Name>", "TAG")]
    public async Task Invalid_guild_names_and_tags_are_rejected(string name, string tag)
    {
        Player player = await NewPlayer();

        HttpResponseMessage response = await player.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(name, tag, null, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task You_can_only_be_in_one_guild()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader);

        HttpResponseMessage second = await leader.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest(UniqueName(), "TWO", null, true));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Open_guilds_can_be_joined_and_found_by_search()
    {
        Player leader = await NewPlayer();
        Player joiner = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader);

        List<GuildSummary> found = (await joiner.Http.GetFromJsonAsync<List<GuildSummary>>($"/api/guilds?q={Uri.EscapeDataString(guild.Name[..10])}"))!;
        Assert.Contains(found, g => g.Id == guild.Id);

        await JoinGuild(joiner, guild);

        Assert.Equal(2, (await Mine(joiner)).Members.Count);
        Assert.Equal("Member", (await Mine(joiner)).MyRole);
    }

    [Fact]
    public async Task Closed_guilds_need_an_invite()
    {
        Player leader = await NewPlayer();
        Player outsider = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader, open: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(outsider, $"/api/guilds/{guild.Id}/join")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await leader.Http.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(outsider.Name))).StatusCode);

        InviteDto invite = Assert.Single((await outsider.Http.GetFromJsonAsync<List<InviteDto>>("/api/guilds/invites"))!);
        Assert.Equal(guild.Id, invite.GuildId);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(outsider, $"/api/guilds/invites/{guild.Id}/accept")).StatusCode);
        Assert.Equal(guild.Id, (await Mine(outsider)).Id);
        Assert.Empty((await outsider.Http.GetFromJsonAsync<List<InviteDto>>("/api/guilds/invites"))!);
    }

    [Fact]
    public async Task Invites_can_be_declined()
    {
        Player leader = await NewPlayer();
        Player invitee = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader, open: false);

        await leader.Http.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(invitee.Name));

        Assert.Equal(HttpStatusCode.NoContent, (await Post(invitee, $"/api/guilds/invites/{guild.Id}/decline")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(invitee, $"/api/guilds/invites/{guild.Id}/accept")).StatusCode);
    }

    [Fact]
    public async Task Ordinary_members_cannot_invite()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        Player outsider = await NewPlayer();

        HttpResponseMessage response = await member.Http.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(outsider.Name));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_invite_cannot_be_used_after_it_expires()
    {
        Player leader = await NewPlayer();
        Player invitee = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader, open: false);
        await leader.Http.PostAsJsonAsync("/api/guilds/invites", new UsernameRequest(invitee.Name));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GuildInvites.Where(i => i.UserId == invitee.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresUtc, DateTime.UtcNow.AddDays(-1)));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Post(invitee, $"/api/guilds/invites/{guild.Id}/accept")).StatusCode);
    }

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task Only_the_leader_can_promote_and_demote()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(officer, $"/api/guilds/members/{member.Id}/promote")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, $"/api/guilds/members/{officer.Id}/demote")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(leader, $"/api/guilds/members/{officer.Id}/demote")).StatusCode);
        Assert.Equal("Member", (await Mine(officer)).MyRole);
    }

    [Fact]
    public async Task Kicking_follows_rank()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();

        // A member can't kick anyone; an officer can't kick the leader.
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, $"/api/guilds/members/{officer.Id}/kick")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(officer, $"/api/guilds/members/{leader.Id}/kick")).StatusCode);

        // An officer CAN kick a member.
        Assert.Equal(HttpStatusCode.NoContent, (await Post(officer, $"/api/guilds/members/{member.Id}/kick")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync("/api/guilds/mine")).StatusCode);
    }

    [Fact]
    public async Task Leadership_can_be_handed_over_and_then_the_old_leader_can_leave()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();

        // The leader can't just walk out.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(leader, "/api/guilds/leave")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(leader, $"/api/guilds/members/{officer.Id}/transfer")).StatusCode);
        Assert.Equal("Leader", (await Mine(officer)).MyRole);
        Assert.Equal("Officer", (await Mine(leader)).MyRole);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(leader, "/api/guilds/leave")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await leader.Http.GetAsync("/api/guilds/mine")).StatusCode);
    }

    [Fact]
    public async Task The_last_member_leaving_closes_the_guild()
    {
        Player leader = await NewPlayer();
        GuildSummary guild = await MakeGuild(leader);

        Assert.Equal(HttpStatusCode.NoContent, (await Post(leader, "/api/guilds/leave")).StatusCode);

        Player stranger = await NewPlayer();
        Assert.Equal(HttpStatusCode.NotFound, (await Post(stranger, $"/api/guilds/{guild.Id}/join")).StatusCode);
    }

    // ------------------------------------------------------------------ guild chat

    [Fact]
    public async Task Guild_members_can_chat_and_outsiders_cannot_read_or_write()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        Player outsider = await NewPlayer();

        Assert.Equal(HttpStatusCode.OK, (await member.Http.PostAsJsonAsync("/api/guilds/chat", new SendMessageRequest("Boss tonight?"))).StatusCode);

        GuildMessageDto seen = Assert.Single((await leader.Http.GetFromJsonAsync<List<GuildMessageDto>>("/api/guilds/chat"))!);
        Assert.Equal("Boss tonight?", seen.Text);
        Assert.Equal(member.Name, seen.Username);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.GetAsync("/api/guilds/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.PostAsJsonAsync("/api/guilds/chat", new SendMessageRequest("let me in"))).StatusCode);
    }

    [Fact]
    public async Task Guild_chat_is_pushed_live_to_connected_members()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        TaskCompletionSource<GuildMessageDto> received = new();

        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(officer.Token);
            })
            .Build();

        connection.On<GuildMessageDto>("ReceiveGuildMessage", message => received.TrySetResult(message));
        await connection.StartAsync();

        await member.Http.PostAsJsonAsync("/api/guilds/chat", new SendMessageRequest("hello guild"));

        GuildMessageDto pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello guild", pushed.Text);
    }

    // ------------------------------------------------------------------ bosses

    private static Task<HttpResponseMessage> Summon(Player player, string key = "shadow-scout")
        => player.Http.PostAsJsonAsync("/api/guilds/boss", new StartBossRequest(key));

    private static async Task<AttackResult> HitOnce(Player player)
    {
        HttpResponseMessage response = await Post(player, "/api/guilds/boss/attack");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttackResult>())!;
    }

    [Fact]
    public async Task Only_officers_can_summon_a_boss_and_only_one_at_a_time()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();

        Assert.Equal(HttpStatusCode.Forbidden, (await Summon(member)).StatusCode);

        HttpResponseMessage started = await Summon(officer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        // Three members: health is 4000 x 3.
        BossInfo boss = (await started.Content.ReadFromJsonAsync<BossInfo>())!;
        Assert.Equal(12000, boss.MaxHp);
        Assert.Equal(3, boss.MyAttacksLeft);

        Assert.Equal(HttpStatusCode.Conflict, (await Summon(leader, "stone-colossus")).StatusCode);
    }

    [Fact]
    public async Task Unknown_bosses_are_not_found()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader);

        Assert.Equal(HttpStatusCode.NotFound, (await Summon(leader, "ultra-god")).StatusCode);
    }

    [Fact]
    public async Task Attacking_reduces_the_boss_health_by_the_damage_dealt()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        await Summon(leader);

        AttackResult hit = await HitOnce(member);

        Assert.True(hit.Damage >= 50);
        Assert.Equal(12000 - hit.Damage, hit.BossHp);
        Assert.Equal(2, hit.AttacksLeft);

        BossInfo boss = (await member.Http.GetFromJsonAsync<BossInfo>("/api/guilds/boss"))!;
        Assert.Equal(hit.BossHp, boss.CurrentHp);
        Assert.Equal(hit.Damage, Assert.Single(boss.Leaderboard).Damage);
    }

    [Fact]
    public async Task Each_member_gets_three_attacks_a_day()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        await Summon(leader);

        for (int i = 0; i < 3; i++)
        {
            await HitOnce(member);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(member, "/api/guilds/boss/attack")).StatusCode);

        // The others still have theirs.
        await HitOnce(officer);
    }

    [Fact]
    public async Task Attacking_needs_a_guild_and_an_active_boss()
    {
        Player loner = await NewPlayer();
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(loner, "/api/guilds/boss/attack")).StatusCode);

        Player leader = await NewPlayer();
        await MakeGuild(leader);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(leader, "/api/guilds/boss/attack")).StatusCode);
    }

    [Fact]
    public async Task Defeating_the_boss_pays_every_contributor_into_their_wallet()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        await Summon(leader);

        await HitOnce(officer);

        // Make the boss nearly dead so the next hit finishes it.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.GuildBosses.Where(b => b.GuildId == guild.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentHp, 10));
        }

        AttackResult finishing = await HitOnce(member);

        Assert.True(finishing.Defeated);
        Assert.Equal(0, finishing.BossHp);
        Assert.True(finishing.RewardDp >= 25);

        // The officer (who hit earlier) is paid too, and the ledger says why.
        ProfileResponse officerProfile = (await officer.Http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile"))!;
        Assert.True(officerProfile.DuelPoints > 900);

        List<LedgerLine> ledger = (await officer.Http.GetFromJsonAsync<List<LedgerLine>>("/api/card-game/ledger"))!;
        Assert.Contains(ledger, l => l.Kind == "guild-boss");

        // Someone who never attacked gets nothing.
        Assert.Equal(900, (await leader.Http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile"))!.DuelPoints);

        // The fight is over: no more hits, and a new boss can be summoned.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(officer, "/api/guilds/boss/attack")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Summon(leader, "stone-colossus")).StatusCode);
    }

    [Fact]
    public async Task The_background_job_closes_bosses_whose_time_ran_out()
    {
        Player leader = await NewPlayer();
        await MakeGuild(leader);
        await Summon(leader);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Pretend it is 25 hours later (the Shadow Scout lasts 24).
            int closed = await BossLifecycleService.RunOnce(db, DateTime.UtcNow.AddHours(25));
            Assert.True(closed >= 1);
        }

        BossInfo boss = (await leader.Http.GetFromJsonAsync<BossInfo>("/api/guilds/boss"))!;
        Assert.Equal("Expired", boss.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(leader, "/api/guilds/boss/attack")).StatusCode);
    }

    [Fact]
    public async Task Simultaneous_attacks_never_lose_or_invent_damage()
    {
        (Player leader, Player officer, Player member, GuildSummary guild) = await FullGuild();
        await Summon(leader);

        // Three players, three attacks each, all fired at the same moment.
        Player[] players = { leader, officer, member };
        HttpResponseMessage[] responses = await Task.WhenAll(players.SelectMany(p => Enumerable.Range(0, 3).Select(_ => Post(p, "/api/guilds/boss/attack"))));

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);

        int dealt = 0;

        foreach (HttpResponseMessage response in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
        {
            dealt += (await response.Content.ReadFromJsonAsync<AttackResult>())!.Damage;
        }

        BossInfo boss = (await leader.Http.GetFromJsonAsync<BossInfo>("/api/guilds/boss"))!;

        // Every point of damage reported to a player is a point the boss really lost.
        Assert.Equal(boss.MaxHp - boss.CurrentHp, dealt);
        Assert.Equal(dealt, boss.Leaderboard.Sum(e => e.Damage));
    }
}
