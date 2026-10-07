using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Chat;
using GameServices.Api.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameServices.Tests;

public class SocialTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public SocialTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name, string Token);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name, auth.AccessToken);
    }

    private static Task<HttpResponseMessage> Request(Player from, Player to)
        => from.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(to.Name));

    private static async Task<FriendsResponse> Friends(Player player)
        => (await player.Http.GetFromJsonAsync<FriendsResponse>("/api/friends"))!;

    private static async Task MakeFriends(Player a, Player b)
    {
        await Request(a, b);
        await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null);
    }

    // ------------------------------------------------------------------ friends

    [Fact]
    public async Task Social_features_require_sign_in()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/friends");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_can_be_sent_and_accepted()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        Assert.Equal(HttpStatusCode.OK, (await Request(alice, bob)).StatusCode);

        Assert.Single((await Friends(bob)).Incoming);
        Assert.Single((await Friends(alice)).Outgoing);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null)).StatusCode);

        FriendsResponse forAlice = await Friends(alice);
        Assert.Equal(bob.Name, Assert.Single(forAlice.Friends).Username);
        Assert.Empty(forAlice.Outgoing);
        Assert.Equal(alice.Name, Assert.Single((await Friends(bob)).Friends).Username);
    }

    [Fact]
    public async Task Sending_a_request_to_someone_who_asked_you_makes_you_friends()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        await Request(alice, bob);
        await Request(bob, alice);

        Assert.Single((await Friends(alice)).Friends);
        Assert.Single((await Friends(bob)).Friends);
    }

    [Fact]
    public async Task Both_players_requesting_at_the_same_moment_still_makes_one_friendship()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        HttpResponseMessage[] responses = await Task.WhenAll(Request(alice, bob), Request(bob, alice));

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);

        FriendsResponse forAlice = await Friends(alice);
        Assert.Equal(1, forAlice.Friends.Count + forAlice.Outgoing.Count + forAlice.Incoming.Count);
    }

    [Fact]
    public async Task You_cannot_add_yourself_or_a_stranger_who_does_not_exist()
    {
        Player alice = await NewPlayer();

        Assert.Equal(HttpStatusCode.BadRequest, (await Request(alice, alice)).StatusCode);

        HttpResponseMessage ghost = await alice.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest("nobody-here"));
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
    }

    [Fact]
    public async Task Duplicate_requests_are_refused()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        await Request(alice, bob);

        Assert.Equal(HttpStatusCode.Conflict, (await Request(alice, bob)).StatusCode);
    }

    [Fact]
    public async Task A_request_can_be_declined_cancelled_and_a_friend_removed()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player carol = await NewPlayer();

        await Request(alice, bob);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/friends/requests/{alice.Id}/decline", null)).StatusCode);
        Assert.Empty((await Friends(alice)).Outgoing);

        await Request(alice, carol);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/api/friends/requests/{carol.Id}")).StatusCode);
        Assert.Empty((await Friends(carol)).Incoming);

        await MakeFriends(alice, bob);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/api/friends/{bob.Id}")).StatusCode);
        Assert.Empty((await Friends(bob)).Friends);
    }

    [Fact]
    public async Task You_cannot_accept_a_request_that_was_not_sent_to_you()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player mallory = await NewPlayer();

        await Request(alice, bob);

        // Mallory tries to accept Alice's request to Bob on his behalf.
        HttpResponseMessage response = await mallory.Http.PostAsync($"/api/friends/requests/{alice.Id}/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await Friends(bob)).Friends);
    }

    [Fact]
    public async Task Friends_who_recently_used_the_game_show_as_online()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        await Friends(bob); // Bob's game talks to the server, so he counts as online.

        Assert.True(Assert.Single((await Friends(alice)).Friends).IsOnline);
    }

    // ------------------------------------------------------------------ blocking

    [Fact]
    public async Task Blocking_ends_the_friendship_and_hides_you_from_them()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        Assert.Equal(HttpStatusCode.OK, (await alice.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(bob.Name))).StatusCode);

        Assert.Empty((await Friends(alice)).Friends);
        Assert.Empty((await Friends(bob)).Friends);
        Assert.Equal(bob.Name, Assert.Single((await alice.Http.GetFromJsonAsync<List<BlockedDto>>("/api/blocks"))!).Username);

        // Bob can't tell he's blocked: it looks like Alice doesn't exist.
        HttpResponseMessage retry = await Request(bob, alice);
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
    }

    [Fact]
    public async Task Unblocking_lets_people_find_each_other_again()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();

        await alice.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(bob.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/api/blocks/{bob.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Request(bob, alice)).StatusCode);
    }

    // ------------------------------------------------------------------ chat

    [Fact]
    public async Task Friends_can_exchange_messages()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        HttpResponseMessage sent = await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest("  Ready to duel?  "));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        // Bob sees an unread badge, then reads it.
        Assert.Equal(1, Assert.Single((await Friends(bob)).Friends).Unread);

        List<ChatMessageDto> inbox = (await bob.Http.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/{alice.Id}"))!;
        ChatMessageDto message = Assert.Single(inbox);
        Assert.Equal("Ready to duel?", message.Text);
        Assert.Equal(alice.Id, message.FromUserId);

        Assert.Equal(0, Assert.Single((await Friends(bob)).Friends).Unread);

        // Polling for only the new ones.
        await bob.Http.PostAsJsonAsync($"/api/chat/{alice.Id}", new SendMessageRequest("Always!"));
        List<ChatMessageDto> newer = (await alice.Http.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/{bob.Id}?afterId={message.Id}"))!;
        Assert.Equal("Always!", Assert.Single(newer).Text);
    }

    [Fact]
    public async Task Strangers_cannot_message_or_read_each_other()
    {
        Player alice = await NewPlayer();
        Player stranger = await NewPlayer();

        HttpResponseMessage send = await alice.Http.PostAsJsonAsync($"/api/chat/{stranger.Id}", new SendMessageRequest("hi"));
        HttpResponseMessage read = await alice.Http.GetAsync($"/api/chat/{stranger.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task A_third_player_cannot_read_a_private_chat()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player eve = await NewPlayer();
        await MakeFriends(alice, bob);
        await MakeFriends(eve, alice);

        await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest("secret plan"));

        // Eve is Alice's friend too, but her chat with Alice is a different conversation.
        List<ChatMessageDto> evesView = (await eve.Http.GetFromJsonAsync<List<ChatMessageDto>>($"/api/chat/{alice.Id}"))!;
        Assert.Empty(evesView);
    }

    [Fact]
    public async Task Blocked_players_cannot_message()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        await alice.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(bob.Name));

        HttpResponseMessage response = await bob.Http.PostAsJsonAsync($"/api/chat/{alice.Id}", new SendMessageRequest("hello?"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_messages_are_rejected(string text)
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        HttpResponseMessage response = await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest(text));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_messages_are_rejected()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        HttpResponseMessage response = await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest(new string('x', 501)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spamming_messages_is_slowed_down()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        List<HttpStatusCode> results = new();

        for (int i = 0; i < 14; i++)
        {
            results.Add((await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest("spam " + i))).StatusCode);
        }

        Assert.Equal(10, results.Count(r => r == HttpStatusCode.OK));
        Assert.Equal(4, results.Count(r => r == HttpStatusCode.TooManyRequests));
    }

    // ------------------------------------------------------------------ real time

    [Fact]
    public async Task A_connected_friend_receives_the_message_instantly()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        TaskCompletionSource<ChatMessageDto> received = new();

        // Bob's game opens a live connection to the chat hub.
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(bob.Token);
            })
            .Build();

        connection.On<ChatMessageDto>("ReceiveMessage", message => received.TrySetResult(message));
        await connection.StartAsync();

        await alice.Http.PostAsJsonAsync($"/api/chat/{bob.Id}", new SendMessageRequest("live!"));

        ChatMessageDto pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("live!", pushed.Text);
        Assert.Equal(alice.Id, pushed.FromUserId);
    }

    [Fact]
    public async Task The_chat_hub_refuses_anonymous_connections()
    {
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }
}
