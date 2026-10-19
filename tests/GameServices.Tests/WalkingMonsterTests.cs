using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>The real server, but every monster-finding chance is lucky (so finds can be tested without waiting weeks).</summary>
public class LuckyMonsterFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Monsters:FindChance", "1");
        builder.UseSetting("Monsters:StepsPerChance", "5000");
        builder.UseSetting("Monsters:MaxFindsPerDay", "1");
    }
}

/// <summary>
/// MONSTERS MADE ON THE SERVER: the starter companion, very rare finds while walking (one chance per 5,000 steps,
/// at most one find a day), serial numbers, and the server's potential maths matching the game's.
/// </summary>
public class WalkingMonsterTests : IClassFixture<LuckyMonsterFactory>
{
    private readonly LuckyMonsterFactory factory;

    public WalkingMonsterTests(LuckyMonsterFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "m" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id);
    }

    // A time zone where it's about midday, so thousands of steps are believable whenever the tests run.
    private static (string Day, int Offset) Midday()
    {
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        return (now.AddMinutes(offset).ToString("yyyy-MM-dd"), offset);
    }

    private async Task<StepReportResult> Report(Player p, int stepsToday, string? biome = null)
    {
        (string day, int offset) = Midday();
        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(day, stepsToday, offset, biome));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        StepReportResult result = (await response.Content.ReadFromJsonAsync<StepReportResult>())!;
        await RewindLastReport(p);
        return result;
    }

    // Pretends the last report was half an hour ago: no waiting for the 15-second report limit, and enough time to
    // have walked the next few thousand steps (the cheat check refuses more than anyone could walk).
    private async Task RewindLastReport(Player p)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
        profile.LastReportUtc = profile.LastReportUtc!.Value.AddMinutes(-30);
        // Each day is judged on its own last report, so that moves back too.
        foreach (WalkingDay day in db.WalkingDays.Where(d => d.UserId == p.Id)) day.UpdatedUtc = day.UpdatedUtc.AddMinutes(-30);
        await db.SaveChangesAsync();
    }

    private static async Task<List<WalkingMonsterDto>> Monsters(Player p) =>
        (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!;

    // ------------------------------------------------------------------ the dice

    [Theory]
    [InlineData(1L, 0, 53)]
    [InlineData(1L, 3, 80)]
    [InlineData(1L, 4, 89)]
    [InlineData(42L, 0, 61)]
    [InlineData(42L, 4, 97)]
    [InlineData(-7L, 0, 53)]
    [InlineData(123456789012345L, 0, 43)]
    [InlineData(123456789012345L, 3, 70)]
    [InlineData(long.MaxValue, 4, 75)]
    public void Potential_matches_the_game_exactly(long seed, int tier, int expected)
    {
        // Expected values were worked out by the game itself (CompanionTraining.Potential in Unity).
        Assert.Equal(expected, MonsterPotential.For(seed, tier));
    }

    [Fact]
    public void The_catalog_has_five_rarities_under_the_current_rules()
    {
        MonsterFinder finder = factory.Services.GetRequiredService<MonsterFinder>();
        Assert.Equal(MonsterRulesUpgrade.CurrentRulesVersion, finder.Catalog.RulesVersion);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, finder.Catalog.Rarities.Select(r => r.Tier).ToArray());
        Assert.All(finder.Catalog.Species, s => Assert.InRange(s.MaxTier, s.MinTier, 4));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]   // Super Rare -> Epic
    [InlineData(4, 3)]   // Ultra Rare -> Epic
    [InlineData(5, 4)]   // Mythic -> Legendary
    [InlineData(6, 4)]   // Legendary stays Legendary
    public void Seven_rarities_become_five(int oldTier, int newTier) => Assert.Equal(newTier, MonsterRulesUpgrade.TierFromVersion2(oldTier));

    [Fact]
    public async Task Monsters_from_the_old_rules_are_brought_up_to_date()
    {
        Player p = await NewPlayer();
        WalkingMonsterDto starter = Assert.Single(await Monsters(p));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == starter.Id);
            m.RarityTier = 5;          // a Mythic, from when there were seven
            m.RulesVersion = 2;
            m.Potential = 1;
            await db.SaveChangesAsync();

            Assert.True(MonsterRulesUpgrade.Run(db) >= 1);
        }

        WalkingMonsterDto after = Assert.Single(await Monsters(p));
        Assert.Equal(4, after.RarityTier);
        Assert.Equal(3, after.RulesVersion);
        Assert.Equal(MonsterPotential.For(after.Seed, 4), after.Potential);
    }

    // ------------------------------------------------------------------ the starter

    [Fact]
    public async Task A_new_walker_gets_one_starter_companion()
    {
        Player p = await NewPlayer();

        List<WalkingMonsterDto> first = await Monsters(p);
        List<WalkingMonsterDto> again = await Monsters(p);

        WalkingMonsterDto starter = Assert.Single(first);
        Assert.Single(again);
        Assert.Equal(starter.Id, again[0].Id);
        Assert.Equal("MON_0001", starter.SpeciesId);
        Assert.Equal(0, starter.RarityTier);
        Assert.Equal("starter", starter.Source);
        Assert.True(starter.Seen);
        Assert.Equal(MonsterPotential.For(starter.Seed, 0), starter.Potential);
    }

    [Fact]
    public async Task Serial_numbers_count_every_one_ever_found()
    {
        WalkingMonsterDto a = (await Monsters(await NewPlayer()))[0];
        WalkingMonsterDto b = (await Monsters(await NewPlayer()))[0];

        Assert.Equal(a.SpeciesId, b.SpeciesId);
        Assert.NotEqual(a.Serial, b.Serial);
        Assert.True(a.Serial >= 1 && b.Serial >= 1);
    }

    // ------------------------------------------------------------------ finding monsters by walking

    [Fact]
    public async Task Walking_5000_steps_is_a_chance_to_find_a_monster()
    {
        Player p = await NewPlayer();
        await Monsters(p); // the starter

        StepReportResult report = await Report(p, 5000, "coastal");

        Assert.Equal(1, report.NewFinds);
        List<WalkingMonsterDto> monsters = await Monsters(p);
        Assert.Equal(2, monsters.Count);
        WalkingMonsterDto found = monsters.Single(m => m.Source == "walk");
        Assert.False(found.Seen);
        Assert.Equal("coastal", found.BiomeId);
        Assert.InRange(found.RarityTier, 0, 4);
        Assert.Equal(MonsterPotential.For(found.Seed, found.RarityTier), found.Potential);
    }

    [Fact]
    public async Task Steps_carry_over_between_reports()
    {
        Player p = await NewPlayer();

        Assert.Equal(0, (await Report(p, 4000)).NewFinds);   // 4,000 carried
        Assert.Equal(1, (await Report(p, 6000)).NewFinds);   // +2,000 = 6,000 -> one chance (1,000 carried)
    }

    [Fact]
    public async Task There_is_never_more_than_one_find_a_day()
    {
        Player p = await NewPlayer();

        Assert.Equal(1, (await Report(p, 20000)).NewFinds);  // four chances, but only one find
        Assert.Equal(0, (await Report(p, 30000)).NewFinds);  // and nothing more today
        Assert.Single(await Monsters(p), m => m.Source == "walk");
    }

    [Fact]
    public async Task A_find_can_be_marked_as_seen_but_only_by_its_owner()
    {
        Player owner = await NewPlayer();
        Player someoneElse = await NewPlayer();
        await Report(owner, 5000);
        WalkingMonsterDto found = (await Monsters(owner)).Single(m => m.Source == "walk");

        Assert.Equal(HttpStatusCode.NotFound, (await someoneElse.Http.PostAsync($"/api/walking/monsters/{found.Id}/seen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PostAsync($"/api/walking/monsters/{found.Id}/seen", null)).StatusCode);
        Assert.True((await Monsters(owner)).Single(m => m.Id == found.Id).Seen);
    }

    [Fact]
    public async Task Walkers_only_see_their_own_monsters()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        await Report(a, 5000);

        List<WalkingMonsterDto> mine = await Monsters(b);
        Assert.Single(mine);                       // just b's own starter
        Assert.DoesNotContain(mine, m => m.Source == "walk");
    }
}
