using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// A PROFILE'S COLOUR AND MOTTO: picked from set lists, never typed, so nothing rude or private can get in - and everyone
/// sees them.
/// </summary>
public class ProfileStyleTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ProfileStyleTests(ApiFactory factory)
    {
        this.factory = factory;
        _ = factory.Services;   // the server has started, so its rules are loaded
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null, 1996, 3)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.GetAsync("/api/walking/monsters");
        return new Player(client, auth.User.Id, name);
    }

    private async Task<(int Colour, int Motto)> StyleOf(Player p)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        WalkingProfile profile = await db.WalkingProfiles.AsNoTracking().FirstAsync(x => x.UserId == p.Id);
        return (profile.ProfileColour, profile.Motto);
    }

    private static Task Choose(Player p, int? colour, int? motto) =>
        p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, null, null, null, colour, motto));

    [Fact]
    public void The_mottos_came_across_from_the_game_and_none_is_empty()
    {
        MonsterCatalog.ProgressionEntry rules = MonsterCatalog.Shared.Progression;
        Assert.Equal(8, rules.ProfileColours);
        Assert.Equal(16, rules.Mottos.Count);
        Assert.Equal("Always exploring!", rules.Mottos[0]);
        Assert.All(rules.Mottos, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(rules.Mottos.Count, rules.Mottos.Distinct().Count());
    }

    [Fact]
    public async Task A_new_walker_has_no_colour_or_motto()
    {
        Player p = await NewPlayer();
        Assert.Equal((-1, -1), await StyleOf(p));
    }

    [Fact]
    public async Task Only_the_set_colours_and_mottos_are_taken()
    {
        Player p = await NewPlayer();

        await Choose(p, 3, 5);
        Assert.Equal((3, 5), await StyleOf(p));

        await Choose(p, 99, 999);
        Assert.Equal((3, 5), await StyleOf(p));   // not one of them: what they had stays

        await Choose(p, -7, -2);
        Assert.Equal((3, 5), await StyleOf(p));

        await Choose(p, null, null);
        Assert.Equal((3, 5), await StyleOf(p));   // an older game that doesn't send them changes nothing

        await Choose(p, -1, -1);
        Assert.Equal((-1, -1), await StyleOf(p));
    }

    [Fact]
    public async Task Everyone_sees_them_on_a_profile_and_in_Players()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        await Choose(p, 6, 1);

        PublicProfileDto seen = (await other.Http.GetFromJsonAsync<PublicProfileDto>("/api/walking/players/" + p.Id))!;
        Assert.Equal(6, seen.Colour);
        Assert.Equal("Every step counts.", seen.Motto);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LastReportUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        ExplorerListDto list = (await other.Http.GetFromJsonAsync<ExplorerListDto>("/api/walking/explorers?view=active"))!;
        ExplorerDto them = Assert.Single(list.Explorers, e => e.UserId == p.Id);
        Assert.Equal(6, them.Colour);
        Assert.Equal("Every step counts.", them.Motto);
    }

    [Fact]
    public async Task No_motto_shows_as_nothing()
    {
        Player p = await NewPlayer(), other = await NewPlayer();

        PublicProfileDto seen = (await other.Http.GetFromJsonAsync<PublicProfileDto>("/api/walking/players/" + p.Id))!;

        Assert.Equal(-1, seen.Colour);
        Assert.Equal("", seen.Motto);
    }
}
