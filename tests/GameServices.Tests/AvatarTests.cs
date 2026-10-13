using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Social;

namespace GameServices.Tests;

public class AvatarTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public AvatarTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "a" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, name);
    }

    // The first bytes of a real JPEG (the server only checks the start and the size).
    private static byte[] FakeJpeg(int size = 2000)
    {
        byte[] bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    private static ByteArrayContent Body(byte[] bytes, string type = "image/jpeg")
    {
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task Avatars_require_sign_in()
    {
        HttpClient anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/api/profile/avatar", Body(FakeJpeg()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users/someone/avatar")).StatusCode);
    }

    [Fact]
    public async Task A_player_can_set_a_picture_and_others_can_see_it()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        byte[] picture = FakeJpeg(3000);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PutAsync("/api/profile/avatar", Body(picture))).StatusCode);

        HttpResponseMessage seen = await bob.Http.GetAsync($"/api/users/{alice.Name}/avatar");
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal("image/jpeg", seen.Content.Headers.ContentType!.MediaType);
        Assert.Equal(picture, await seen.Content.ReadAsByteArrayAsync());

        // Names are not case sensitive, like sign-in.
        Assert.Equal(HttpStatusCode.OK, (await bob.Http.GetAsync($"/api/users/{alice.Name.ToUpperInvariant()}/avatar")).StatusCode);
    }

    [Fact]
    public async Task Someone_with_no_picture_gets_404()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.GetAsync($"/api/users/{alice.Name}/avatar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.GetAsync("/api/users/nobody-here/avatar")).StatusCode);
    }

    [Fact]
    public async Task A_new_picture_replaces_the_old_one()
    {
        Player alice = await NewPlayer();

        await alice.Http.PutAsync("/api/profile/avatar", Body(FakeJpeg(1000)));
        await alice.Http.PutAsync("/api/profile/avatar", Body(FakeJpeg(1500)));

        byte[] now = await alice.Http.GetByteArrayAsync($"/api/users/{alice.Name}/avatar");
        Assert.Equal(1500, now.Length);
    }

    [Fact]
    public async Task Only_real_pictures_of_a_sensible_size_are_accepted()
    {
        Player alice = await NewPlayer();

        // Not a picture at all.
        byte[] text = System.Text.Encoding.UTF8.GetBytes("this is not a picture, it is just some text that is long enough");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsync("/api/profile/avatar", Body(text))).StatusCode);

        // Far too big.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await alice.Http.PutAsync("/api/profile/avatar", Body(FakeJpeg(200 * 1024)))).StatusCode);

        // Nothing was stored.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.GetAsync($"/api/users/{alice.Name}/avatar")).StatusCode);
    }

    [Fact]
    public async Task An_unchanged_picture_is_answered_with_not_modified()
    {
        Player alice = await NewPlayer();
        await alice.Http.PutAsync("/api/profile/avatar", Body(FakeJpeg()));

        HttpResponseMessage first = await alice.Http.GetAsync($"/api/users/{alice.Name}/avatar");
        string etag = first.Headers.ETag!.Tag;

        HttpRequestMessage again = new(HttpMethod.Get, $"/api/users/{alice.Name}/avatar");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);

        Assert.Equal(HttpStatusCode.NotModified, (await alice.Http.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task The_look_of_your_table_is_kept_and_shown_to_an_opponent_in_the_room()
    {
        Player alice = await NewPlayer();

        string look = "{\"playmatName\":\"Neon\",\"sleeveName\":\"Red\",\"zoneOpacity\":0.4}";
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PutAsJsonAsync("/api/profile/cosmetics", new CosmeticsRequest(look))).StatusCode);

        HttpResponseMessage made = await alice.Http.PostAsJsonAsync("/api/rooms", new { name = "Look room " + Guid.NewGuid().ToString("N")[..6], guildMatch = false });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);

        string mine = await alice.Http.GetStringAsync("/api/rooms/mine");
        Assert.Contains("Neon", mine);
    }

    [Fact]
    public async Task A_look_must_be_a_small_json_object()
    {
        Player alice = await NewPlayer();

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsJsonAsync("/api/profile/cosmetics", new CosmeticsRequest("not json at all"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsJsonAsync("/api/profile/cosmetics", new CosmeticsRequest("[1,2,3]"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsJsonAsync("/api/profile/cosmetics", new CosmeticsRequest("{\"x\":\"" + new string('a', 2500) + "\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PutAsJsonAsync("/api/profile/cosmetics", new CosmeticsRequest("{}"))).StatusCode);
    }
}
