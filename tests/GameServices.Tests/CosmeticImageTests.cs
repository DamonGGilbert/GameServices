using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

public class CosmeticImageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public CosmeticImageTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int prestige = 0, int level = 1)
    {
        HttpClient client = factory.CreateClient();
        string name = "c" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.Id == auth.User.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ShowPrestige, prestige).SetProperty(x => x.ShowLevel, level));

        return new Player(client, auth.User.Id, name);
    }

    // The first bytes of a PNG file are enough for the server's "is this really a picture" check.
    private static readonly byte[] SmallPicture = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };

    private static Task<HttpResponseMessage> Upload(Player who, string kind, byte[] bytes)
    {
        ByteArrayContent body = new(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return who.Http.PutAsync($"/api/profile/cosmetic-image/{kind}", body);
    }

    private static Task<HttpResponseMessage> View(Player viewer, Player owner, string kind)
        => viewer.Http.GetAsync($"/api/users/{owner.Name}/cosmetic-image/{kind}");

    [Fact]
    public async Task Custom_images_need_the_level_cap_or_a_prestige_rank()
    {
        Player newbie = await NewPlayer(prestige: 0, level: 12);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(newbie, "sleeve", SmallPicture)).StatusCode);

        Player maxed = await NewPlayer(prestige: 0, level: 50);
        Assert.Equal(HttpStatusCode.NoContent, (await Upload(maxed, "sleeve", SmallPicture)).StatusCode);

        Player prestiged = await NewPlayer(prestige: 1, level: 3);
        Assert.Equal(HttpStatusCode.NoContent, (await Upload(prestiged, "playmat", SmallPicture)).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_friends_and_guildmates_can_see_the_picture()
    {
        Player owner = await NewPlayer(prestige: 2);
        Player stranger = await NewPlayer();
        Player friend = await NewPlayer();
        Player guildmate = await NewPlayer();

        Assert.Equal(HttpStatusCode.NoContent, (await Upload(owner, "playmat", SmallPicture)).StatusCode);

        // The owner sees it; a stranger is told "not found".
        Assert.Equal(HttpStatusCode.OK, (await View(owner, owner, "playmat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await View(stranger, owner, "playmat")).StatusCode);

        // A friend can.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (Guid a, Guid b) = SocialEndpoints.Pair(owner.Id, friend.Id);
            db.Friendships.Add(new Friendship { UserAId = a, UserBId = b, RequesterId = owner.Id, Status = FriendshipStatus.Accepted, AcceptedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        HttpResponseMessage seen = await View(friend, owner, "playmat");
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(SmallPicture, await seen.Content.ReadAsByteArrayAsync());

        // A guildmate can.
        HttpResponseMessage made = await owner.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Pics " + Guid.NewGuid().ToString("N")[..8], "PIC", "", true));
        GuildSummary guild = (await made.Content.ReadFromJsonAsync<GuildSummary>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await guildmate.Http.PostAsync($"/api/guilds/{guild.Id}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await View(guildmate, owner, "playmat")).StatusCode);

        // Still hidden from the stranger, and a sleeve nobody uploaded is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await View(stranger, owner, "playmat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await View(friend, owner, "sleeve")).StatusCode);
    }

    [Fact]
    public async Task Bad_uploads_are_refused_and_pictures_can_be_removed()
    {
        Player owner = await NewPlayer(prestige: 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(owner, "sleeve", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(owner, "banner", SmallPicture)).StatusCode);

        byte[] huge = new byte[CosmeticImageEndpoints.MaxBytes + 10];
        SmallPicture.CopyTo(huge, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(owner, "sleeve", huge)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Upload(owner, "sleeve", SmallPicture)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await View(owner, owner, "sleeve")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync("/api/profile/cosmetic-image/sleeve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await View(owner, owner, "sleeve")).StatusCode);
    }

    [Fact]
    public async Task A_blocked_player_cannot_see_the_picture_even_as_a_guildmate()
    {
        Player owner = await NewPlayer(prestige: 1);
        Player other = await NewPlayer();

        Assert.Equal(HttpStatusCode.NoContent, (await Upload(owner, "playmat", SmallPicture)).StatusCode);

        HttpResponseMessage made = await owner.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Blk " + Guid.NewGuid().ToString("N")[..8], "BLK", "", true));
        GuildSummary guild = (await made.Content.ReadFromJsonAsync<GuildSummary>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await other.Http.PostAsync($"/api/guilds/{guild.Id}/join", null)).StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Blocks.Add(new UserBlock { BlockerId = owner.Id, BlockedId = other.Id });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await View(other, owner, "playmat")).StatusCode);
    }
}
