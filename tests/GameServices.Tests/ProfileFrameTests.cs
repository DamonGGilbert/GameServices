using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// FRAMES FOR A PROFILE PICTURE are earned, never bought or made up: a walker can only wear one the server agrees they've
/// earned, and then everyone sees it.
/// </summary>
public class ProfileFrameTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ProfileFrameTests(ApiFactory factory)
    {
        this.factory = factory;
        _ = factory.Services;   // the server has started, so its rules are loaded
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "f" + Guid.NewGuid().ToString("N")[..10];
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null, 1996, 3)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.GetAsync("/api/walking/monsters");
        return new Player(client, auth.User.Id, name);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> FromDb<T>(Func<AppDbContext, Task<T>> read)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<List<string>> Earned(Player p) => FromDb(async db =>
        await Explorer.EarnedFramesAsync(db, await db.WalkingProfiles.AsNoTracking().FirstAsync(x => x.UserId == p.Id), CancellationToken.None));

    private Task<string> Wearing(Player p) => FromDb(db => db.WalkingProfiles.Where(x => x.UserId == p.Id).Select(x => x.Frame).FirstAsync());

    private static Task Wear(Player p, string frame) => p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, null, null, frame));

    [Fact]
    public void The_frames_came_across_from_the_game()
    {
        List<string> ids = MonsterCatalog.Shared.Progression.Frames.Select(f => f.Id).ToList();
        Assert.Equal(new[] { "frame_trailblazer", "frame_explorer", "frame_gold", "frame_buddy", "frame_champion", "frame_legendary" }, ids);
    }

    [Fact]
    public async Task A_new_walker_has_no_frames_and_cant_wear_one()
    {
        Player p = await NewPlayer();
        Assert.Empty(await Earned(p));

        await Wear(p, "frame_legendary");
        Assert.Equal("", await Wearing(p));

        await Wear(p, "frame_made_up");
        Assert.Equal("", await Wearing(p));
    }

    [Fact]
    public async Task Each_frame_is_earned_its_own_way()
    {
        Player p = await NewPlayer();
        await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            profile.Cosmetics = "frame_trailblazer";   // the 7-day streak reward
            profile.PlayerLevel = 10;
            db.WalkingAchievements.Add(new WalkingAchievement { UserId = p.Id, AchievementId = "veteran", Unlocked = true });
            db.WalkingAchievements.Add(new WalkingAchievement { UserId = p.Id, AchievementId = "treasure_hunter", Unlocked = false });   // not yet
            db.WalkingMedals.Add(new WalkingMedal { UserId = p.Id, TrackId = "steps", Reached = 2 });   // Silver: not Gold yet
        });

        Assert.Equal(new[] { "frame_trailblazer", "frame_explorer", "frame_champion" }, await Earned(p));

        await WithDb(async db => (await db.WalkingMedals.FirstAsync(m => m.UserId == p.Id)).Reached = 3);   // Gold
        Assert.Contains("frame_gold", await Earned(p));
    }

    [Fact]
    public async Task An_earned_frame_can_be_worn_and_everyone_sees_it()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).PlayerLevel = 12);

        await Wear(p, "frame_explorer");
        Assert.Equal("frame_explorer", await Wearing(p));

        PublicProfileDto seen = (await other.Http.GetFromJsonAsync<PublicProfileDto>("/api/walking/players/" + p.Id))!;
        Assert.Equal("frame_explorer", seen.Frame);

        // A frame they no longer qualify for (or never did) isn't taken, and what they wear stays.
        await Wear(p, "frame_champion");
        Assert.Equal("frame_explorer", await Wearing(p));

        await Wear(p, "");
        Assert.Equal("", await Wearing(p));
    }

    [Fact]
    public async Task The_players_list_shows_each_walkers_frame()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).PlayerLevel = 12);
        await Wear(p, "frame_explorer");

        // (the "active" list shows walkers with recent steps)
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LastReportUtc = DateTime.UtcNow);
        ExplorerListDto list = (await other.Http.GetFromJsonAsync<ExplorerListDto>("/api/walking/explorers?view=active"))!;

        Assert.Equal("frame_explorer", Assert.Single(list.Explorers, e => e.UserId == p.Id).Frame);
    }
}
