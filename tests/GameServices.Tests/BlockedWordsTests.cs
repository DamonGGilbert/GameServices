using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Moderation;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// THE LIST OF BLOCKED WORDS lives in the database: filled the first time the server starts, changed without a new version
/// of the server. It catches single words and combinations of words that are only a problem together.
/// </summary>
public class BlockedWordsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public BlockedWordsTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<T> InDb<T>(Func<AppDbContext, Task<T>> work)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static WordFilter Seeded() =>
        new(BlockedWordSeed.Of(BlockedWordKind.Anywhere), BlockedWordSeed.Of(BlockedWordKind.Whole), BlockedWordSeed.Of(BlockedWordKind.Allowed), BlockedWordSeed.Of(BlockedWordKind.Combination));

    // ------------------------------------------------------------------ combinations

    [Theory]
    [InlineData("kill yourself")]
    [InlineData("KILL YOURSELF!!")]
    [InlineData("kill   yourself")]
    [InlineData("k1ll y0urself")]
    [InlineData("just go and die")]
    [InlineData("send nudes")]
    [InlineData("where do you live")]
    [InlineData("what school do you go to")]
    [InlineData("whats your phone number")]
    [InlineData("add me on snap")]
    [InlineData("don't tell your mum")]
    [InlineData("killyourself")]
    public void Combinations_are_blocked(string text)
    {
        Assert.False(Seeded().IsClean(text), text);
    }

    [Theory]
    [InlineData("kill the wild monster")]
    [InlineData("you can do it yourself")]
    [InlineData("go on then")]
    [InlineData("nice school uniform")]
    [InlineData("the live show")]
    [InlineData("send me a trade")]
    [InlineData("my monster is number one")]
    public void The_same_words_on_their_own_are_fine(string text)
    {
        Assert.True(Seeded().IsClean(text), text);
    }

    [Fact]
    public void Censoring_a_combination_stars_it_out_and_keeps_the_rest()
    {
        string censored = Seeded().Censor("hey kill yourself ok");

        Assert.Contains("hey", censored);
        Assert.Contains("ok", censored);
        Assert.DoesNotContain("kill", censored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yourself", censored, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("KillYourself99")]
    [InlineData("send_nudes")]
    public void A_name_with_a_combination_is_refused(string name)
    {
        Assert.False(Seeded().IsCleanName(name), name);
    }

    // ------------------------------------------------------------------ the list in the database

    [Fact]
    public async Task The_list_is_filled_the_first_time_and_the_filter_is_built_from_it()
    {
        WordFilter filter = await InDb(async db =>
        {
            await BlockedWords.SeedIfEmptyAsync(db);
            Assert.True(await db.BlockedWords.AnyAsync(w => w.Kind == BlockedWordKind.Combination));
            return await BlockedWords.LoadAsync(db);
        });

        Assert.False(filter.IsClean("fuck"));
        Assert.False(filter.IsClean("send nudes"));
        Assert.True(filter.IsClean("Scunthorpe"));
    }

    [Fact]
    public async Task Seeding_again_doesnt_bring_back_a_word_you_took_off()
    {
        await InDb(async db =>
        {
            await BlockedWords.SeedIfEmptyAsync(db);
            await BlockedWords.AddAsync(db, "seedtestword", BlockedWordKind.Anywhere);
            await BlockedWords.RemoveAsync(db, "seedtestword");
            await BlockedWords.SeedIfEmptyAsync(db);
            Assert.False(await db.BlockedWords.AnyAsync(w => w.Text == "seedtestword"));
            return true;
        });
    }

    [Fact]
    public async Task Adding_a_word_blocks_it_and_removing_it_lets_it_through()
    {
        await InDb(async db =>
        {
            Assert.True((await BlockedWords.LoadAsync(db)).IsClean("flibbertigibbet"));

            Assert.True(await BlockedWords.AddAsync(db, "  FlibbertiGibbet ", BlockedWordKind.Anywhere));
            Assert.False((await BlockedWords.LoadAsync(db)).IsClean("flibbertigibbet"));
            Assert.False(await BlockedWords.AddAsync(db, "flibbertigibbet", BlockedWordKind.Anywhere));   // already there

            Assert.Equal(1, await BlockedWords.RemoveAsync(db, "flibbertigibbet"));
            Assert.True((await BlockedWords.LoadAsync(db)).IsClean("flibbertigibbet"));
            return true;
        });
    }

    [Fact]
    public async Task Adding_a_combination_blocks_only_the_words_together()
    {
        await InDb(async db =>
        {
            Assert.True(await BlockedWords.AddAsync(db, "purple wombats", BlockedWordKind.Combination));
            Assert.False(await BlockedWords.AddAsync(db, "wombats", BlockedWordKind.Combination));   // one word isn't a combination

            WordFilter filter = await BlockedWords.LoadAsync(db);
            Assert.False(filter.IsClean("I love purple wombats"));
            Assert.True(filter.IsClean("purple is nice"));
            Assert.True(filter.IsClean("wombats are nice"));

            await BlockedWords.RemoveAsync(db, "purple wombats");
            return true;
        });
    }

    [Fact]
    public async Task An_innocent_word_can_be_let_through()
    {
        await InDb(async db =>
        {
            await BlockedWords.AddAsync(db, "gribble", BlockedWordKind.Anywhere);
            Assert.False((await BlockedWords.LoadAsync(db)).IsClean("gribbleton"));

            await BlockedWords.AddAsync(db, "gribbleton", BlockedWordKind.Allowed);
            WordFilter filter = await BlockedWords.LoadAsync(db);
            Assert.True(filter.IsClean("gribbleton"));
            Assert.False(filter.IsClean("gribble"));

            await BlockedWords.RemoveAsync(db, "gribble");
            await BlockedWords.RemoveAsync(db, "gribbleton");
            return true;
        });
    }

    // ------------------------------------------------------------------ in the game

    [Fact]
    public async Task A_rude_name_for_a_monster_is_refused_by_the_server()
    {
        HttpClient client = factory.CreateClient();
        string name = "w" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        Guid monster = (await client.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;
        await InDb(async db =>
        {
            WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, auth.User.Id, CancellationToken.None);
            profile.NameTags = 2;
            await db.SaveChangesAsync();
            return true;
        });

        var rude = await client.PostAsJsonAsync("/api/walking/monsters/" + monster + "/name", new { Name = "KillYourself" });
        Assert.False(rude.IsSuccessStatusCode);

        var fine = await client.PostAsJsonAsync("/api/walking/monsters/" + monster + "/name", new { Name = "Biscuit" });
        Assert.True(fine.IsSuccessStatusCode);
    }
}
