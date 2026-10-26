using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// WALK WITH FRIENDS: two friends walking at the same time and place get +25% Glimmer on those steps - only when both
/// switched it on, only friends, only while both are actually walking, and only up to a daily cap.
/// </summary>
public class WalkTogetherTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkTogetherTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private const double Lat = 53.4200, Lon = -3.0400;   // anywhere

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "t" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id, name);
    }

    private static async Task MakeFriends(Player a, Player b)
    {
        await a.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(b.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null)).StatusCode);
    }

    // A time zone where it's about midday now, so thousands of steps are believable.
    private static (string Day, int Offset) Midday()
    {
        DateTime now = DateTime.UtcNow;
        int offset = (int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15;
        offset = Math.Clamp(offset, -720, 720);
        return (now.AddMinutes(offset).ToString("yyyy-MM-dd"), offset);
    }

    private async Task<StepReportResult> Report(Player p, int stepsToday, bool share = true, double lat = Lat, double lon = Lon)
    {
        await RewindLastReport(p);
        (string day, int offset) = Midday();
        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(day, stepsToday, offset, null, share, lat, lon));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StepReportResult>())!;
    }

    // Pretends the last report was half an hour ago: no 15-second report limit, and time to have walked the next steps.
    // ("Walking together" is judged by the server's own clock, so this doesn't affect it.)
    private async Task RewindLastReport(Player p)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        WalkingProfile? profile = await db.WalkingProfiles.FirstOrDefaultAsync(x => x.UserId == p.Id);
        if (profile?.LastReportUtc == null) return;
        profile.LastReportUtc = profile.LastReportUtc.Value.AddMinutes(-30);
        foreach (WalkingDay day in db.WalkingDays.Where(d => d.UserId == p.Id)) day.UpdatedUtc = day.UpdatedUtc.AddMinutes(-30);
        await db.SaveChangesAsync();
    }

    private static async Task<int> Glimmer(Player p) => (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer;

    [Fact]
    public async Task Friends_walking_together_earn_a_quarter_more_Glimmer()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await MakeFriends(alice, bob);

        StepReportResult first = await Report(alice, 1000);
        Assert.Empty(first.WalkingWith!);   // Bob isn't out yet

        StepReportResult bobs = await Report(bob, 1000, lat: Lat + 0.0004);   // ~40 m away
        Assert.Equal(new[] { alice.Name }, bobs.WalkingWith);
        Assert.Equal(2, bobs.TogetherBonus);   // 1,000 steps together: 2 bonus Glimmer (1 per 400)
        Assert.Equal(1, bobs.TogetherDays);
        Assert.Equal(10 + 2, await Glimmer(bob));   // 10 for walking + 2 for walking together

        StepReportResult again = await Report(alice, 2000);
        Assert.Equal(new[] { bob.Name }, again.WalkingWith);
        Assert.Equal(2, again.TogetherBonus);
    }

    [Fact]
    public async Task Only_friends_count()
    {
        Player alice = await NewPlayer(), stranger = await NewPlayer();
        await Report(alice, 1000);
        StepReportResult r = await Report(stranger, 1000);
        Assert.Empty(r.WalkingWith!);
        Assert.Equal(0, r.TogetherBonus);
    }

    [Fact]
    public async Task Nobody_is_matched_unless_both_switched_it_on()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await MakeFriends(alice, bob);

        await Report(alice, 1000, share: false);
        Assert.Empty((await Report(bob, 1000)).WalkingWith!);

        await Report(alice, 2000);
        Assert.Equal(new[] { alice.Name }, (await Report(bob, 2000)).WalkingWith);
    }

    [Fact]
    public async Task Standing_still_or_far_away_isnt_walking_together()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await MakeFriends(alice, bob);

        await Report(alice, 1000);
        await Report(alice, 1000);   // no new steps: Alice has stopped
        Assert.Empty((await Report(bob, 1000)).WalkingWith!);

        await Report(alice, 2000);
        Assert.Empty((await Report(bob, 2000, lat: Lat + 0.01)).WalkingWith!);   // ~1 km away
    }

    [Fact]
    public async Task The_bonus_has_a_daily_cap()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Report(alice, 1000);

        // A very long walk together: 1 bonus per 400 steps, but never more than the cap in a day.
        int total = 0;
        for (int steps = 5000; steps <= 30000; steps += 5000)
        {
            await Report(alice, steps);
            total += (await Report(bob, steps)).TogetherBonus;
        }

        Assert.Equal(WalkTogether.DailyBonusCap, total);
    }

    [Fact]
    public void Neighbouring_cells_are_about_a_hundred_metres_apart()
    {
        (int x, int y) = WalkTogether.Cell(Lat, Lon);
        (int x2, int y2) = WalkTogether.Cell(Lat + 0.0011, Lon);
        Assert.Equal(x, x2);
        Assert.Equal(y + 1, y2);
    }
}
