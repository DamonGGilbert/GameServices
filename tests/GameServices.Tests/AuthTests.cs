using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;

namespace GameServices.Tests;

/// <summary>
/// Integration tests: each one sends real HTTP requests to the real server code
/// and checks the answers. If a future change breaks sign-up or sign-in, these fail.
/// </summary>
public class AuthTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient client;

    public AuthTests(ApiFactory factory)
    {
        client = factory.CreateClient();
    }

    private static string UniqueName() => "p" + Guid.NewGuid().ToString("N")[..10];

    private Task<HttpResponseMessage> Register(string username, string password = "CorrectHorse1", string? email = null)
        => client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, password, email));

    private Task<HttpResponseMessage> Login(string username, string password)
        => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

    [Fact]
    public async Task Health_endpoint_says_healthy()
    {
        HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_creates_an_account_and_returns_tokens()
    {
        string name = UniqueName();

        HttpResponseMessage response = await Register(name);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        AuthResponse? body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.Equal(name, body.User.Username);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_username_even_in_different_case()
    {
        string name = UniqueName();
        await Register(name);

        HttpResponseMessage duplicate = await Register(name.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Registering_an_email_that_already_has_an_account_says_to_sign_in()
    {
        string email = UniqueName().ToLowerInvariant() + "@example.com";
        await Register(UniqueName(), email: email);

        HttpResponseMessage again = await Register(UniqueName(), email: email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        string body = await again.Content.ReadAsStringAsync();
        Assert.Contains("You already have an account. Sign in instead.", body);
        Assert.Contains("email_already_registered", body);
    }

    [Fact]
    public async Task You_can_sign_in_with_your_email_instead_of_your_username()
    {
        string name = UniqueName();
        string email = name.ToLowerInvariant() + "@example.com";
        await Register(name, email: email);

        HttpResponseMessage response = await Login(email, "CorrectHorse1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(email, "WrongPassword9")).StatusCode);
    }

    [Theory]
    [InlineData("ab", "CorrectHorse1")]          // username too short
    [InlineData("has spaces", "CorrectHorse1")]  // bad characters
    [InlineData("validname", "short")]           // password too short
    public async Task Register_rejects_invalid_input(string username, string password)
    {
        HttpResponseMessage response = await Register(username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_a_bad_email()
    {
        HttpResponseMessage response = await Register(UniqueName(), email: "not-an-email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_works_with_the_right_password_and_is_case_insensitive_on_username()
    {
        string name = UniqueName();
        await Register(name, "CorrectHorse1");

        HttpResponseMessage response = await Login(name.ToUpperInvariant(), "CorrectHorse1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_fails_with_the_same_message_for_wrong_password_and_unknown_user()
    {
        string name = UniqueName();
        await Register(name, "CorrectHorse1");

        HttpResponseMessage wrongPassword = await Login(name, "WrongPassword9");
        HttpResponseMessage unknownUser = await Login(UniqueName(), "WrongPassword9");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        // The messages must be identical, so attackers can't discover which usernames exist.
        // (Compare only the "detail" text -- each response also carries a unique trace id.)
        string Detail(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString()!;

        Assert.Equal(
            Detail(await wrongPassword.Content.ReadAsStringAsync()),
            Detail(await unknownUser.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Me_requires_a_token()
    {
        HttpResponseMessage response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_signed_in_player()
    {
        string name = UniqueName();
        AuthResponse auth = (await (await Register(name)).Content.ReadFromJsonAsync<AuthResponse>())!;

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UserDto? me = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal(name, me!.Username);
    }

    [Fact]
    public async Task Me_rejects_a_forged_token()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.realtoken");

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_gives_new_tokens_and_the_old_refresh_token_stops_working()
    {
        AuthResponse first = (await (await Register(UniqueName())).Content.ReadFromJsonAsync<AuthResponse>())!;

        HttpResponseMessage refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        AuthResponse second = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Rotation: the first refresh token was single-use.
        HttpResponseMessage reused = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_signs_the_account_out_everywhere()
    {
        AuthResponse first = (await (await Register(UniqueName())).Content.ReadFromJsonAsync<AuthResponse>())!;

        AuthResponse second = (await (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        // Someone replays the OLD token (a thief?) ...
        await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));

        // ... so even the legitimate NEW token is now revoked.
        HttpResponseMessage legit = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, legit.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        AuthResponse auth = (await (await Register(UniqueName())).Content.ReadFromJsonAsync<AuthResponse>())!;

        HttpResponseMessage logout = await client.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        HttpResponseMessage refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }
}

public class RateLimitTests : IClassFixture<StrictRateLimitFactory>
{
    private readonly HttpClient client;

    public RateLimitTests(StrictRateLimitFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Too_many_login_attempts_get_429()
    {
        HttpStatusCode last = HttpStatusCode.OK;

        for (int i = 0; i < 6; i++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody", "WrongPassword9"));
            last = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
